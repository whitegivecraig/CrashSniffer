using System;
using System.Collections.Generic;

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
    /// <summary>CPU 封装温度（℃，AMD 优先 Tctl/Tdie，Intel 取 Package）</summary>
    public double? CpuTemperature { get; set; }

    /// <summary>CPU 封装功耗（W）</summary>
    public double? CpuPower { get; set; }

    /// <summary>CPU 核心平均频率（MHz）</summary>
    public double? CpuClock { get; set; }

    /// <summary>CPU 负载（%）</summary>
    public double? CpuLoad { get; set; }

    /// <summary>CPU 单核心最高负载（%）</summary>
    public double? CpuMaxCoreLoad { get; set; }

    /// <summary>CPU 各逻辑核心负载 / 频率（按编号升序，频率可能少于负载项）</summary>
    public List<CpuCoreReading> CpuCores { get; set; } = new();

    // —— GPU ——
    /// <summary>GPU 核心温度（℃）</summary>
    public double? GpuTemperature { get; set; }

    /// <summary>GPU 热点温度（℃，部分卡支持）</summary>
    public double? GpuHotspot { get; set; }

    /// <summary>GPU 显存温度（℃，部分卡支持）</summary>
    public double? GpuMemoryTemperature { get; set; }

    /// <summary>GPU 显存已用（MB）</summary>
    public double? GpuMemoryUsedMb { get; set; }

    /// <summary>GPU 显存总量（MB）</summary>
    public double? GpuMemoryTotalMb { get; set; }

    /// <summary>GPU 功耗（W）</summary>
    public double? GpuPower { get; set; }

    /// <summary>GPU 核心频率（MHz）</summary>
    public double? GpuClock { get; set; }

    /// <summary>GPU 显存频率（MHz）</summary>
    public double? GpuMemoryClock { get; set; }

    /// <summary>GPU 利用率（%）</summary>
    public double? GpuLoad { get; set; }

    /// <summary>GPU 显存控制器负载（%）</summary>
    public double? GpuMemoryLoad { get; set; }

    /// <summary>GPU 风扇转速百分比（%）</summary>
    public double? GpuFanPercent { get; set; }

    /// <summary>GPU 核心电压（V）</summary>
    public double? GpuVoltage { get; set; }

    // —— 内存 ——
    /// <summary>物理内存占用（%）</summary>
    public double? MemoryLoad { get; set; }

    /// <summary>物理内存已用（GB）</summary>
    public double? MemoryUsedGb { get; set; }

    /// <summary>物理内存总量（GB）</summary>
    public double? MemoryTotalGb { get; set; }

    /// <summary>虚拟内存（含页面文件）占用（%）</summary>
    public double? VirtualMemoryLoad { get; set; }

    /// <summary>虚拟内存已用（GB）</summary>
    public double? VirtualMemoryUsedGb { get; set; }

    /// <summary>虚拟内存总量（GB）</summary>
    public double? VirtualMemoryTotalGb { get; set; }

    // —— 主板 / 电压 ——
    /// <summary>主板/ SuperIO 传感器最高温度（℃）</summary>
    public double? MoboMaxTemperature { get; set; }

    /// <summary>CPU Vcore 电压（V）</summary>
    public double? CpuVoltage { get; set; }

    /// <summary>+12V 电压（V）</summary>
    public double? Voltage12V { get; set; }

    /// <summary>+5V 电压（V）</summary>
    public double? Voltage5V { get; set; }

    /// <summary>+3.3V 电压（V）</summary>
    public double? Voltage3V { get; set; }

    /// <summary>+3V Standby 待机电压（V）</summary>
    public double? Voltage3VStandby { get; set; }

    /// <summary>CMOS 电池电压（V，CR2032）</summary>
    public double? CmosBatteryVoltage { get; set; }

    /// <summary>CPU SoC 电压（V）</summary>
    public double? CpuSocVoltage { get; set; }

    // —— 散热 ——
    /// <summary>CPU 风扇转速（RPM）</summary>
    public double? CpuFanRpm { get; set; }

    /// <summary>GPU 风扇转速（RPM，取第一个 GPU 风扇）</summary>
    public double? GpuFanRpm { get; set; }

    /// <summary>主板 / SuperIO 风扇（有转速的插针，按编号升序）</summary>
    public List<FanReading> Fans { get; set; } = new();

    // —— 存储 ——
    /// <summary>全部物理磁盘读数</summary>
    public List<StorageReading> Storages { get; set; } = new();

    /// <summary>磁盘最高温度（℃，全部磁盘的最大值，兼容旧数据）</summary>
    public double? SsdTemperature { get; set; }

    /// <summary>磁盘最低剩余寿命（%，全部磁盘的最小值，兼容旧数据）</summary>
    public double? SsdLife { get; set; }
}

/// <summary>
/// 单块物理磁盘的传感器读数。
/// Index 与 Win32_DiskDrive.Index 对应，用于关联盘符。
/// </summary>
public class StorageReading
{
    /// <summary>LibreHardwareMonitor 标识（如 /nvme/1、/hdd/0）</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>物理磁盘序号（Win32_DiskDrive.Index），未知为 -1</summary>
    public int Index { get; set; } = -1;

    /// <summary>磁盘型号</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>盘符列表（如 C:、D:），无分区时为空</summary>
    public List<string> Volumes { get; set; } = new();

    /// <summary>温度（℃，多个传感器取最大值）</summary>
    public double? Temperature { get; set; }

    /// <summary>剩余寿命（%），由 Percentage Used 换算；不支持为 null</summary>
    public double? LifePercent { get; set; }

    /// <summary>已用空间（%）</summary>
    public double? UsedSpacePercent { get; set; }

    /// <summary>读取速率（MB/s）</summary>
    public double? ReadRateMBps { get; set; }

    /// <summary>写入速率（MB/s）</summary>
    public double? WriteRateMBps { get; set; }

    /// <summary>累计读取量（GB）</summary>
    public double? TotalReadGb { get; set; }

    /// <summary>累计写入量（GB）</summary>
    public double? TotalWrittenGb { get; set; }

    /// <summary>NVMe 备用块余量（%），不等于寿命</summary>
    public double? AvailableSparePercent { get; set; }

    /// <summary>NVMe 备用块告警阈值（%）</summary>
    public double? SpareThresholdPercent { get; set; }
}

/// <summary>单个 CPU 逻辑核心的负载与频率读数</summary>
public class CpuCoreReading
{
    /// <summary>核心编号（从 1 开始）</summary>
    public int Index { get; set; }

    /// <summary>核心负载（%）</summary>
    public double? Load { get; set; }

    /// <summary>核心频率（MHz，部分逻辑核心无独立传感器）</summary>
    public double? Clock { get; set; }
}

/// <summary>主板 / SuperIO 风扇读数</summary>
public class FanReading
{
    /// <summary>传感器名称（如 Fan #1）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>转速（RPM）</summary>
    public double? Rpm { get; set; }

    /// <summary>调速占空比（%）</summary>
    public double? Percent { get; set; }
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
