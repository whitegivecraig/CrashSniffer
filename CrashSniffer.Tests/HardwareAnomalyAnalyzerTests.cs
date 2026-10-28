using System;
using System.Collections.Generic;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HardwareAnomalyAnalyzerTests
{
    private static readonly DateTime Crash = new(2026, 10, 28, 12, 0, 0);

    private static HardwareSnapshot Window(params HardwareReading[] readings)
        => new() { CrashTime = Crash, Readings = readings.ToList() };

    private static List<HardwareReading> Series(int count, int stepSeconds,
        Func<int, HardwareReading> factory)
    {
        var list = new List<HardwareReading>();
        int start = -(count - 1) * stepSeconds;
        for (int i = 0; i < count; i++)
        {
            var r = factory(i);
            r.Timestamp = Crash.AddSeconds(start + i * stepSeconds);
            list.Add(r);
        }
        return list;
    }

    [Fact]
    public void Analyze_NormalWindow_ReturnsNoAnomalies()
    {
        var readings = Series(6, 20, _ => new HardwareReading
        {
            CpuTemperature = 55,
            CpuLoad = 30,
            CpuClock = 4000,
            GpuTemperature = 60,
            Voltage12V = 12.05,
            Voltage5V = 5.02,
            Voltage3V = 3.31,
        });

        Assert.Empty(HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray())));
    }

    [Fact]
    public void Analyze_NoPreCrashReadings_ReturnsInfo()
    {
        var snapshot = Window(new HardwareReading
        {
            Timestamp = Crash.AddMinutes(5),
            CpuTemperature = 55,
        });

        var anomaly = Assert.Single(HardwareAnomalyAnalyzer.Analyze(snapshot));
        Assert.Equal(AnomalySeverity.Info, anomaly.Severity);
        Assert.Contains("无崩溃前读数", anomaly.Title);
    }

    [Fact]
    public void Analyze_ShortWindow_ReturnsInfoAndStillChecksValues()
    {
        var snapshot = Window(new HardwareReading
        {
            Timestamp = Crash.AddSeconds(-10),
            CpuTemperature = 55,
        });

        var anomaly = Assert.Single(HardwareAnomalyAnalyzer.Analyze(snapshot));
        Assert.Equal(AnomalySeverity.Info, anomaly.Severity);
        Assert.Contains("监控窗口较短", anomaly.Title);
    }

    [Fact]
    public void Analyze_CpuOverheat_ReturnsCritical()
    {
        var readings = Series(6, 20, i => new HardwareReading
        {
            CpuTemperature = i == 5 ? 96 : 60,
        });

        var anomalies = HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray()));

        Assert.Contains(anomalies, a =>
            a.Severity == AnomalySeverity.Critical && a.Title.Contains("CPU 温度偏高"));
    }

    [Fact]
    public void Analyze_CpuTempSpike_ReturnsWarning()
    {
        var readings = Series(10, 20, i => new HardwareReading
        {
            CpuTemperature = i < 8 ? 50 : 75,
        });

        var anomalies = HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray()));

        Assert.Contains(anomalies, a =>
            a.Severity == AnomalySeverity.Warning && a.Title.Contains("CPU 温度骤升"));
    }

    [Fact]
    public void Analyze_CpuClockDropUnderLoad_ReturnsWarning()
    {
        var readings = Series(6, 26, i => new HardwareReading
        {
            CpuTemperature = 70,
            CpuLoad = 90,
            CpuClock = i < 3 ? 4000 : 1200,
        });

        var anomalies = HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray()));

        Assert.Contains(anomalies, a =>
            a.Severity == AnomalySeverity.Warning && a.Title.Contains("CPU 频率明显下降"));
    }

    [Fact]
    public void Analyze_MissingVoltage_ReturnsWarning()
    {
        var readings = Series(6, 20, _ => new HardwareReading
        {
            Voltage12V = 11.2,
        });

        var anomalies = HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray()));

        var anomaly = Assert.Single(anomalies);
        Assert.Equal(AnomalySeverity.Warning, anomaly.Severity);
        Assert.Contains("+12V 电压异常", anomaly.Title);
    }

    [Fact]
    public void Analyze_GpuHotspotOverCriticalLine_ReturnsCritical()
    {
        var readings = Series(6, 20, _ => new HardwareReading
        {
            GpuHotspot = 106,
        });

        var anomalies = HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray()));

        Assert.Contains(anomalies, a =>
            a.Severity == AnomalySeverity.Critical && a.Title.Contains("GPU 热点"));
    }

    [Fact]
    public void Analyze_FanStalledWhileHot_ReturnsWarning()
    {
        var readings = Series(6, 20, _ => new HardwareReading
        {
            CpuTemperature = 85,
            CpuFanRpm = 0,
        });

        var anomalies = HardwareAnomalyAnalyzer.Analyze(Window(readings.ToArray()));

        Assert.Contains(anomalies, a =>
            a.Severity == AnomalySeverity.Warning && a.Title.Contains("CPU 风扇转速为 0"));
    }

    [Fact]
    public void GetPreCrashReadings_ExcludesAfterCrashGraceAndSorts()
    {
        var snapshot = Window(
            new HardwareReading { Timestamp = Crash.AddSeconds(-10) },
            new HardwareReading { Timestamp = Crash.AddSeconds(5) },
            new HardwareReading { Timestamp = Crash.AddSeconds(1) },
            new HardwareReading { Timestamp = Crash.AddSeconds(-20) });

        var pre = HardwareAnomalyAnalyzer.GetPreCrashReadings(snapshot);

        Assert.Equal(3, pre.Count);
        Assert.Equal(Crash.AddSeconds(-20), pre[0].Timestamp);
        Assert.Equal(Crash.AddSeconds(1), pre[2].Timestamp);
    }
}
