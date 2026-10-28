using System;
using System.Collections.Generic;
using System.Linq;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 崩溃前硬件读数分析：识别温度过高/骤升、供电电压偏离、降频、风扇停转等疑似异常。
/// 基于崩溃前窗口内的采样点，结论仅作为排障方向提示（传感器精度有限）。
/// </summary>
public static class HardwareAnomalyAnalyzer
{
    private const int MinSamplesForTrend = 6;
    private const double GpuHotspotWarn = 100.0;
    private const double GpuHotspotCrit = 105.0;
    private const double TempSpikeDelta = 15.0;
    private const double FanTempThreshold = 80.0;
    private const double CpuClockDropRatio = 0.6;
    private const double CpuClockDropMinLoad = 50.0;

    /// <summary>分析快照中崩溃前窗口的读数（崩溃时刻后 2 秒内仍视为崩溃前）</summary>
    public static List<HardwareAnomaly> Analyze(HardwareSnapshot snapshot)
    {
        var result = new List<HardwareAnomaly>();
        var pre = GetPreCrashReadings(snapshot);
        if (pre.Count == 0)
        {
            result.Add(Anomaly(AnomalySeverity.Info, "无崩溃前读数",
                "监控未覆盖崩溃前时段（例如崩溃后才启动程序），无法分析硬件状态。"));
            return result;
        }

        double windowSec = (snapshot.CrashTime - pre[0].Timestamp).TotalSeconds;
        if (pre.Count < 5 || windowSec < 60)
        {
            result.Add(Anomaly(AnomalySeverity.Info, "监控窗口较短",
                $"崩溃前 {pre.Count} 个采样点 / {Math.Max(0, windowSec):F0} 秒读数，以下结论仅供参考。"));
        }

        AnalyzeCpu(pre, snapshot.CrashTime, result);
        AnalyzeGpu(pre, result);
        AnalyzeSsd(pre, result);
        AnalyzeVoltage(pre, r => r.Voltage12V, HardwareThresholds.V12Nominal, "+12V", result);
        AnalyzeVoltage(pre, r => r.Voltage5V, HardwareThresholds.V5Nominal, "+5V", result);
        AnalyzeVoltage(pre, r => r.Voltage3V, HardwareThresholds.V3Nominal, "+3.3V", result);
        AnalyzeFans(pre, result);

        return result;
    }

    /// <summary>取崩溃前窗口内的读数（升序）</summary>
    public static List<HardwareReading> GetPreCrashReadings(HardwareSnapshot snapshot)
        => snapshot.Readings
            .Where(r => r.Timestamp <= snapshot.CrashTime.AddSeconds(2))
            .OrderBy(r => r.Timestamp)
            .ToList();

    private static void AnalyzeCpu(List<HardwareReading> pre, DateTime crashTime, List<HardwareAnomaly> result)
    {
        var temps = pre.Where(r => r.CpuTemperature.HasValue).ToList();
        if (temps.Count > 0)
        {
            var peak = temps.OrderByDescending(r => r.CpuTemperature!.Value).First();
            double max = peak.CpuTemperature!.Value;
            var level = HardwareThresholds.EvaluateCpuTemp(max);
            if (level is HardwareHealth.Warning or HardwareHealth.Critical)
            {
                result.Add(Anomaly(
                    level == HardwareHealth.Critical ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    "CPU 温度偏高（疑似过热）",
                    $"峰值 {max:F1}℃（{peak.Timestamp:HH:mm:ss}），警戒线 {HardwareThresholds.CpuTempWarn:0}℃ / " +
                    $"危险线 {HardwareThresholds.CpuTempCrit:0}℃。"));
            }

            if (temps.Count >= MinSamplesForTrend)
            {
                var early = temps.Where(r => r.Timestamp <= pre[0].Timestamp.AddMinutes(2)).ToList();
                var last2 = temps.Where(r => r.Timestamp >= crashTime.AddMinutes(-2)).ToList();
                if (early.Count >= 2 && last2.Count >= 2)
                {
                    double earlyAvg = early.Average(r => r.CpuTemperature!.Value);
                    double lastMax = last2.Max(r => r.CpuTemperature!.Value);
                    if (lastMax - earlyAvg >= TempSpikeDelta)
                    {
                        result.Add(Anomaly(AnomalySeverity.Warning, "CPU 温度骤升",
                            $"窗口初期均值 {earlyAvg:F1}℃ → 最后 2 分钟峰值 {lastMax:F1}℃（+{lastMax - earlyAvg:F1}℃）。"));
                    }
                }
            }
        }

        var clocks = pre.Where(r => r.CpuClock.HasValue).ToList();
        if (clocks.Count >= MinSamplesForTrend)
        {
            double maxClock = clocks.Max(r => r.CpuClock!.Value);
            var lastMinute = clocks.Where(r => r.Timestamp >= crashTime.AddMinutes(-1)).ToList();
            var loads = pre.Where(r => r.Timestamp >= crashTime.AddMinutes(-1) && r.CpuLoad.HasValue)
                .Select(r => r.CpuLoad!.Value).ToList();
            if (lastMinute.Count >= 2 && maxClock > 0)
            {
                double avgClock = lastMinute.Average(r => r.CpuClock!.Value);
                double avgLoad = loads.Count > 0 ? loads.Average() : 0;
                if (avgClock < maxClock * CpuClockDropRatio && avgLoad >= CpuClockDropMinLoad)
                {
                    result.Add(Anomaly(AnomalySeverity.Warning, "CPU 频率明显下降（疑似降频）",
                        $"最后 1 分钟均值 {avgClock:F0}MHz，窗口峰值 {maxClock:F0}MHz，CPU 负载 {avgLoad:F0}%。"));
                }
            }
        }
    }

    private static void AnalyzeGpu(List<HardwareReading> pre, List<HardwareAnomaly> result)
    {
        var temps = pre.Where(r => r.GpuTemperature.HasValue).ToList();
        if (temps.Count > 0)
        {
            var peak = temps.OrderByDescending(r => r.GpuTemperature!.Value).First();
            double max = peak.GpuTemperature!.Value;
            var level = HardwareThresholds.EvaluateGpuTemp(max);
            if (level is HardwareHealth.Warning or HardwareHealth.Critical)
            {
                result.Add(Anomaly(
                    level == HardwareHealth.Critical ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    "GPU 温度偏高（疑似过热）",
                    $"峰值 {max:F1}℃（{peak.Timestamp:HH:mm:ss}），警戒线 {HardwareThresholds.GpuTempWarn:0}℃ / " +
                    $"危险线 {HardwareThresholds.GpuTempCrit:0}℃。"));
            }
        }

        var hotspots = pre.Where(r => r.GpuHotspot.HasValue).ToList();
        if (hotspots.Count > 0)
        {
            var peak = hotspots.OrderByDescending(r => r.GpuHotspot!.Value).First();
            double max = peak.GpuHotspot!.Value;
            if (max >= GpuHotspotWarn)
            {
                result.Add(Anomaly(
                    max >= GpuHotspotCrit ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    "GPU 热点温度偏高（疑似过热）",
                    $"峰值 {max:F1}℃（{peak.Timestamp:HH:mm:ss}），警戒线 {GpuHotspotWarn:0}℃ / 危险线 {GpuHotspotCrit:0}℃。"));
            }
        }
    }

    private static void AnalyzeSsd(List<HardwareReading> pre, List<HardwareAnomaly> result)
    {
        var temps = pre.Where(r => r.SsdTemperature.HasValue).ToList();
        if (temps.Count == 0) return;

        var peak = temps.OrderByDescending(r => r.SsdTemperature!.Value).First();
        double max = peak.SsdTemperature!.Value;
        var level = HardwareThresholds.EvaluateSsdTemp(max);
        if (level is HardwareHealth.Warning or HardwareHealth.Critical)
        {
            result.Add(Anomaly(
                level == HardwareHealth.Critical ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                "SSD 温度偏高（疑似过热）",
                $"峰值 {max:F1}℃（{peak.Timestamp:HH:mm:ss}），警戒线 {HardwareThresholds.SsdTempWarn:0}℃ / " +
                $"危险线 {HardwareThresholds.SsdTempCrit:0}℃。"));
        }
    }

    private static void AnalyzeVoltage(List<HardwareReading> pre, Func<HardwareReading, double?> selector,
        double nominal, string label, List<HardwareAnomaly> result)
    {
        var values = pre.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count < 3) return;

        double min = values.Min();
        double max = values.Max();
        double deviation = Math.Max(Math.Abs(min - nominal), Math.Abs(max - nominal)) / nominal;
        if (deviation < HardwareThresholds.VtolWarn) return;

        var severity = deviation >= HardwareThresholds.VtolCrit
            ? AnomalySeverity.Critical
            : AnomalySeverity.Warning;
        result.Add(Anomaly(severity, $"{label} 电压异常（疑似供电不稳）",
            $"范围 {min:F2}–{max:F2}V（额定 {nominal:0.##}V，最大偏离 {deviation * 100:F1}%）。"));
    }

    private static void AnalyzeFans(List<HardwareReading> pre, List<HardwareAnomaly> result)
    {
        var cpuStall = pre.FirstOrDefault(r =>
            r.CpuFanRpm.HasValue && r.CpuFanRpm.Value < 1 &&
            r.CpuTemperature.HasValue && r.CpuTemperature.Value >= FanTempThreshold);
        if (cpuStall != null)
        {
            result.Add(Anomaly(AnomalySeverity.Warning, "CPU 风扇转速为 0（疑似停转或未接测速）",
                $"CPU 温度 {cpuStall.CpuTemperature!.Value:F0}℃ 时（{cpuStall.Timestamp:HH:mm:ss}）转速读数为 0。"));
        }

        var gpuStall = pre.FirstOrDefault(r =>
            r.GpuFanRpm.HasValue && r.GpuFanRpm.Value < 1 &&
            r.GpuTemperature.HasValue && r.GpuTemperature.Value >= FanTempThreshold);
        if (gpuStall != null)
        {
            result.Add(Anomaly(AnomalySeverity.Warning, "GPU 风扇转速为 0（疑似停转）",
                $"GPU 温度 {gpuStall.GpuTemperature!.Value:F0}℃ 时（{gpuStall.Timestamp:HH:mm:ss}）转速读数为 0。"));
        }
    }

    private static HardwareAnomaly Anomaly(AnomalySeverity severity, string title, string detail)
        => new() { Severity = severity, Title = title, Detail = detail };
}
