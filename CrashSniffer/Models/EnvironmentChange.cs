namespace CrashSniffer.Models;

/// <summary>
/// 环境变更类别
/// </summary>
public enum ChangeCategory
{
    /// <summary>BIOS 版本/日期刷新</summary>
    Bios = 0,

    /// <summary>Windows 版本/Build 更新</summary>
    WindowsUpdate = 1,

    /// <summary>显卡驱动版本变化</summary>
    GpuDriver = 2,

    /// <summary>内存实际运行频率变化（XMP/EXPO 开关推断）</summary>
    MemorySpeed = 3,

    /// <summary>芯片组/平台驱动版本变化</summary>
    PlatformDriver = 4,

    /// <summary>硬件清单增删（CPU/主板/GPU/内存条）</summary>
    Hardware = 5,

    /// <summary>Windows 内存诊断结果变化</summary>
    MemoryDiag = 6,
}

/// <summary>
/// 单条环境变更记录：两次扫描之间检测到的差异
/// </summary>
public class EnvironmentChange
{
    /// <summary>变更被检测到的扫描时间（即新快照的 CollectedAt）</summary>
    public DateTime Time { get; set; }

    public ChangeCategory Category { get; set; }

    /// <summary>变更对象，如 "显卡 AMD Radeon RX 7800 XT"、"BIOS"</summary>
    public string Item { get; set; } = string.Empty;

    public string OldValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;
}

/// <summary>
/// 环境历史存档中的快照条目
/// </summary>
public class SnapshotRecord
{
    public DateTime CollectedAt { get; set; }

    /// <summary>快照内容指纹，用于判断环境是否变化（见 EnvironmentChangeDetector.FingerprintOf）</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public EnvironmentSnapshot Snapshot { get; set; } = new();
}
