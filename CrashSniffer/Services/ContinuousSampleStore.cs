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
/// 连续采样存档：后台采样器按 10 秒批量追加读数，按小时滚动为 JSONL 文件。
/// 崩溃重启后，主程序用最近 48 小时的采样补出"崩溃前窗口"，解决内存缓冲随重启丢失的问题。
/// 存储路径：%LOCALAPPDATA%\CrashSniffer\continuous-samples\
/// </summary>
public static class ContinuousSampleStore
{
    /// <summary>保留的采样文件数（每小时一个，默认 48 个 ≈ 48 小时）</summary>
    public static int MaxFiles { get; set; } = 48;

    public static string DefaultStorageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrashSniffer", "continuous-samples");

    internal static string? StorageDirOverride { get; set; }

    private static string StorageDir => StorageDirOverride ?? DefaultStorageDir;

    private static readonly object IoLock = new();

    private const string FilePrefix = "samples_";
    private const string FileExtension = ".jsonl";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>追加一批读数（内部按小时分文件）</summary>
    public static void Append(IEnumerable<HardwareReading> readings)
    {
        var list = readings.ToList();
        if (list.Count == 0) return;

        try
        {
            lock (IoLock)
            {
                Directory.CreateDirectory(StorageDir);
                foreach (var group in list.GroupBy(r => r.Timestamp.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture)))
                {
                    string path = Path.Combine(StorageDir, $"{FilePrefix}{group.Key}{FileExtension}");
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                    foreach (var r in group.OrderBy(x => x.Timestamp))
                        sw.WriteLine(JsonSerializer.Serialize(r, JsonOpts));
                }
                EnforceLimit();
            }
        }
        catch { /* 采样写盘失败不影响调用方 */ }
    }

    /// <summary>读取时间范围内的读数（升序）</summary>
    public static List<HardwareReading> LoadRange(DateTime from, DateTime to)
    {
        var result = new List<HardwareReading>();
        try
        {
            if (!Directory.Exists(StorageDir)) return result;

            lock (IoLock)
            {
                foreach (string file in Directory.GetFiles(StorageDir, $"{FilePrefix}*{FileExtension}"))
                {
                    if (!TryParseFileHour(Path.GetFileNameWithoutExtension(file), out DateTime hour)) continue;
                    if (hour.AddHours(1) < from || hour > to) continue;

                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    while (sr.ReadLine() is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var r = JsonSerializer.Deserialize<HardwareReading>(line, JsonOpts);
                            if (r != null && r.Timestamp >= from && r.Timestamp <= to)
                                result.Add(r);
                        }
                        catch { /* 跳过损坏行（可能为正在写入的半行） */ }
                    }
                }
            }
        }
        catch { }
        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    /// <summary>用连续采样构造某次崩溃的前后窗口快照；无数据返回 null</summary>
    public static HardwareSnapshot? BuildSnapshotForCrash(DateTime crashTime)
    {
        var readings = LoadRange(crashTime.AddMinutes(-5), crashTime.AddMinutes(1));
        if (readings.Count == 0) return null;

        return new HardwareSnapshot
        {
            EventId = $"continuous_{crashTime:yyyyMMdd_HHmmss}",
            CrashTime = crashTime,
            CrashType = "连续采样",
            Readings = readings,
        };
    }

    /// <summary>最新一次采样的落盘时间（本地时间），无采样返回 null</summary>
    public static DateTime? GetLatestSampleTime()
    {
        try
        {
            if (!Directory.Exists(StorageDir)) return null;
            var newest = Directory.GetFiles(StorageDir, $"{FilePrefix}*{FileExtension}")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest?.LastWriteTime;
        }
        catch { return null; }
    }

    /// <summary>按文件名解析采样文件所属小时</summary>
    internal static bool TryParseFileHour(string fileNameWithoutExtension, out DateTime hour)
    {
        hour = default;
        if (!fileNameWithoutExtension.StartsWith(FilePrefix, StringComparison.Ordinal)) return false;
        string s = fileNameWithoutExtension.Substring(FilePrefix.Length);
        return DateTime.TryParseExact(s, "yyyyMMdd_HH", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out hour);
    }

    private static void EnforceLimit()
    {
        var files = Directory.GetFiles(StorageDir, $"{FilePrefix}*{FileExtension}")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        while (files.Count > MaxFiles)
        {
            try { files[0].Delete(); } catch { }
            files.RemoveAt(0);
        }
    }
}
