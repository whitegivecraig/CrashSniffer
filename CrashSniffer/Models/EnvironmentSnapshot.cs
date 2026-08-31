namespace CrashSniffer.Models;

/// <summary>
/// 单块显卡信息
/// </summary>
public class GpuInfo
{
    public string Name { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string DriverDate { get; set; } = string.Empty;
    public long AdapterRamMb { get; set; }
}

/// <summary>
/// 单根内存条信息
/// </summary>
public class MemoryStickInfo
{
    public string Slot { get; set; } = string.Empty;
    public double CapacityGb { get; set; }
    public uint Speed { get; set; }          // 标称/SPD 频率 (MT/s)
    public uint ConfiguredSpeed { get; set; } // 实际运行频率 (MT/s)
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
}

/// <summary>
/// 芯片组/平台相关驱动版本
/// </summary>
public class PlatformDriverInfo
{
    public string FileName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Windows 内存诊断最近一次结果
/// </summary>
public class MemoryDiagResult
{
    /// <summary>是否找到诊断记录</summary>
    public bool Found { get; set; }

    /// <summary>是否检测到硬件错误 (1102)；false = 无错误 (1101)</summary>
    public bool ErrorsDetected { get; set; }

    public DateTime? Time { get; set; }

    public string Detail { get; set; } = string.Empty;

    public string Display => !Found
        ? "无记录（未运行过 Windows 内存诊断）"
        : $"{(ErrorsDetected ? "❌ 检测到硬件问题" : "✅ 未发现错误")} ({Time:yyyy-MM-dd HH:mm})";
}

/// <summary>
/// 扫描时刻的硬件/驱动环境快照，附在报告中便于排障时对照
/// </summary>
public class EnvironmentSnapshot
{
    public DateTime CollectedAt { get; set; } = DateTime.Now;

    public string CpuName { get; set; } = string.Empty;
    public string Motherboard { get; set; } = string.Empty;
    public string BiosVersion { get; set; } = string.Empty;
    public string BiosDate { get; set; } = string.Empty;

    public string OsName { get; set; } = string.Empty;
    public string OsDisplayVersion { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;

    public List<GpuInfo> Gpus { get; } = new();
    public List<MemoryStickInfo> MemorySticks { get; } = new();

    /// <summary>内存总量与频率摘要</summary>
    public string MemorySummary { get; set; } = string.Empty;

    /// <summary>XMP/EXPO 状态推断说明</summary>
    public string XmpNote { get; set; } = string.Empty;

    public List<PlatformDriverInfo> PlatformDrivers { get; } = new();

    public MemoryDiagResult MemoryDiag { get; } = new();

    /// <summary>采集失败的子项说明（WMI 查询失败等），不阻断整体</summary>
    public List<string> CollectErrors { get; } = new();
}
