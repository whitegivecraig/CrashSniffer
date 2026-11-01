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

    /// <summary>崩溃指令所在的模块+偏移（如 nvlddmkm.sys+0x12F4A0）</summary>
    public string FaultingModuleOffset { get; set; } = string.Empty;

    /// <summary>异常发生时的指令地址（来自 Minidump ExceptionStream）</summary>
    public ulong ExceptionAddress { get; set; }

    /// <summary>异常代码（来自 Minidump ExceptionStream）</summary>
    public uint ExceptionCode { get; set; }

    /// <summary>主 dump 完整路径</summary>
    public string DumpPath { get; set; } = string.Empty;

    /// <summary>主 dump 文件大小（字节），0 表示无 dump</summary>
    public long DumpSize { get; set; }

    /// <summary>本次聚合事件关联的全部 dump 文件路径（含主 dump 与同时间窗合并的其它 dump）</summary>
    public List<string> AllDumpPaths { get; set; } = new();

    // —— WHEA / TDR / 41 / 6008 相关字段 ——
    /// <summary>WHEA 错误类型：CPU Cache / TLB / Bus / ...</summary>
    public string WheaErrorType { get; set; } = string.Empty;

    /// <summary>WHEA 出错 CPU/PCIe 位置描述</summary>
    public string WheaLocation { get; set; } = string.Empty;

    /// <summary>WHEA 报错的处理器 APIC ID（用于定位具体超频不稳核心）</summary>
    public int? WheaApicId { get; set; }

    /// <summary>WHEA 出错部位结论：指向 CPU / PCIe 设备 / 内存等硬件部位</summary>
    public string WheaHardwarePart { get; set; } = string.Empty;

    /// <summary>GPU 厂商：AMD / NVIDIA / Intel / 未知</summary>
    public string GpuVendor { get; set; } = string.Empty;

    /// <summary>原始事件消息</summary>
    public string EventMessage { get; set; } = string.Empty;

    /// <summary>关联事件列表（前后±5分钟）</summary>
    public List<RelatedEvent> RelatedEvents { get; set; } = new();

    /// <summary>根据停止代码/嫌疑驱动匹配的排障建议</summary>
    public List<string> Troubleshooting { get; set; } = new();

    /// <summary>向 AllDumpPaths 中去重添加一条转储文件路径</summary>
    public void AddDumpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string trimmed = path.Trim();
        if (!AllDumpPaths.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)))
            AllDumpPaths.Add(trimmed);
    }

    /// <summary>获取当前事件关联的全部去重转储路径（主路径优先）</summary>
    public List<string> GetEffectiveDumpPaths()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(DumpPath))
            list.Add(DumpPath.Trim());
        foreach (var p in AllDumpPaths)
        {
            if (!string.IsNullOrWhiteSpace(p) &&
                !list.Any(existing => string.Equals(existing, p.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(p.Trim());
            }
        }
        return list;
    }

    /// <summary>格式化文件大小（避免小转储文件整除后显示 0 MB）</summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 KB";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
