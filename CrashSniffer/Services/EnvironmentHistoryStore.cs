using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 环境历史存档数据（快照 + 变更记录）
/// </summary>
public class EnvironmentHistoryData
{
    public List<SnapshotRecord> Snapshots { get; set; } = new();
    public List<EnvironmentChange> Changes { get; set; } = new();
}

/// <summary>
/// 环境快照历史存档：每次扫描后记录快照，与最近一份做指纹对比，
/// 相同则跳过，不同则入库并生成变更记录。
/// JSON 存档 %ProgramData%\CrashSniffer\environment_history.json，
/// 原子写入、损坏自动备份重建、失败不阻断扫描（参照 HistoryStore 模式）
/// </summary>
public static class EnvironmentHistoryStore
{
    /// <summary>快照保留上限（超出淘汰最旧，防文件无限增长）</summary>
    private const int MaxSnapshots = 100;

    /// <summary>变更记录保留上限（超出淘汰最旧）</summary>
    private const int MaxChanges = 500;

    public static string StorageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CrashSniffer");

    public static string FilePath { get; } = Path.Combine(StorageDir, "environment_history.json");

    /// <summary>测试用路径覆盖（避免测试写 %ProgramData%）</summary>
    internal static string? FilePathOverrideForTests { get; set; }

    private static string ActualFilePath => FilePathOverrideForTests ?? FilePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 读取存档；文件损坏时备份为 .bak 并返回空数据
    /// </summary>
    public static EnvironmentHistoryData Load()
    {
        try
        {
            if (!File.Exists(ActualFilePath)) return new EnvironmentHistoryData();
            string json = File.ReadAllText(ActualFilePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<EnvironmentHistoryData>(json, JsonOpts);
            if (data == null) return new EnvironmentHistoryData();
            data.Snapshots ??= new();
            data.Changes ??= new();
            return data;
        }
        catch
        {
            // 损坏：备份原文件后重建，不阻断主流程
            try
            {
                if (File.Exists(ActualFilePath))
                {
                    string bak = ActualFilePath + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(ActualFilePath, bak);
                }
            }
            catch { }
            return new EnvironmentHistoryData();
        }
    }

    /// <summary>
    /// 读取全部变更记录（按时间倒序）
    /// </summary>
    public static List<EnvironmentChange> LoadChanges()
        => Load().Changes.OrderByDescending(c => c.Time).ToList();

    /// <summary>
    /// 记录一次扫描的环境快照：与最近快照做指纹对比，相同则跳过，
    /// 不同则入库并生成变更记录（Time = 新快照采集时间）。返回本次新增变更数
    /// </summary>
    public static int RecordSnapshot(EnvironmentSnapshot snap)
    {
        try
        {
            var data = Load();
            string fp = EnvironmentChangeDetector.FingerprintOf(snap);

            var last = data.Snapshots.OrderByDescending(s => s.CollectedAt).FirstOrDefault();
            if (last != null && last.Fingerprint == fp)
                return 0; // 环境未变，不重复入库

            int added = 0;
            if (last != null)
            {
                foreach (var c in EnvironmentChangeDetector.Detect(last.Snapshot, snap))
                {
                    c.Time = snap.CollectedAt;
                    data.Changes.Add(c);
                    added++;
                }
            }

            data.Snapshots.Add(new SnapshotRecord
            {
                CollectedAt = snap.CollectedAt,
                Fingerprint = fp,
                Snapshot = snap,
            });

            // 上限淘汰
            if (data.Snapshots.Count > MaxSnapshots)
                data.Snapshots = data.Snapshots
                    .OrderByDescending(s => s.CollectedAt).Take(MaxSnapshots).ToList();
            if (data.Changes.Count > MaxChanges)
                data.Changes = data.Changes
                    .OrderByDescending(c => c.Time).Take(MaxChanges).ToList();

            Save(data);
            return added;
        }
        catch
        {
            // 环境历史失败不影响主流程
            return 0;
        }
    }

    /// <summary>原子写入：先写临时文件再替换，避免写一半留下损坏文件</summary>
    private static void Save(EnvironmentHistoryData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ActualFilePath)!);
        string json = JsonSerializer.Serialize(data, JsonOpts);
        string tmp = ActualFilePath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (File.Exists(ActualFilePath)) File.Delete(ActualFilePath);
        File.Move(tmp, ActualFilePath);
    }
}
