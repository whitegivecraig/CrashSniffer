using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using CrashSniffer.Models;

namespace CrashSniffer.Collectors;

/// <summary>
/// 通过 WMI / 注册表 / 事件日志采集硬件与驱动环境快照：
/// GPU+驱动版本、CPU、BIOS、内存频率(XMP 推断)、系统版本、芯片组驱动、内存诊断结果
/// 每个子项独立容错，失败只记录不中断
/// </summary>
public static class EnvironmentCollector
{
    /// <summary>
    /// 采集完整环境快照（耗时约 1-3 秒，建议在后台线程调用）
    /// </summary>
    public static EnvironmentSnapshot Collect()
    {
        var snap = new EnvironmentSnapshot();
        CollectCpu(snap);
        CollectBios(snap);
        CollectMotherboard(snap);
        CollectGpus(snap);
        CollectMemory(snap);
        CollectOs(snap);
        CollectPlatformDrivers(snap);
        CollectMemoryDiag(snap);
        return snap;
    }

    // —— 各子项 ——

    private static void CollectCpu(EnvironmentSnapshot snap)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in searcher.Get())
            {
                snap.CpuName = (o["Name"] as string)?.Trim() ?? string.Empty;
                break;
            }
        }
        catch (Exception ex) { snap.CollectErrors.Add($"CPU 信息采集失败: {ex.Message}"); }
    }

    private static void CollectBios(EnvironmentSnapshot snap)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (var o in searcher.Get())
            {
                snap.BiosVersion = (o["SMBIOSBIOSVersion"] as string)?.Trim() ?? string.Empty;
                if (o["ReleaseDate"] is string dmtf && DateTime.TryParseExact(
                        dmtf.Substring(0, 14), "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    snap.BiosDate = dt.ToString("yyyy-MM-dd");
                break;
            }
        }
        catch (Exception ex) { snap.CollectErrors.Add($"BIOS 信息采集失败: {ex.Message}"); }
    }

    private static void CollectMotherboard(EnvironmentSnapshot snap)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (var o in searcher.Get())
            {
                string maker = (o["Manufacturer"] as string)?.Trim() ?? "";
                string product = (o["Product"] as string)?.Trim() ?? "";
                snap.Motherboard = $"{maker} {product}".Trim();
                break;
            }
        }
        catch (Exception ex) { snap.CollectErrors.Add($"主板信息采集失败: {ex.Message}"); }
    }

    private static void CollectGpus(EnvironmentSnapshot snap)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                var gpu = new GpuInfo
                {
                    Name = (o["Name"] as string)?.Trim() ?? "",
                    DriverVersion = (o["DriverVersion"] as string)?.Trim() ?? "",
                };
                if (o["DriverDate"] is string dmtf && DateTime.TryParseExact(
                        dmtf.Substring(0, 14), "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    gpu.DriverDate = dt.ToString("yyyy-MM-dd");
                if (o["AdapterRAM"] is uint ram)
                    gpu.AdapterRamMb = ram / (1024 * 1024); // WMI uint32 上限 4GB，超过的显示 4095
                if (!string.IsNullOrEmpty(gpu.Name))
                    snap.Gpus.Add(gpu);
            }
        }
        catch (Exception ex) { snap.CollectErrors.Add($"显卡信息采集失败: {ex.Message}"); }
    }

    private static void CollectMemory(EnvironmentSnapshot snap)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            double totalGb = 0;
            foreach (var o in searcher.Get())
            {
                var stick = new MemoryStickInfo
                {
                    Slot = (o["BankLabel"] as string)?.Trim() ?? "",
                    Manufacturer = (o["Manufacturer"] as string)?.Trim() ?? "",
                    PartNumber = (o["PartNumber"] as string)?.Trim() ?? "",
                };
                if (o["Capacity"] is ulong cap) { stick.CapacityGb = Math.Round(cap / (1024.0 * 1024 * 1024), 0); totalGb += stick.CapacityGb; }
                stick.Speed = ToUint(o["Speed"]);
                stick.ConfiguredSpeed = ToUint(o["ConfiguredClockSpeed"]);
                snap.MemorySticks.Add(stick);
            }

            if (snap.MemorySticks.Count > 0)
            {
                uint actual = snap.MemorySticks.Max(s => s.ConfiguredSpeed);
                uint rated = snap.MemorySticks.Max(s => s.Speed);
                string spec = snap.MemorySticks
                    .Select(s => $"{s.CapacityGb}GB")
                    .GroupBy(x => x)
                    .Select(g => $"{g.Count()}x{g.Key}")
                    .Aggregate((a, b) => a + " + " + b);
                snap.MemorySummary = $"{spec}，实际运行 {actual} MT/s（SPD 标称上限 {rated} MT/s）";

                // XMP/EXPO 推断：实际频率高于 JEDEC 基础频率，基本可以断定开了 XMP/EXPO 或手动超频
                if (actual > rated)
                    snap.XmpNote = $"实际频率 {actual} 高于 SPD 标称 {rated}：开启了 XMP/EXPO 或手动超频内存";
                else if (actual >= 2933)
                    snap.XmpNote = $"实际频率 {actual} MT/s 已超出 JEDEC 标准范围，大概率开启了 XMP/EXPO";
                else
                    snap.XmpNote = "未检测到内存超频迹象（运行在 JEDEC 标准频率内）";
            }
        }
        catch (Exception ex) { snap.CollectErrors.Add($"内存信息采集失败: {ex.Message}"); }
    }

    private static void CollectOs(EnvironmentSnapshot snap)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                snap.OsName = (key.GetValue("ProductName") as string)?.Trim() ?? "";
                snap.OsDisplayVersion = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "";
                string build = (key.GetValue("CurrentBuildNumber") as string)?.Trim() ?? "";
                string ubr = key.GetValue("UBR")?.ToString() ?? "";
                snap.OsBuild = string.IsNullOrEmpty(build) ? "" : $"{build}.{ubr}";
            }
        }
        catch (Exception ex) { snap.CollectErrors.Add($"系统版本采集失败: {ex.Message}"); }
    }

    /// <summary>
    /// 平台/芯片组相关驱动版本：AMD 平台查 amd*，NVIDIA/Intel 同理；
    /// 这些驱动版本对 WHEA/PCIe 类排障非常关键（AGESA/芯片组驱动更新经常直接修复问题）
    /// </summary>
    private static void CollectPlatformDrivers(EnvironmentSnapshot snap)
    {
        // 常见平台驱动 → 说明
        var targets = new (string file, string desc)[]
        {
            ("amdppm.sys", "AMD 处理器电源管理"),
            ("amdi2c.sys", "AMD I2C/SMBus"),
            ("amdgpio2.sys", "AMD GPIO"),
            ("amdpsp.sys", "AMD PSP 平台安全处理器"),
            ("amdsata.sys", "AMD SATA"),
            ("nvme.sys", "标准 NVMe 存储"),
            ("stornvme.sys", "Windows NVMe 存储"),
            ("storahci.sys", "Windows AHCI SATA"),
        };

        string driversDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers");

        foreach (var (file, desc) in targets)
        {
            try
            {
                string path = Path.Combine(driversDir, file);
                if (!File.Exists(path)) continue;
                var vi = FileVersionInfo.GetVersionInfo(path);
                snap.PlatformDrivers.Add(new PlatformDriverInfo
                {
                    FileName = file,
                    Description = desc,
                    Version = vi.FileVersion ?? vi.ProductVersion ?? "未知",
                });
            }
            catch
            {
                // 单个驱动失败跳过
            }
        }
    }

    /// <summary>
    /// 读取最近一次 Windows 内存诊断结果 (MemoryDiagnostics-Results 日志 1101/1102)
    /// </summary>
    private static void CollectMemoryDiag(EnvironmentSnapshot snap)
    {
        try
        {
            string xpath = "<QueryList><Query Id=\"0\" Path=\"MemoryDiagnostics-Results\">" +
                           "<Select Path=\"MemoryDiagnostics-Results\">*[System[(EventID=1101 or EventID=1102)]]" +
                           "</Select></Query></QueryList>";
            var query = new EventLogQuery("MemoryDiagnostics-Results", PathType.LogName, xpath)
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            EventRecord? rec;
            while ((rec = reader.ReadEvent()) != null)
            {
                using (rec)
                {
                    snap.MemoryDiag.Found = true;
                    snap.MemoryDiag.ErrorsDetected = rec.Id == 1102;
                    snap.MemoryDiag.Time = rec.TimeCreated?.ToLocalTime();
                    snap.MemoryDiag.Detail = SafeMessage(rec);
                    return; // 只取最近一条
                }
            }
        }
        catch
        {
            // 无记录或日志不存在：保持 Found=false
        }
    }

    // —— 辅助 ——

    private static uint ToUint(object? v)
    {
        return v switch
        {
            null => 0,
            uint u => u,
            int i when i >= 0 => (uint)i,
            string s when uint.TryParse(s, out var u) => u,
            _ => 0,
        };
    }

    private static string SafeMessage(EventRecord rec)
    {
        try { return rec.FormatDescription() ?? string.Empty; }
        catch { return string.Empty; }
    }
}
