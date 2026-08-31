namespace CrashSniffer.Models;

/// <summary>
/// 崩溃事件类型，严重级别从高到低
/// </summary>
public enum CrashType
{
    /// <summary>蓝屏 BSOD (BugCheck)：最严重</summary>
    BSOD = 0,

    /// <summary>WHEA 硬件错误 (CPU/PCIe/内存/ECC)</summary>
    WHEA_Error = 1,

    /// <summary>GPU 驱动 TDR 超时恢复重置</summary>
    GPU_TDR = 2,

    /// <summary>Kernel-Power Event ID 41：非正常重启</summary>
    KernelPower_41 = 3,

    /// <summary>EventLog Event ID 6008：意外关闭</summary>
    Shutdown_6008 = 4,

    /// <summary>LiveKernelReports dump：GPU 等设备挂死但未蓝屏</summary>
    LiveKernel = 5,
}

/// <summary>
/// 关联的原始事件记录（用于事件前后±5分钟内的事件展示与导出）
/// </summary>
public class RelatedEvent
{
    public DateTime Time { get; set; }
    public string Provider { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RawXml { get; set; }
}

/// <summary>
/// 聚合后的单次崩溃事件
/// </summary>
public class CrashEvent
{
    /// <summary>崩溃发生时间</summary>
    public DateTime Time { get; set; }

    /// <summary>事件类型</summary>
    public CrashType Type { get; set; }

    /// <summary>列表摘要</summary>
    public string Summary { get; set; } = string.Empty;

    // —— BSOD 相关字段 ——
    /// <summary>停止代码十六进制，如 0x0000000A</summary>
    public uint BugCheckCode { get; set; }

    /// <summary>停止代码英文符号名，如 IRQL_NOT_LESS_OR_EQUAL</summary>
    public string StopCode { get; set; } = string.Empty;

    /// <summary>停止代码中文解释</summary>
    public string StopCodeChinese { get; set; } = string.Empty;

    /// <summary>4 个 BugCheck 参数</summary>
    public ulong[] BugCheckParams { get; set; } = new ulong[4];

    /// <summary>嫌疑驱动文件，如 amdkmdag.sys</summary>
    public string SuspectDriver { get; set; } = string.Empty;

    /// <summary>dump 完整路径</summary>
    public string DumpPath { get; set; } = string.Empty;

    /// <summary>dump 文件大小（字节），0 表示无 dump</summary>
    public long DumpSize { get; set; }

    // —— WHEA / TDR / 41 / 6008 相关字段 ——
    /// <summary>WHEA 错误类型：CPU Cache / TLB / Bus / ...</summary>
    public string WheaErrorType { get; set; } = string.Empty;

    /// <summary>WHEA 出错 CPU/PCIe 位置描述</summary>
    public string WheaLocation { get; set; } = string.Empty;

    /// <summary>WHEA 出错部位结论：指向 CPU / PCIe 设备 / 内存等硬件部位</summary>
    public string WheaHardwarePart { get; set; } = string.Empty;

    /// <summary>GPU 厂商：AMD / NVIDIA / Intel / 未知</summary>
    public string GpuVendor { get; set; } = string.Empty;

    /// <summary>原始事件消息</summary>
    public string EventMessage { get; set; } = string.Empty;

    /// <summary>关联事件列表（前后±5分钟）</summary>
    public List<RelatedEvent> RelatedEvents { get; } = new();

    /// <summary>根据停止代码/嫌疑驱动匹配的排障建议</summary>
    public List<string> Troubleshooting { get; } = new();
}
