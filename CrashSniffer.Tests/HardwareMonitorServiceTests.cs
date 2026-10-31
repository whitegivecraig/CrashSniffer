using System;
using System.IO;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

[Collection("HardwareSnapshotStore")]
public class HardwareMonitorServiceTests : IDisposable
{
    private readonly string _testDir;

    public HardwareMonitorServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"hw_mon_test_{Guid.NewGuid():N}");
        HardwareSnapshotStore.StorageDirOverride = _testDir;
    }

    public void Dispose()
    {
        HardwareSnapshotStore.StorageDirOverride = null;
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void TryRecordCrashWindow_NoBufferedReadings_WritesNothing()
    {
        using var service = new HardwareMonitorService();

        Assert.False(service.TryRecordCrashWindow(DateTime.Now, "Bsod"));
        Assert.Empty(HardwareSnapshotStore.ListEventIds());
    }

    [Fact]
    public void TryRecordCrashWindow_FutureCrashTime_WritesNothing()
    {
        using var service = new HardwareMonitorService();

        Assert.False(service.TryRecordCrashWindow(DateTime.Now.AddMinutes(30), "Bsod"));
        Assert.Empty(HardwareSnapshotStore.ListEventIds());
    }

    [Theory]
    [InlineData("/nvme/1", 1)]
    [InlineData("/hdd/0", 0)]
    [InlineData("/hdd/4", 4)]
    [InlineData("/", -1)]
    [InlineData("", -1)]
    [InlineData("/nvme/", -1)]
    [InlineData("/nvme/abc", -1)]
    public void ParseStorageIndex_ReturnsSuffixNumber(string identifier, int expected)
    {
        Assert.Equal(expected, HardwareMonitorService.ParseStorageIndex(identifier));
    }

    [Theory]
    [InlineData("Fan #1", 1)]
    [InlineData("Fan #12", 12)]
    [InlineData("CPU Fan #2", 2)]
    [InlineData("CPU Fan", -1)]
    [InlineData("", -1)]
    [InlineData("Fan #", -1)]
    [InlineData("Fan #abc", -1)]
    public void ParseTrailingIndex_ReturnsSuffixNumber(string name, int expected)
    {
        Assert.Equal(expected, HardwareMonitorService.ParseTrailingIndex(name));
    }
}
