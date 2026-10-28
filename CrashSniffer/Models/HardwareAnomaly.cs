namespace CrashSniffer.Models;

/// <summary>硬件异常严重程度</summary>
public enum AnomalySeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// 崩溃前硬件读数分析结论（疑似异常）。
/// 传感器精度有限，结论仅作排障方向提示。
/// </summary>
public class HardwareAnomaly
{
    public AnomalySeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
