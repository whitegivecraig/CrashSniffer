using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件快照存档：每次崩溃把前后窗口的读数序列落盘为独立 JSON 文件。
/// 存储路径：%LOCALAPPDATA%\CrashSniffer\hardware-snapshots\
/// 最多保留 100 份，超出淘汰最旧（按 CrashTime）。
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
    };

    /// <summary>快照文件命名：{EventId}.json</summary>
    private static string FilePathFor(string eventId)
        => Path.Combine(StorageDir, $"{SanitizeFileName(eventId)}.json");

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

    /// <summary>列出所有快照的元数据（不含读数详情），按 CrashTime 倒序</summary>
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
