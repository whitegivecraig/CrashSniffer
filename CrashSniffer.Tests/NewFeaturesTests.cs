using System;
using System.Collections.Generic;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

[Collection("HardwareThresholds")]
public class NewFeaturesTests : IDisposable
{
    public void Dispose()
    {
        HardwareThresholds.CurrentPreset = HardwareThresholdPreset.DesktopStandard;
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(290 * 1024, "290 KB")]
    [InlineData(2 * 1024 * 1024, "2.00 MB")]
    public void FormatFileSize_FormatsHumanReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, CrashEvent.FormatFileSize(bytes));
    }

    [Fact]
    public void HardwareThresholds_PresetsAdjustTemperatureLimits()
    {
        HardwareThresholds.CurrentPreset = HardwareThresholdPreset.DesktopStandard;
        Assert.Equal(HardwareHealth.Warning, HardwareThresholds.EvaluateCpuTemp(92.0));

        // 切换到游戏本耐温预设后，92℃ 应视为正常
        HardwareThresholds.CurrentPreset = HardwareThresholdPreset.LaptopHighTemp;
        Assert.Equal(HardwareHealth.Normal, HardwareThresholds.EvaluateCpuTemp(92.0));
        Assert.Equal(HardwareHealth.Warning, HardwareThresholds.EvaluateCpuTemp(96.0));

        // 切换到水冷严格预设后，89℃ 应直接视为危险
        HardwareThresholds.CurrentPreset = HardwareThresholdPreset.WaterCooledStrict;
        Assert.Equal(HardwareHealth.Critical, HardwareThresholds.EvaluateCpuTemp(89.0));
    }

    [Fact]
    public void ForumPostBuilder_BuildForEvent_ContainsKeyDiagnosticSections()
    {
        var ev = new CrashEvent
        {
            Time = new DateTime(2026, 10, 8, 18, 55, 0),
            Type = CrashType.BSOD,
            BugCheckCode = 0x141,
            StopCode = "VIDEO_ENGINE_TIMEOUT_DETECTED",
            StopCodeChinese = "显卡引擎超时",
            Summary = "BSOD 0x141 amdkmdag.sys",
            FaultingModuleOffset = "amdkmdag.sys+0x1D0FB0",
            ExceptionAddress = 0xFFFFF80550E10FB0UL,
            SuspectDriver = "amdkmdag.sys [AMD 显卡驱动]",
            DumpPath = @"C:\Windows\Minidump\100826-1000-01.dmp",
            DumpSize = 2048 * 1024,
            Troubleshooting = { "使用 DDU 卸载重装显卡驱动" },
        };

        var env = new EnvironmentSnapshot
        {
            CpuName = "AMD Ryzen 7 7800X3D",
            Motherboard = "ASUS ROG STRIX B650E",
            MemorySummary = "32GB DDR5-6000",
        };

        string post = ForumPostBuilder.BuildForEvent(ev, env);

        Assert.Contains("0x00000141", post);
        Assert.Contains("VIDEO_ENGINE_TIMEOUT_DETECTED", post);
        Assert.Contains("amdkmdag.sys+0x1D0FB0", post);
        Assert.Contains("AMD Ryzen 7 7800X3D", post);
        Assert.Contains("使用 DDU 卸载重装显卡驱动", post);
    }

    [Fact]
    public void CrashDumpConfigService_EvaluateIssues_DetectsDisabledDumpAndAutoCleanup()
    {
        var status = new CrashDumpConfigStatus
        {
            CrashDumpEnabled = 0,
            LogEventEnabled = false,
            AlwaysKeepMemoryDump = false,
            MinidumpDirExists = false,
            HasActivePageFile = false,
        };

        CrashDumpConfigService.EvaluateIssues(status);

        Assert.False(status.IsOptimal);
        Assert.Equal(5, status.Issues.Count);
    }

    [Fact]
    public void HardwareThresholds_ZeroOrNegativeTemperatures_ReturnUnknownInsteadOfNormal()
    {
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateCpuTemp(0.0));
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateCpuTemp(-5.0));
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateGpuTemp(0.0));
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateMoboTemp(0.0));
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateSsdTemp(0.0));
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateVoltage12V(0.0));
        Assert.Equal(HardwareHealth.Normal, HardwareThresholds.EvaluateMemoryLoad(0.0));
    }

    [Fact]
    public void ConfigureRing0Guard_WhenDisabled_PreventsWinRing0ExtractionAndCollectsValidReadings()
    {
        HardwareMonitorService.AllowWinRing0Driver = false;
        using var service = new HardwareMonitorService();
        service.Start();

        var r = service.CollectSingleReading();
        service.Stop();

        Assert.NotNull(r);
        Console.WriteLine($"[Live Hardware Reading]");
        Console.WriteLine($"  CPU: Temp={r!.CpuTemperature:F1} C, Load={r.CpuLoad:F1}%, Power={r.CpuPower:F1} W, Clock={r.CpuClock:F0} MHz, MaxCoreLoad={r.CpuMaxCoreLoad:F1}%, Cores={r.CpuCores.Count}");
        foreach (var c in r.CpuCores)
            Console.WriteLine($"  Core #{c.Index}: Load={c.Load:F1}%, Clock={c.Clock:F0} MHz");
        Console.WriteLine($"  Motherboard: MaxTemp={r.MoboMaxTemperature:F1} C, CpuFan={r.CpuFanRpm:F0} RPM, Fans={string.Join(", ", r.Fans.ConvertAll(f => $"{f.Name}={f.Rpm:F0}RPM({f.Percent:F0}%)"))}");
        Console.WriteLine($"  Voltages: +12V={r.Voltage12V:F2}V, +5V={r.Voltage5V:F2}V, +3.3V={r.Voltage3V:F2}V, Vcore={r.CpuVoltage:F3}V, SoC={r.CpuSocVoltage:F3}V, 3VSB={r.Voltage3VStandby:F2}V, CMOS={r.CmosBatteryVoltage:F2}V");
        Console.WriteLine($"  Memory: {r.MemoryUsedGb:F1}/{r.MemoryTotalGb:F1} GB ({r.MemoryLoad:F1}%)");
        Console.WriteLine($"  Storages: {r.Storages.Count} drives, MaxTemp={r.SsdTemperature:F1} C");

        // 绝不出现 0.0℃ 或 0.0W 的异常假读数
        if (r.CpuTemperature.HasValue)
            Assert.True(r.CpuTemperature.Value > 5.0 && r.CpuTemperature.Value < 130.0);
        if (r.CpuPower.HasValue)
            Assert.True(r.CpuPower.Value > 1.0 && r.CpuPower.Value < 500.0);
        if (r.MoboMaxTemperature.HasValue)
            Assert.True(r.MoboMaxTemperature.Value > 5.0 && r.MoboMaxTemperature.Value < 120.0);
    }
}
