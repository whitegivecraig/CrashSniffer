using System;
using System.IO;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

[Collection("HardwareSnapshotStore")]
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
    public void SaveAndLoad_PersistsStorageDetails()
    {
        var snap = new HardwareSnapshot
        {
            EventId = "test_event_storage",
            CrashTime = new DateTime(2026, 10, 28, 12, 0, 0),
            CrashType = "BSOD",
            Readings = new()
            {
                new()
                {
                    Timestamp = new DateTime(2026, 10, 28, 11, 55, 0),
                    GpuMemoryTemperature = 88,
                    GpuMemoryUsedMb = 8192,
                    MemoryLoad = 62,
                    MoboMaxTemperature = 55,
                    Storages = new()
                    {
                        new()
                        {
                            Id = "/nvme/1",
                            Index = 1,
                            Name = "HP SSD SP803 2TB",
                            Volumes = { "E:" },
                            Temperature = 44,
                            LifePercent = 99,
                            UsedSpacePercent = 62.4,
                            ReadRateMBps = 12.5,
                            WriteRateMBps = 3.2,
                            TotalReadGb = 50891,
                            TotalWrittenGb = 36341,
                        },
                    },
                },
            },
        };

        HardwareSnapshotStore.Save(snap);
        var loaded = HardwareSnapshotStore.Load(snap.EventId);

        Assert.NotNull(loaded);
        var reading = Assert.Single(loaded!.Readings);
        Assert.Equal(88, reading.GpuMemoryTemperature!.Value, 3);
        Assert.Equal(8192, reading.GpuMemoryUsedMb!.Value, 3);
        Assert.Equal(62, reading.MemoryLoad!.Value, 3);
        Assert.Equal(55, reading.MoboMaxTemperature!.Value, 3);

        var storage = Assert.Single(reading.Storages);
        Assert.Equal("/nvme/1", storage.Id);
        Assert.Equal(1, storage.Index);
        Assert.Equal("E:", Assert.Single(storage.Volumes));
        Assert.Equal(44, storage.Temperature!.Value, 3);
        Assert.Equal(99, storage.LifePercent!.Value, 3);
        Assert.Equal(62.4, storage.UsedSpacePercent!.Value, 3);
        Assert.Equal(12.5, storage.ReadRateMBps!.Value, 3);
        Assert.Equal(36341, storage.TotalWrittenGb!.Value, 3);
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
    public void BuildEventId_TryParseCrashTime_RoundtripsToSecond()
    {
        var t = new DateTime(2026, 9, 17, 12, 34, 56, 789);
        string id = HardwareSnapshotStore.BuildEventId(t);

        Assert.True(HardwareSnapshotStore.TryParseCrashTime(id, out var parsed));
        Assert.Equal(new DateTime(2026, 9, 17, 12, 34, 56), parsed);
    }

    [Fact]
    public void TryParseCrashTime_UnknownId_ReturnsFalse()
    {
        Assert.False(HardwareSnapshotStore.TryParseCrashTime("e1", out _));
    }

    [Fact]
    public void ListEventIds_ReturnsSavedIds()
    {
        const string id = "crash_20260917_120000_000000";
        HardwareSnapshotStore.Save(new() { EventId = id, CrashTime = DateTime.Now });

        Assert.Contains(id, HardwareSnapshotStore.ListEventIds());
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
