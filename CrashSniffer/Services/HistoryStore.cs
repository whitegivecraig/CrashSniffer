using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 崩溃事件历史记录（用于趋势统计与前后对比），JSON 存档：
/// %ProgramData%\CrashSniffer\history.json
/// </summary>
public class HistoryRecord
{
    public DateTime Time { get; set; }
    public CrashType Type { get; set; }
    public uint BugCheckCode { get; set; }
    public string StopCode { get; set; } = string.Empty;
    public string SuspectDriver { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>本次扫描收录时间</summary>
    public DateTime RecordedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 历史存档读写：按事件指纹（时间+类型+停止代码+嫌疑驱动）去重合并，
/// 文件损坏时备份为 .bak 并重建，不阻断扫描
/// </summary>
public static class HistoryStore
{
    public static string StorageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CrashSniffer");

    public static string FilePath { get; } = Path.Combine(StorageDir, "history.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 读取全部历史记录（按时间倒序）；文件损坏时备份并返回空列表
    /// </summary>
    public static List<HistoryRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<HistoryRecord>();
            string json = File.ReadAllText(FilePath, Encoding.UTF8);
            var list = JsonSerializer.Deserialize<List<HistoryRecord>>(json, JsonOpts);
            return list ?? new List<HistoryRecord>();
        }
        catch
        {
            // 损坏：备份原文件后重建
            try
            {
                if (File.Exists(FilePath))
                {
                    string bak = FilePath + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(FilePath, bak);
                }
            }
            catch { /* 备份失败也继续 */ }
            return new List<HistoryRecord>();
        }
    }

    /// <summary>
    /// 把一次扫描得到的事件按指纹去重后合并进存档
    /// </summary>
    /// <returns>本次新增的记录数</returns>
    public static int AppendAndSave(IEnumerable<CrashEvent> events)
    {
        try
        {
            var existing = Load();
            var fingerprints = existing.Select(Fingerprint).ToHashSet();

            int added = 0;
            foreach (var ev in events)
            {
                var rec = ToRecord(ev);
                string fp = Fingerprint(rec);
                if (fingerprints.Contains(fp)) continue;

                fingerprints.Add(fp);
                existing.Add(rec);
                added++;
            }

            if (added > 0)
            {
                var ordered = existing.OrderByDescending(r => r.Time).ToList();
                Save(ordered);
            }
            return added;
        }
        catch
        {
            // 历史记录失败不影响主流程
            return 0;
        }
    }

    private static void Save(List<HistoryRecord> records)
    {
        Directory.CreateDirectory(StorageDir);
        string json = JsonSerializer.Serialize(records, JsonOpts);

        // 原子写入：先写临时文件再替换，避免写一半被杀进程留下损坏文件
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(tmp, FilePath);
    }

    private static HistoryRecord ToRecord(CrashEvent ev) => new()
    {
        Time = ev.Time,
        Type = ev.Type,
        BugCheckCode = ev.BugCheckCode,
        StopCode = ev.StopCode,
        SuspectDriver = StripBracket(ev.SuspectDriver),
        Summary = ev.Summary,
    };

    private static string Fingerprint(HistoryRecord r) =>
        $"{r.Time.Ticks}|{(int)r.Type}|{r.BugCheckCode}|{r.StopCode}|{r.SuspectDriver}";

    private static string StripBracket(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        int i = s.IndexOf('[');
        return (i > 0 ? s.Substring(0, i) : s).Trim();
    }
}
