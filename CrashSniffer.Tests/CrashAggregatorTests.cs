using System;
using System.Collections.Generic;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class CrashAggregatorTests
{
    [Fact]
    public void AggregateEvents_GroupsEventsWithin30Minutes()
    {
        DateTime baseTime = new DateTime(2026, 11, 1, 12, 0, 0);

        var events = new List<CrashEvent>
        {
            new() { Time = baseTime, Type = CrashType.KernelPower_41, Summary = "KP41" },
            new() { Time = baseTime.AddMinutes(5), Type = CrashType.BSOD, BugCheckCode = 0x124, Summary = "BSOD" },
            new() { Time = baseTime.AddHours(2), Type = CrashType.Shutdown_6008, Summary = "6008" },
        };

        var merged = CrashAggregator.AggregateEvents(events);

        // Group 1: baseTime & baseTime + 5m -> merged into 1 event (BSOD leader)
        // Group 2: baseTime + 2h -> separate group
        Assert.Equal(2, merged.Count);

        var firstGroup = merged.First(e => e.Time <= baseTime.AddMinutes(10));
        Assert.Equal(CrashType.BSOD, firstGroup.Type);
        Assert.Equal(0x124u, firstGroup.BugCheckCode);
    }

    [Fact]
    public void AggregateEvents_PriorityOrder_BsodBeatsWheaAndTdrAnd41()
    {
        DateTime baseTime = new DateTime(2026, 11, 1, 14, 0, 0);

        var events = new List<CrashEvent>
        {
            new() { Time = baseTime, Type = CrashType.KernelPower_41, Summary = "KP41" },
            new() { Time = baseTime.AddSeconds(10), Type = CrashType.Shutdown_6008, Summary = "6008" },
            new() { Time = baseTime.AddSeconds(20), Type = CrashType.GPU_TDR, Summary = "TDR" },
            new() { Time = baseTime.AddSeconds(30), Type = CrashType.WHEA_Error, Summary = "WHEA" },
            new() { Time = baseTime.AddSeconds(40), Type = CrashType.BSOD, Summary = "BSOD" },
        };

        var merged = CrashAggregator.AggregateEvents(events);

        Assert.Single(merged);
        Assert.Equal(CrashType.BSOD, merged[0].Type);
    }

    [Fact]
    public void AggregateEvents_PrefersDumpLeaderOverNonDumpBsod()
    {
        DateTime baseTime = new DateTime(2026, 11, 1, 15, 0, 0);

        var nonDumpBsod = new CrashEvent
        {
            Time = baseTime,
            Type = CrashType.BSOD,
            BugCheckCode = 0x116,
            DumpPath = string.Empty,
            Summary = "Event 1001",
        };

        var dumpBsod = new CrashEvent
        {
            Time = baseTime.AddSeconds(5),
            Type = CrashType.BSOD,
            BugCheckCode = 0x116,
            DumpPath = @"C:\Windows\Minidump\test.dmp",
            Summary = "Minidump 0x116",
        };

        var merged = CrashAggregator.AggregateEvents(new[] { nonDumpBsod, dumpBsod });

        Assert.Single(merged);
        Assert.Equal(@"C:\Windows\Minidump\test.dmp", merged[0].DumpPath);
    }

    [Fact]
    public void AggregateEvents_LimitsMergedMessageLength()
    {
        DateTime baseTime = new DateTime(2026, 11, 1, 16, 0, 0);
        string longMessage = new string('A', 3000);

        var events = new List<CrashEvent>
        {
            new() { Time = baseTime, Type = CrashType.BSOD, EventMessage = longMessage },
            new() { Time = baseTime.AddMinutes(1), Type = CrashType.KernelPower_41, EventMessage = longMessage },
            new() { Time = baseTime.AddMinutes(2), Type = CrashType.Shutdown_6008, EventMessage = longMessage },
            new() { Time = baseTime.AddMinutes(3), Type = CrashType.WHEA_Error, EventMessage = longMessage },
        };

        var merged = CrashAggregator.AggregateEvents(events);

        Assert.Single(merged);
        Assert.True(merged[0].EventMessage.Length <= 8000);
    }

    [Fact]
    public void BuildSuspectRanking_RanksAndCleansDriverNames()
    {
        var events = new List<CrashEvent>
        {
            new() { SuspectDriver = "nvlddmkm.sys  [NVIDIA 显卡内核模式驱动]" },
            new() { SuspectDriver = "nvlddmkm.sys" },
            new() { SuspectDriver = "amdkmdag.sys [AMD 显卡驱动]" },
            new() { SuspectDriver = "stornvme.sys" },
            new() { SuspectDriver = "" },
            new() { SuspectDriver = "   " },
        };

        var ranking = CrashAggregator.BuildSuspectRanking(events);

        Assert.Equal(3, ranking.Count);
        Assert.Equal("nvlddmkm.sys", ranking[0].Driver);
        Assert.Equal(2, ranking[0].Count);
        Assert.Contains(ranking, r => r.Driver.Equals("amdkmdag.sys", StringComparison.OrdinalIgnoreCase) && r.Count == 1);
        Assert.Contains(ranking, r => r.Driver.Equals("stornvme.sys", StringComparison.OrdinalIgnoreCase) && r.Count == 1);
    }

    [Fact]
    public void AggregateEvents_PreservesAllDumpPathsAndModuleOffsetAcrossMergedGroup()
    {
        DateTime baseTime = new DateTime(2026, 10, 8, 18, 55, 0);

        var ev1 = new CrashEvent
        {
            Time = baseTime,
            Type = CrashType.BSOD,
            BugCheckCode = 0x116,
            DumpPath = @"C:\Windows\Minidump\100826-01.dmp",
            Summary = "BSOD 0x116",
        };

        var ev2 = new CrashEvent
        {
            Time = baseTime.AddMinutes(3),
            Type = CrashType.LiveKernel,
            BugCheckCode = 0x141,
            DumpPath = @"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-20261008-1855.dmp",
            FaultingModuleOffset = "amdkmdag.sys+0x1D0FB0",
            ExceptionAddress = 0xFFFFF80550E10FB0UL,
            Summary = "LiveKernel 0x141",
        };

        var merged = CrashAggregator.AggregateEvents(new[] { ev1, ev2 });

        Assert.Single(merged);
        var effectiveDumps = merged[0].GetEffectiveDumpPaths();
        Assert.Equal(2, effectiveDumps.Count);
        Assert.Contains(@"C:\Windows\Minidump\100826-01.dmp", effectiveDumps);
        Assert.Contains(@"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-20261008-1855.dmp", effectiveDumps);
        Assert.Equal("amdkmdag.sys+0x1D0FB0", merged[0].FaultingModuleOffset);
        Assert.Equal(0xFFFFF80550E10FB0UL, merged[0].ExceptionAddress);
    }
}
