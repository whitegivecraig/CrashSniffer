using System;

namespace CrashSniffer.Models;

/// <summary>
/// 单次硬件传感器读数快照（一次轮询的全部核心指标）。
/// 缺失项为 null，UI 展示为 "—"。
/// </summary>
public class HardwareReading
{
    /// <summary>采集时间（本地时间）</summary>
    public DateTime Timestamp { get; set; }

    // —— CPU ——
    /// <summary>CPU 封装温度（℃）</summary>
    public double? CpuTemperature { get; set; }

    /// <summary>CPU 封装功耗（W）</summary>
    public double? CpuPower { get; set; }

    /// <summary>CPU 核心平均频率（MHz）</summary>
    public double? CpuClock { get; set; }

    /// <summary>CPU 负载（%）</summary>
    public double? CpuLoad { get; set; }

    // —— GPU ——
    /// <summary>GPU 核心温度（℃）</summary>
    public double? GpuTemperature { get; set; }

    /// <summary>GPU 热点温度（℃，部分卡支持）</summary>
    public double? GpuHotspot { get; set; }

    /// <summary>GPU 功耗（W）</summary>
    public double? GpuPower { get; set; }

    /// <summary>GPU 核心频率（MHz）</summary>
    public double? GpuClock { get; set; }

    /// <summary>GPU 显存频率（MHz）</summary>
    public double? GpuMemoryClock { get; set; }

    /// <summary>GPU 利用率（%）</summary>
    public double? GpuLoad { get; set; }

    // —— 主板 / 电压 ——
    /// <summary>CPU Vcore 电压（V）</summary>
    public double? CpuVoltage { get; set; }

    /// <summary>+12V 电压（V）</summary>
    public double? Voltage12V { get; set; }

    /// <summary>+5V 电压（V）</summary>
    public double? Voltage5V { get; set; }

    /// <summary>+3.3V 电压（V）</summary>
    public double? Voltage3V { get; set; }

    // —— 散热 ——
    /// <summary>CPU 风扇转速（RPM）</summary>
    public double? CpuFanRpm { get; set; }

    /// <summary>GPU 风扇转速（RPM，取第一个 GPU 风扇）</summary>
    public double? GpuFanRpm { get; set; }

    // —— 存储 ——
    /// <summary>SSD 温度（℃，取第一个 NVMe/SATA SSD）</summary>
    public double? SsdTemperature { get; set; }

    /// <summary>SSD 剩余寿命（%，部分 SSD 支持）</summary>
    public double? SsdLife { get; set; }
}

/// <summary>
/// 崩溃时刻的硬件读数窗口快照（落盘到 hardware-snapshots 目录）
/// </summary>
public class HardwareSnapshot
{
    /// <summary>崩溃事件唯一标识（时间+ticks，用于与崩溃列表关联）</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>崩溃发生时间</summary>
    public DateTime CrashTime { get; set; }

    /// <summary>崩溃类型描述</summary>
    public string CrashType { get; set; } = string.Empty;

    /// <summary>崩溃前 5min + 崩溃后 1min 的读数序列（按时间升序）</summary>
    public List<HardwareReading> Readings { get; set; } = new();
}
