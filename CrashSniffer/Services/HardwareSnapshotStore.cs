using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件快照存档：每次崩溃把前后窗口的读数序列落盘为独立 JSON 文件。
/// 存储路径：%LOCALAPPDATA%\CrashSniffer\hardware-snapshots\
/// 最多保留 100 份，超出淘汰最旧（按文件写入时间）。
/// 原子写入（.tmp + File.Move），损坏自动备份，失败不阻断。
/// </summary>
public static class HardwareSnapshotStore
{
    /// <summary>快照保留上限（可被测试覆盖）</summary>
    public static int MaxSnapshots { get; set; } = 100;

    /// <summary>默认存储目录</summary>
    public static string DefaultStorageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrashSniffer", "hardware-snapshots");

    /// <summary>测试用目录覆盖</summary>
    internal static string? StorageDirOverride { get; set; }

    private static string StorageDir => StorageDirOverride ?? DefaultStorageDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object IoLock = new();

    private const string EventIdPrefix = "crash_";

    /// <summary>快照文件命名：{EventId}.json</summary>
    private static string FilePathFor(string eventId)
        => Path.Combine(StorageDir, $"{SanitizeFileName(eventId)}.json");

    /// <summary>生成崩溃快照的 EventId（时间精确到秒，ticks 后缀保证唯一）</summary>
    public static string BuildEventId(DateTime crashTime)
        => $"{EventIdPrefix}{crashTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}_{crashTime.Ticks % 1000000:D6}";

    /// <summary>从 EventId 解析崩溃时间（仅适用于 BuildEventId 生成的格式）</summary>
    public static bool TryParseCrashTime(string eventId, out DateTime crashTime)
    {
        crashTime = default;
        if (!eventId.StartsWith(EventIdPrefix, StringComparison.Ordinal)) return false;
        if (eventId.Length < EventIdPrefix.Length + 15) return false;
        return DateTime.TryParseExact(
            eventId.Substring(EventIdPrefix.Length, 15),
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out crashTime);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>保存一份崩溃快照</summary>
    public static void Save(HardwareSnapshot snapshot)
    {
        try
        {
            lock (IoLock)
            {
                Directory.CreateDirectory(StorageDir);
                string path = FilePathFor(snapshot.EventId);
                string json = JsonSerializer.Serialize(snapshot, JsonOpts);

                // 原子写入：先写 .tmp 再 Move 覆盖
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);

                // 上限淘汰
                EnforceLimit();
            }
        }
        catch
        {
            // 存档失败不阻断主流程
        }
    }

    /// <summary>按 EventId 加载快照，不存在返回 null</summary>
    public static HardwareSnapshot? Load(string eventId)
    {
        try
        {
            string path = FilePathFor(eventId);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            var snap = JsonSerializer.Deserialize<HardwareSnapshot>(json, JsonOpts);
            if (snap != null && snap.Readings == null) snap.Readings = new();
            return snap;
        }
        catch
        {
            // 损坏：备份后返回 null
            try
            {
                string path = FilePathFor(eventId);
                if (File.Exists(path))
                {
                    string bak = path + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(path, bak);
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>按崩溃时间查找快照（默认 2 分钟容差，取时间最接近的一份）</summary>
    public static HardwareSnapshot? FindByCrashTime(DateTime crashTime, double toleranceMinutes = 2)
    {
        try
        {
            string? bestId = null;
            double bestDelta = double.MaxValue;
            foreach (string id in ListEventIds())
            {
                if (!TryParseCrashTime(id, out DateTime t)) continue;
                double delta = Math.Abs((t - crashTime).TotalMinutes);
                if (delta < toleranceMinutes && delta < bestDelta)
                {
                    bestDelta = delta;
                    bestId = id;
                }
            }
            return bestId == null ? null : Load(bestId);
        }
        catch { return null; }
    }

    /// <summary>列出全部快照 EventId（只读文件名，不解析 JSON，用于按时间快速匹配）</summary>
    public static List<string> ListEventIds()
    {
        var ids = new List<string>();
        try
        {
            if (!Directory.Exists(StorageDir)) return ids;
            foreach (string file in Directory.GetFiles(StorageDir, "*.json"))
                ids.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch { }
        return ids;
    }

    /// <summary>列出所有快照（含读数详情，供报告导出），按 CrashTime 倒序</summary>
    public static List<HardwareSnapshot> ListSnapshots()
    {
        var list = new List<HardwareSnapshot>();
        try
        {
            if (!Directory.Exists(StorageDir)) return list;
            foreach (string file in Directory.GetFiles(StorageDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var snap = JsonSerializer.Deserialize<HardwareSnapshot>(json, JsonOpts);
                    if (snap != null)
                    {
                        if (snap.Readings == null) snap.Readings = new();
                        list.Add(snap);
                    }
                }
                catch { /* 跳过损坏文件 */ }
            }
        }
        catch { }
        return list.OrderByDescending(s => s.CrashTime).ToList();
    }

    /// <summary>超出上限时删除最旧的快照文件</summary>
    private static void EnforceLimit()
    {
        try
        {
            var files = Directory.GetFiles(StorageDir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTime)
                .ToList();

            while (files.Count > MaxSnapshots)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                try { File.Delete(oldest.FullName); } catch { }
            }
        }
        catch { }
    }
}
