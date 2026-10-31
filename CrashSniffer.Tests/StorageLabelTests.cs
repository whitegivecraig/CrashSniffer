using System;
using System.Collections.Generic;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class StorageLabelTests
{
    [Fact]
    public void OrderForDisplay_SortsByDriveLetter_UnletteredLast()
    {
        var drives = new[]
        {
            Drive(3, "/nvme/3", "C:"),
            Drive(0, "/hdd/0", "D:"),
            Drive(5, "/other/5"),
            Drive(1, "/nvme/1", "E:"),
            Drive(2, "/nvme/2", "F:"),
            Drive(4, "/hdd/4", "G:"),
        };

        var ordered = StorageLabel.OrderForDisplay(drives).Select(StorageLabel.Short).ToList();

        Assert.Equal(new[] { "C:", "D:", "E:", "F:", "G:", "磁盘5" }, ordered);
    }

    [Fact]
    public void Representatives_SortsByDriveLetter()
    {
        var snapshot = new HardwareSnapshot
        {
            CrashTime = new DateTime(2026, 10, 31, 12, 0, 0),
            Readings =
            {
                new HardwareReading
                {
                    Timestamp = new DateTime(2026, 10, 31, 11, 59, 0),
                    Storages =
                    {
                        Drive(3, "/nvme/3", "C:"),
                        Drive(0, "/hdd/0", "D:"),
                        Drive(1, "/nvme/1", "E:"),
                        Drive(2, "/nvme/2", "F:"),
                        Drive(4, "/hdd/4", "G:"),
                    },
                },
            },
        };

        var ordered = StorageLabel.Representatives(snapshot).Select(StorageLabel.Short).ToList();

        Assert.Equal(new[] { "C:", "D:", "E:", "F:", "G:" }, ordered);
    }

    private static StorageReading Drive(int index, string id, string? volume = null)
    {
        var drive = new StorageReading { Id = id, Index = index };
        if (volume != null) drive.Volumes.Add(volume);
        return drive;
    }
}
