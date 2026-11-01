using System;
using System.Collections.Generic;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>硬件健康等级</summary>
public enum HardwareHealth
{
    Unknown,   // 无数据
    Normal,    // 正常（绿）
    Warning,   // 警告（橙）
    Critical,  // 危险（红）
}

/// <summary>硬件温度与告警阈值预设档位</summary>
public enum HardwareThresholdPreset
{
    /// <summary>标准风冷台式机（默认）</summary>
    DesktopStandard = 0,
    /// <summary>游戏本 / ITX 紧凑机箱（耐温阈值上调，减少正常满载误报）</summary>
    LaptopHighTemp = 1,
    /// <summary>分体水冷 / 严格温控（告警阈值下调，提早发现导热异常）</summary>
    WaterCooledStrict = 2,
}

/// <summary>
/// 硬件阈值判定：根据读数返回健康等级。
/// 支持「标准台式机 / 游戏本耐温 / 水冷严格」三档预设切换。
/// </summary>
public static class HardwareThresholds
{
    /// <summary>当前生效的硬件阈值预设档位（默认 DesktopStandard）</summary>
    public static HardwareThresholdPreset CurrentPreset { get; set; } = HardwareThresholdPreset.DesktopStandard;

    // CPU 温度阈值（℃）
    internal static double CpuTempWarn => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 95.0,
        HardwareThresholdPreset.WaterCooledStrict => 82.0,
        _ => 90.0,
    };

    internal static double CpuTempCrit => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 100.0,
        HardwareThresholdPreset.WaterCooledStrict => 88.0,
        _ => 95.0,
    };

    // GPU 温度阈值（℃）
    internal static double GpuTempWarn => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 86.0,
        HardwareThresholdPreset.WaterCooledStrict => 74.0,
        _ => 80.0,
    };

    internal static double GpuTempCrit => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 90.0,
        HardwareThresholdPreset.WaterCooledStrict => 80.0,
        _ => 85.0,
    };

    // GPU 显存温度阈值（℃）
    internal static double GpuMemTempWarn => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 96.0,
        HardwareThresholdPreset.WaterCooledStrict => 86.0,
        _ => 90.0,
    };

    internal static double GpuMemTempCrit => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 104.0,
        HardwareThresholdPreset.WaterCooledStrict => 94.0,
        _ => 100.0,
    };

    // GPU 热点温度阈值（℃）
    internal static double GpuHotspotWarn => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 105.0,
        HardwareThresholdPreset.WaterCooledStrict => 92.0,
        _ => 100.0,
    };

    internal static double GpuHotspotCrit => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 110.0,
        HardwareThresholdPreset.WaterCooledStrict => 98.0,
        _ => 105.0,
    };

    // 主板 / SuperIO 传感器温度阈值（℃）
    internal static double MoboTempWarn => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 90.0,
        HardwareThresholdPreset.WaterCooledStrict => 80.0,
        _ => 85.0,
    };

    internal static double MoboTempCrit => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 98.0,
        HardwareThresholdPreset.WaterCooledStrict => 90.0,
        _ => 95.0,
    };

    // SSD 温度阈值（℃）
    internal static double SsdTempWarn => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 72.0,
        HardwareThresholdPreset.WaterCooledStrict => 60.0,
        _ => 65.0,
    };

    internal static double SsdTempCrit => CurrentPreset switch
    {
        HardwareThresholdPreset.LaptopHighTemp => 78.0,
        HardwareThresholdPreset.WaterCooledStrict => 66.0,
        _ => 70.0,
    };

    // 磁盘剩余寿命阈值（%，越低越危险）
    internal const double SsdLifeWarn = 10.0;
    internal const double SsdLifeCrit = 5.0;

    // 内存占用阈值（%）
    internal const double MemoryLoadWarn = 95.0;
    internal const double MemoryLoadCrit = 99.0;

    // CMOS 电池（CR2032）电压阈值（V，越低越危险）
    internal const double CmosBatteryWarn = 2.8;
    internal const double CmosBatteryCrit = 2.5;

    // 电压阈值：额定 ±5% 警告，±10% 危险
    internal const double V12Nominal = 12.0;
    internal const double V5Nominal = 5.0;
    internal const double V3Nominal = 3.3;
    internal const double VtolWarn = 0.05;   // ±5%
    internal const double VtolCrit = 0.10;   // ±10%

    /// <summary>获取预设档位的显示名称</summary>
    public static string PresetDisplayName(HardwareThresholdPreset preset) => preset switch
    {
        HardwareThresholdPreset.DesktopStandard => "🖥️ 标准台式机 (CPU 90℃ / GPU 80℃)",
        HardwareThresholdPreset.LaptopHighTemp => "💻 游戏本 / ITX 耐温 (CPU 95℃ / GPU 86℃)",
        HardwareThresholdPreset.WaterCooledStrict => "❄️ 水冷 / 严格温控 (CPU 82℃ / GPU 74℃)",
        _ => preset.ToString(),
    };

    public static HardwareHealth EvaluateCpuTemp(double? temp)
        => EvaluateTemp(temp, CpuTempWarn, CpuTempCrit);

    public static HardwareHealth EvaluateGpuTemp(double? temp)
        => EvaluateTemp(temp, GpuTempWarn, GpuTempCrit);

    public static HardwareHealth EvaluateGpuMemoryTemp(double? temp)
        => EvaluateTemp(temp, GpuMemTempWarn, GpuMemTempCrit);

    public static HardwareHealth EvaluateGpuHotspot(double? temp)
        => EvaluateTemp(temp, GpuHotspotWarn, GpuHotspotCrit);

    public static HardwareHealth EvaluateMoboTemp(double? temp)
        => EvaluateTemp(temp, MoboTempWarn, MoboTempCrit);

    public static HardwareHealth EvaluateSsdTemp(double? temp)
        => EvaluateTemp(temp, SsdTempWarn, SsdTempCrit);

    public static HardwareHealth EvaluateSsdLife(double? life)
    {
        if (life == null) return HardwareHealth.Unknown;
        if (life <= SsdLifeCrit) return HardwareHealth.Critical;
        if (life <= SsdLifeWarn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    public static HardwareHealth EvaluateMemoryLoad(double? load)
    {
        if (load == null || load < 0) return HardwareHealth.Unknown;
        if (load >= MemoryLoadCrit) return HardwareHealth.Critical;
        if (load >= MemoryLoadWarn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    public static HardwareHealth EvaluateCmosBattery(double? voltage)
    {
        if (voltage == null || voltage <= 0.1) return HardwareHealth.Unknown;
        if (voltage <= CmosBatteryCrit) return HardwareHealth.Critical;
        if (voltage <= CmosBatteryWarn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    /// <summary>NVMe 备用块余量低于厂商阈值时告警；余量为 0 视为危险</summary>
    public static HardwareHealth EvaluateAvailableSpare(double? spare, double? threshold)
    {
        if (spare == null || threshold == null) return HardwareHealth.Unknown;
        if (spare <= 0) return HardwareHealth.Critical;
        if (spare <= threshold) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    public static HardwareHealth EvaluateVoltage12V(double? v)
        => EvaluateVoltage(v, V12Nominal);

    public static HardwareHealth EvaluateVoltage5V(double? v)
        => EvaluateVoltage(v, V5Nominal);

    public static HardwareHealth EvaluateVoltage3V(double? v)
        => EvaluateVoltage(v, V3Nominal);

    private static HardwareHealth EvaluateTemp(double? v, double warn, double crit)
    {
        // 0.0℃ 或负数、>=150℃ 均为无效/未就绪传感器读数，绝不能判定为"正常"
        if (v == null || v <= 0.5 || v >= 150.0) return HardwareHealth.Unknown;
        if (v >= crit) return HardwareHealth.Critical;
        if (v >= warn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    private static HardwareHealth EvaluateVoltage(double? v, double nominal)
    {
        if (v == null || v <= 0.1) return HardwareHealth.Unknown;
        double deviation = Math.Abs(v.Value - nominal) / nominal;
        if (deviation >= VtolCrit) return HardwareHealth.Critical;
        if (deviation >= VtolWarn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    /// <summary>
    /// 综合评估：取所有指标中最严重的等级。
    /// 无任何有效读数返回 Unknown。
    /// </summary>
    public static HardwareHealth EvaluateOverall(HardwareReading r)
    {
        var levels = new List<HardwareHealth>
        {
            EvaluateCpuTemp(r.CpuTemperature),
            EvaluateGpuTemp(r.GpuTemperature),
            EvaluateGpuHotspot(r.GpuHotspot),
            EvaluateGpuMemoryTemp(r.GpuMemoryTemperature),
            EvaluateMoboTemp(r.MoboMaxTemperature),
            EvaluateMemoryLoad(r.MemoryLoad),
            EvaluateMemoryLoad(r.VirtualMemoryLoad),
            EvaluateVoltage12V(r.Voltage12V),
            EvaluateVoltage5V(r.Voltage5V),
            EvaluateVoltage3V(r.Voltage3V),
            EvaluateVoltage3V(r.Voltage3VStandby),
            EvaluateCmosBattery(r.CmosBatteryVoltage),
        };

        if (r.Storages is { Count: > 0 })
        {
            foreach (var storage in r.Storages)
            {
                levels.Add(EvaluateSsdTemp(storage.Temperature));
                levels.Add(EvaluateSsdLife(storage.LifePercent));
                levels.Add(EvaluateAvailableSpare(storage.AvailableSparePercent, storage.SpareThresholdPercent));
            }
        }
        else
        {
            // 旧快照只有单盘汇总字段
            levels.Add(EvaluateSsdTemp(r.SsdTemperature));
            levels.Add(EvaluateSsdLife(r.SsdLife));
        }

        HardwareHealth worst = HardwareHealth.Unknown;
        foreach (var l in levels)
        {
            if (l == HardwareHealth.Critical) return HardwareHealth.Critical;
            if (l == HardwareHealth.Warning) worst = HardwareHealth.Warning;
            else if (l == HardwareHealth.Normal && worst == HardwareHealth.Unknown)
                worst = HardwareHealth.Normal;
        }
        return worst;
    }

    /// <summary>等级对应的颜色（与 MainForm 配色一致）</summary>
    public static System.Drawing.Color ColorFor(HardwareHealth level) => level switch
    {
        HardwareHealth.Critical => System.Drawing.Color.FromArgb(0xdc, 0x26, 0x26),
        HardwareHealth.Warning => System.Drawing.Color.FromArgb(0xd9, 0x77, 0x06),
        HardwareHealth.Normal => System.Drawing.Color.FromArgb(0x04, 0x78, 0x57),
        _ => System.Drawing.Color.FromArgb(0x6b, 0x72, 0x80),
    };

    /// <summary>等级中文标签</summary>
    public static string LabelFor(HardwareHealth level) => level switch
    {
        HardwareHealth.Critical => "危险",
        HardwareHealth.Warning => "警告",
        HardwareHealth.Normal => "正常",
        _ => "未知",
    };
}
