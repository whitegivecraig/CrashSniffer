using System;
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

/// <summary>
/// 硬件阈值判定：根据读数返回健康等级。
/// 阈值参考主流硬件厂商规格，保守取值。
/// </summary>
public static class HardwareThresholds
{
    // CPU 温度阈值（℃）
    internal const double CpuTempWarn = 90.0;
    internal const double CpuTempCrit = 95.0;

    // GPU 温度阈值（℃）
    internal const double GpuTempWarn = 80.0;
    internal const double GpuTempCrit = 85.0;

    // SSD 温度阈值（℃）
    internal const double SsdTempWarn = 65.0;
    internal const double SsdTempCrit = 70.0;

    // 电压阈值：额定 ±5% 警告，±10% 危险
    internal const double V12Nominal = 12.0;
    internal const double V5Nominal = 5.0;
    internal const double V3Nominal = 3.3;
    internal const double VtolWarn = 0.05;   // ±5%
    internal const double VtolCrit = 0.10;   // ±10%

    public static HardwareHealth EvaluateCpuTemp(double? temp)
        => EvaluateTemp(temp, CpuTempWarn, CpuTempCrit);

    public static HardwareHealth EvaluateGpuTemp(double? temp)
        => EvaluateTemp(temp, GpuTempWarn, GpuTempCrit);

    public static HardwareHealth EvaluateSsdTemp(double? temp)
        => EvaluateTemp(temp, SsdTempWarn, SsdTempCrit);

    public static HardwareHealth EvaluateVoltage12V(double? v)
        => EvaluateVoltage(v, V12Nominal);

    public static HardwareHealth EvaluateVoltage5V(double? v)
        => EvaluateVoltage(v, V5Nominal);

    public static HardwareHealth EvaluateVoltage3V(double? v)
        => EvaluateVoltage(v, V3Nominal);

    private static HardwareHealth EvaluateTemp(double? v, double warn, double crit)
    {
        if (v == null) return HardwareHealth.Unknown;
        if (v >= crit) return HardwareHealth.Critical;
        if (v >= warn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    private static HardwareHealth EvaluateVoltage(double? v, double nominal)
    {
        if (v == null) return HardwareHealth.Unknown;
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
        var levels = new[]
        {
            EvaluateCpuTemp(r.CpuTemperature),
            EvaluateGpuTemp(r.GpuTemperature),
            EvaluateSsdTemp(r.SsdTemperature),
            EvaluateVoltage12V(r.Voltage12V),
            EvaluateVoltage5V(r.Voltage5V),
            EvaluateVoltage3V(r.Voltage3V),
        };

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
