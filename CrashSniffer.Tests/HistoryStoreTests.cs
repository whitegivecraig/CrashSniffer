using System;
using System.Collections.Generic;
using System.IO;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _tempFile;

    public HistoryStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"CrashSniffer_HistoryTest_{Guid.NewGuid():N}.json");
        HistoryStore.FilePathOverrideForTests = _tempFile;
    }

    public void Dispose()
    {
        HistoryStore.FilePathOverrideForTests = null;
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        try { if (File.Exists(_tempFile + ".bak")) File.Delete(_tempFile + ".bak"); } catch { }
        try { if (File.Exists(_tempFile + ".tmp")) File.Delete(_tempFile + ".tmp"); } catch { }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        var records = HistoryStore.Load();
        Assert.Empty(records);
    }

    [Fact]
    public void AppendAndSave_DeduplicatesEventsByFingerprint()
    {
        DateTime crashTime = new DateTime(2026, 11, 1, 10, 30, 0);

        var event1 = new CrashEvent
        {
            Time = crashTime,
            Type = CrashType.BSOD,
            BugCheckCode = 0x124,
            StopCode = "WHEA_UNCORRECTABLE_ERROR",
            SuspectDriver = "genuineintel.sys [CPU]",
            Summary = "BSOD 0x124",
        };

        var event2Duplicate = new CrashEvent
        {
            Time = crashTime,
            Type = CrashType.BSOD,
            BugCheckCode = 0x124,
            StopCode = "WHEA_UNCORRECTABLE_ERROR",
            SuspectDriver = "genuineintel.sys",
            Summary = "BSOD 0x124 duplicate",
        };

        var event3Different = new CrashEvent
        {
            Time = crashTime.AddHours(1),
            Type = CrashType.GPU_TDR,
            SuspectDriver = "nvlddmkm.sys",
            Summary = "GPU TDR",
        };

        int added1 = HistoryStore.AppendAndSave(new[] { event1 });
        Assert.Equal(1, added1);

        int added2 = HistoryStore.AppendAndSave(new[] { event2Duplicate, event3Different });
        // event2Duplicate should be skipped by fingerprint
        Assert.Equal(1, added2);

        var all = HistoryStore.Load();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Load_WhenCorrupted_BacksUpAndReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "{ this is invalid json content }");

        var list = HistoryStore.Load();

        Assert.Empty(list);
        Assert.True(File.Exists(_tempFile + ".bak"));
    }
}
