using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace CrashSniffer.Services;

/// <summary>物理磁盘信息（型号 + 盘符）</summary>
public sealed class PhysicalDiskInfo
{
    public int Index { get; init; }
    public string Model { get; init; } = string.Empty;
    public List<string> Volumes { get; init; } = new();
}

/// <summary>
/// Win32_DiskDrive.Index → 盘符映射：通过 WMI 关联表
/// （物理盘 → 分区 → 逻辑盘）查询，结果缓存 60 秒。
/// 查询失败静默降级为空映射，不影响传感器采集。
/// </summary>
public static class StorageVolumeMapper
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly object Sync = new();

    private static Dictionary<int, PhysicalDiskInfo> _cache = new();
    private static DateTime _cacheAtUtc = DateTime.MinValue;

    public static IReadOnlyDictionary<int, PhysicalDiskInfo> GetDisks()
    {
        lock (Sync)
        {
            if (DateTime.UtcNow - _cacheAtUtc < CacheTtl) return _cache;
            try
            {
                _cache = Query();
            }
            catch
            {
                _cache = new Dictionary<int, PhysicalDiskInfo>();
            }
            // 失败也记录时间，避免每轮轮询都阻塞在 WMI 查询上
            _cacheAtUtc = DateTime.UtcNow;
            return _cache;
        }
    }

    internal static void ResetCache()
    {
        lock (Sync)
        {
            _cache = new Dictionary<int, PhysicalDiskInfo>();
            _cacheAtUtc = DateTime.MinValue;
        }
    }

    private static Dictionary<int, PhysicalDiskInfo> Query()
    {
        var disks = new Dictionary<int, PhysicalDiskInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Index, Model FROM Win32_DiskDrive");
        using var results = searcher.Get();
        foreach (ManagementObject drive in results)
        {
            try
            {
                if (drive["Index"] == null) continue;
                int index = Convert.ToInt32(drive["Index"]);
                var info = new PhysicalDiskInfo
                {
                    Index = index,
                    Model = (drive["Model"]?.ToString() ?? string.Empty).Trim(),
                };

                using (var partitions = drive.GetRelated("Win32_DiskPartition"))
                {
                    foreach (ManagementObject partition in partitions)
                    {
                        using (partition)
                        using (var logicals = partition.GetRelated("Win32_LogicalDisk"))
                        {
                            foreach (ManagementObject logical in logicals)
                            {
                                using (logical)
                                {
                                    string letter = (logical["DeviceID"]?.ToString() ?? string.Empty).Trim();
                                    if (letter.Length > 0 &&
                                        !info.Volumes.Contains(letter, StringComparer.OrdinalIgnoreCase))
                                        info.Volumes.Add(letter);
                                }
                            }
                        }
                    }
                }

                info.Volumes.Sort(StringComparer.OrdinalIgnoreCase);
                disks[index] = info;
            }
            catch
            {
                // 单块磁盘查询失败跳过
            }
        }
        return disks;
    }
}
