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
}
