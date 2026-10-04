using System;
using System.IO;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HardwareSnapshotStoreTests : IDisposable
{
    private readonly string _testDir;

    public HardwareSnapshotStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"hw_test_{Guid.NewGuid():N}");
        HardwareSnapshotStore.StorageDirOverride = _testDir;
    }

    public void Dispose()
    {
        HardwareSnapshotStore.StorageDirOverride = null;
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void SaveAndLoad_Roundtrip()
    {
        var snap = new HardwareSnapshot
        {
            EventId = "test_event_1",
            CrashTime = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Local),
            CrashType = "BSOD",
            Readings = new()
            {
                new() { Timestamp = new DateTime(2026, 9, 17, 11, 55, 0), CpuTemperature = 85, GpuTemperature = 75 },
            }
        };

        HardwareSnapshotStore.Save(snap);
        var loaded = HardwareSnapshotStore.Load(snap.EventId);

        Assert.NotNull(loaded);
        Assert.Equal(snap.EventId, loaded!.EventId);
        Assert.Equal(snap.CrashTime, loaded.CrashTime);
        Assert.Single(loaded.Readings);
        Assert.Equal(85, loaded.Readings[0].CpuTemperature);
    }

    [Fact]
    public void Load_NonExistent_ReturnsNull()
    {
        Assert.Null(HardwareSnapshotStore.Load("does_not_exist"));
    }

    [Fact]
    public void ListSnapshots_ReturnsAll()
    {
        HardwareSnapshotStore.Save(new() { EventId = "e1", CrashTime = DateTime.Now });
        HardwareSnapshotStore.Save(new() { EventId = "e2", CrashTime = DateTime.Now });
        var list = HardwareSnapshotStore.ListSnapshots();
        Assert.Contains(list, s => s.EventId == "e1");
        Assert.Contains(list, s => s.EventId == "e2");
    }

    [Fact]
    public void Save_ExceedsLimit_RemovesOldest()
    {
        var oldLimit = HardwareSnapshotStore.MaxSnapshots;
        try
        {
            HardwareSnapshotStore.MaxSnapshots = 3;
            for (int i = 0; i < 5; i++)
                HardwareSnapshotStore.Save(new()
                {
                    EventId = $"e{i}",
                    CrashTime = DateTime.Now.AddMinutes(i)
                });

            var list = HardwareSnapshotStore.ListSnapshots();
            Assert.Equal(3, list.Count);
            Assert.DoesNotContain(list, s => s.EventId == "e0");
            Assert.DoesNotContain(list, s => s.EventId == "e1");
            Assert.Contains(list, s => s.EventId == "e4");
        }
        finally
        {
            HardwareSnapshotStore.MaxSnapshots = oldLimit;
        }
    }
}
