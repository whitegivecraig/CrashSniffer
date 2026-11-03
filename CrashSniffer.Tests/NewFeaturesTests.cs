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

    [Fact]
    public void ProblematicDriverKnowledge_MatchAndEnrichEvent_DetectsHighRiskDrivers()
    {
        var rule1 = ProblematicDriverKnowledge.Match("AsIO.sys");
        Assert.NotNull(rule1);
        Assert.Equal("主板灯控/工具", rule1!.Category);

        var rule2 = ProblematicDriverKnowledge.Match("vgk.sys");
        Assert.NotNull(rule2);
        Assert.Equal("游戏反作弊", rule2!.Category);

        var rule3 = ProblematicDriverKnowledge.Match("ene.sys");
        Assert.NotNull(rule3);

        // 测试 EnrichEvent
        var ev = new CrashEvent
        {
            Type = CrashType.BSOD,
            BugCheckCode = 0x3B,
            SuspectDriver = "AsIO.sys",
        };

        ProblematicDriverKnowledge.EnrichEvent(ev);

        Assert.NotNull(ev.ProblematicDriverWarning);
        Assert.Contains("AsIO", ev.ProblematicDriverWarning);
        Assert.NotEmpty(ev.Troubleshooting);
        Assert.Contains("【高危驱动排查】", ev.Troubleshooting[0]);
    }

    [Fact]
    public void WinDbgService_ParseAnalyzeOutput_ExtractsStackAndBucket()
    {
        string mockOutput = @"
*******************************************************************************
*                                                                             *
*                        Bugcheck Analysis                                    *
*                                                                             *
*******************************************************************************

SYSTEM_SERVICE_EXCEPTION (3b)
An exception happened while executing a system service routine.
Arguments:
Arg1: 00000000c0000005, Exception code that caused the bugcheck
Arg2: fffff8013e4b1020, Address of the instruction which caused the bugcheck
Arg3: ffff9201a011ef90, Address of the context record for the exception that caused the bugcheck
Arg4: 0000000000000000, zero.

PROCESS_NAME:  LeagueClient.exe

STACK_TEXT:  
ffff9201`a011f818 fffff801`3e20d524     : 00000000`0000003b 00000000`c0000005 fffff801`3e4b1020 ffff9201`a011ef90 : nt!KeBugCheckEx
ffff9201`a011f820 fffff801`3e0984a2     : 00000000`00000000 00000000`00000000 00000000`00000000 00000000`00000000 : nt!KiSystemServiceHandler+0x7c
ffff9201`a011f860 fffff801`3e4b1020     : 00000000`00000001 00000000`00000002 00000000`00000003 00000000`00000004 : vgk+0x11020

FAILURE_BUCKET_ID:  0x3B_c0000005_vgk+11020
";

        var (bucket, procName, stack) = WinDbgService.ParseAnalyzeOutput(mockOutput);

        Assert.Equal("0x3B_c0000005_vgk+11020", bucket);
        Assert.Equal("LeagueClient.exe", procName);
        Assert.NotNull(stack);
        Assert.Contains("vgk+0x11020", stack);
        Assert.Contains("nt!KeBugCheckEx", stack);
    }

    [Fact]
    public void MinidumpParser_ParseReportWer_ExtractsKernelCrash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string werPath = Path.Combine(tempDir, "Report.wer");

        try
        {
            // 写入 UTF-16LE 格式的 Report.wer 文件
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Version=1");
            sb.AppendLine("EventType=BlueScreen");
            sb.AppendLine("EventTime=133456789012345678");
            sb.AppendLine("Sig[0].Name=BCCode");
            sb.AppendLine("Sig[0].Value=139");
            sb.AppendLine("Sig[1].Name=BCP1");
            sb.AppendLine("Sig[1].Value=3");
            sb.AppendLine("Sig[2].Name=BCP2");
            sb.AppendLine("Sig[2].Value=ffff800012345678");
            sb.AppendLine("Sig[3].Name=BCP3");
            sb.AppendLine("Sig[3].Value=ffff800098765432");
            sb.AppendLine("Sig[4].Name=BCP4");
            sb.AppendLine("Sig[4].Value=0");
            sb.AppendLine("OriginalFilename=ene.sys");

            File.WriteAllText(werPath, sb.ToString(), System.Text.Encoding.Unicode);

            var fi = new FileInfo(werPath);
            var ev = CrashSniffer.Collectors.MinidumpParser.ParseReportWer(werPath, fi);

            Assert.NotNull(ev);
            Assert.Equal(CrashType.BSOD, ev!.Type);
            Assert.Equal(0x139u, ev.BugCheckCode);
            Assert.Equal(3UL, ev.BugCheckParams[0]);
            Assert.Contains("ene.sys", ev.SuspectDriver);
            Assert.NotNull(ev.ProblematicDriverWarning);
            Assert.Contains("ene", ev.ProblematicDriverWarning);
            Assert.StartsWith("[WER 归档]", ev.Summary);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MinidumpParser_UnloadedModuleList_DetectsUnloadedDrivers()
    {
        // 构造带有 Stream 14 (UnloadedModuleListStream) 的 Minidump 文件
        string temp = Path.GetTempFileName();
        try
        {
            byte[] buf = new byte[0x2000];
            // Header: Signature ("MDMP" = 0x504D444D), Version (0xA793), NumberOfStreams (2), StreamDirRva (0x20)
            BitConverter.GetBytes(0x504D444Du).CopyTo(buf, 0x00);
            BitConverter.GetBytes((ushort)0xA793).CopyTo(buf, 0x04);
            BitConverter.GetBytes((ushort)2).CopyTo(buf, 0x06);
            BitConverter.GetBytes(0x20u).CopyTo(buf, 0x08);

            // Stream Dir Entry #0: StreamType = 0x11 (STREAM_BUGCHECK), DataSize = 40, Rva = 0x100
            BitConverter.GetBytes(0x11u).CopyTo(buf, 0x20);
            BitConverter.GetBytes(40u).CopyTo(buf, 0x24);
            BitConverter.GetBytes(0x100u).CopyTo(buf, 0x28);

            // BugCheck @ 0x100: Code = 0x3B, P1 = 0xC0000005, P2 = 0xFFFFF80012341050 (inside unloaded driver)
            ulong unloadedBase = 0xFFFFF80012340000UL;
            ulong faultAddr = unloadedBase + 0x1050UL;
            BitConverter.GetBytes(0x0000003Bu).CopyTo(buf, 0x100);
            BitConverter.GetBytes(0xC0000005UL).CopyTo(buf, 0x104);
            BitConverter.GetBytes(faultAddr).CopyTo(buf, 0x10C); // P2

            // Stream Dir Entry #1: StreamType = 0x0E (STREAM_UNLOADED_MODULE_LIST), DataSize = 36, Rva = 0x200
            BitConverter.GetBytes(0x0Eu).CopyTo(buf, 0x2C);
            BitConverter.GetBytes(36u).CopyTo(buf, 0x30);
            BitConverter.GetBytes(0x200u).CopyTo(buf, 0x34);

            // UnloadedModuleList @ 0x200:
            // SizeOfHeader = 12, SizeOfEntry = 24, NumberOfEntries = 1
            BitConverter.GetBytes(12u).CopyTo(buf, 0x200);
            BitConverter.GetBytes(24u).CopyTo(buf, 0x204);
            BitConverter.GetBytes(1u).CopyTo(buf, 0x208);

            // Entry #0 @ 0x20C: BaseOfImage = 0xFFFFF80012340000, SizeOfImage = 0x20000, Checksum = 0, TimeDateStamp = 0, ModuleNameRva = 0x300
            BitConverter.GetBytes(unloadedBase).CopyTo(buf, 0x20C);
            BitConverter.GetBytes(0x20000u).CopyTo(buf, 0x214);
            BitConverter.GetBytes(0u).CopyTo(buf, 0x218);
            BitConverter.GetBytes(0u).CopyTo(buf, 0x21C);
            BitConverter.GetBytes(0x300u).CopyTo(buf, 0x220);

            // Counted Unicode String @ 0x300: LengthBytes (30), "corsairvbus.sys\0"
            string modName = "corsairvbus.sys";
            byte[] strBytes = System.Text.Encoding.Unicode.GetBytes(modName);
            BitConverter.GetBytes((uint)strBytes.Length).CopyTo(buf, 0x300);
            strBytes.CopyTo(buf, 0x304);

            File.WriteAllBytes(temp, buf);

            var ev = CrashSniffer.Collectors.MinidumpParser.ParseFile(temp);
            Assert.NotNull(ev);
            Assert.Equal(0x3Bu, ev!.BugCheckCode);
            Assert.Contains("corsairvbus.sys", ev.UnloadedDrivers);
            Assert.Contains("corsairvbus.sys+0x1050 [已卸载]", ev.FaultingModuleOffset);
            Assert.NotNull(ev.ProblematicDriverWarning);
            Assert.Contains("corsairvbus", ev.ProblematicDriverWarning);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
