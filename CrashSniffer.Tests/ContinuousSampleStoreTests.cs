using System;
using System.IO;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class ContinuousSampleStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly int _oldMaxFiles;

    public ContinuousSampleStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"hw_cont_{Guid.NewGuid():N}");
        _oldMaxFiles = ContinuousSampleStore.MaxFiles;
        ContinuousSampleStore.StorageDirOverride = _testDir;
    }

    public void Dispose()
    {
        ContinuousSampleStore.StorageDirOverride = null;
        ContinuousSampleStore.MaxFiles = _oldMaxFiles;
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void Append_ThenLoadRange_ReturnsSortedReadings()
    {
        var t0 = new DateTime(2026, 10, 28, 10, 0, 0);
        ContinuousSampleStore.Append(new[]
        {
            Reading(t0.AddSeconds(4), 61),
            Reading(t0, 60),
            Reading(t0.AddSeconds(2), 62),
        });

        var loaded = ContinuousSampleStore.LoadRange(t0.AddMinutes(-1), t0.AddMinutes(1));

        Assert.Equal(3, loaded.Count);
        Assert.Equal(new double?[] { 60, 62, 61 }, loaded.Select(r => r.CpuTemperature).ToArray());
    }

    [Fact]
    public void Append_ThenLoadRange_RoundtripsStorageDetails()
    {
        var t0 = new DateTime(2026, 10, 28, 10, 0, 0);
        ContinuousSampleStore.Append(new[]
        {
            new HardwareReading
            {
                Timestamp = t0,
                MemoryLoad = 44.4,
                Storages = new()
                {
                    new()
                    {
                        Id = "/nvme/3",
                        Index = 3,
                        Name = "SanDisk Ultra 3D NVMe",
                        Volumes = { "C:" },
                        Temperature = 49,
                        LifePercent = 97,
                    },
                },
            },
        });

        var reading = Assert.Single(ContinuousSampleStore.LoadRange(t0.AddSeconds(-1), t0.AddSeconds(1)));

        Assert.Equal(44.4, reading.MemoryLoad!.Value, 3);
        var storage = Assert.Single(reading.Storages);
        Assert.Equal("/nvme/3", storage.Id);
        Assert.Equal("C:", Assert.Single(storage.Volumes));
        Assert.Equal(49, storage.Temperature!.Value, 3);
        Assert.Equal(97, storage.LifePercent!.Value, 3);
    }

    [Fact]
    public void Append_SplitsFilesByHour()
    {
        var t0 = new DateTime(2026, 10, 28, 10, 59, 50);
        ContinuousSampleStore.Append(new[] { Reading(t0, 50), Reading(t0.AddSeconds(20), 51) });

        Assert.Single(Directory.GetFiles(_testDir, "samples_20261028_10.jsonl"));
        Assert.Single(Directory.GetFiles(_testDir, "samples_20261028_11.jsonl"));
    }

    [Fact]
    public void LoadRange_ExcludesOutOfRangeReadings()
    {
        var t0 = new DateTime(2026, 10, 28, 10, 0, 0);
        ContinuousSampleStore.Append(new[]
        {
            Reading(t0.AddMinutes(-30), 40),
            Reading(t0, 55),
            Reading(t0.AddMinutes(30), 70),
        });

        var loaded = ContinuousSampleStore.LoadRange(t0.AddMinutes(-5), t0.AddMinutes(5));

        Assert.Single(loaded);
        Assert.Equal(55, loaded[0].CpuTemperature);
    }

    [Fact]
    public void BuildSnapshotForCrash_ReturnsOnlyWindowReadings()
    {
        var crash = new DateTime(2026, 10, 28, 12, 0, 0);
        ContinuousSampleStore.Append(new[]
        {
            Reading(crash.AddMinutes(-10), 40),
            Reading(crash.AddMinutes(-3), 70),
            Reading(crash.AddSeconds(30), 88),
            Reading(crash.AddMinutes(3), 90),
        });

        var snap = ContinuousSampleStore.BuildSnapshotForCrash(crash);

        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Readings.Count);
        Assert.Equal("连续采样", snap.CrashType);
        Assert.Equal(crash, snap.CrashTime);
        Assert.Equal(70, snap.Readings[0].CpuTemperature);
        Assert.Equal(88, snap.Readings[1].CpuTemperature);
    }

    [Fact]
    public void BuildSnapshotForCrash_NoSamples_ReturnsNull()
    {
        Assert.Null(ContinuousSampleStore.BuildSnapshotForCrash(DateTime.Now));
    }

    [Fact]
    public void Append_ExceedsMaxFiles_DeletesOldestFile()
    {
        ContinuousSampleStore.MaxFiles = 2;
        var day = new DateTime(2026, 10, 26, 8, 0, 0);

        for (int i = 0; i < 3; i++)
        {
            var ts = day.AddHours(i);
            ContinuousSampleStore.Append(new[] { Reading(ts, 50 + i) });
            string path = Path.Combine(_testDir, $"samples_{ts:yyyyMMdd_HH}.jsonl");
            File.SetLastWriteTimeUtc(path, ts.ToUniversalTime());
        }

        var files = Directory.GetFiles(_testDir, "samples_*.jsonl")
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(2, files.Count);
        Assert.DoesNotContain("samples_20261026_08.jsonl", files);
        Assert.Contains("samples_20261026_09.jsonl", files);
        Assert.Contains("samples_20261026_10.jsonl", files);
    }

    [Fact]
    public void GetLatestSampleTime_NoFiles_ReturnsNull()
    {
        Assert.Null(ContinuousSampleStore.GetLatestSampleTime());
    }

    [Fact]
    public void GetLatestSampleTime_ReturnsNewestFileTime()
    {
        var t0 = new DateTime(2026, 10, 28, 9, 0, 0);
        ContinuousSampleStore.Append(new[] { Reading(t0, 50) });
        string path = Path.Combine(_testDir, "samples_20261028_09.jsonl");
        var expected = new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, expected);

        Assert.Equal(expected.ToLocalTime(), ContinuousSampleStore.GetLatestSampleTime());
    }

    [Theory]
    [InlineData("samples_20261028_13", true)]
    [InlineData("samples_2026102", false)]
    [InlineData("other_20261028_13", false)]
    public void TryParseFileHour_ParsesOnlyExpectedNames(string name, bool expected)
    {
        Assert.Equal(expected, ContinuousSampleStore.TryParseFileHour(name, out var hour));
        if (expected) Assert.Equal(new DateTime(2026, 10, 28, 13, 0, 0), hour);
    }

    private static HardwareReading Reading(DateTime ts, double cpuTemp)
        => new() { Timestamp = ts, CpuTemperature = cpuTemp };
}
