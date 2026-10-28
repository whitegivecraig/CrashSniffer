using System;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HardwareCsvExporterTests
{
    [Fact]
    public void BuildCsv_WritesHeaderOnly_WhenNoReadings()
    {
        var snapshot = new HardwareSnapshot { CrashTime = DateTime.Now };

        var lines = HardwareCsvExporter.BuildCsv(snapshot)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.StartsWith("时间,相对崩溃(秒),CPU温度(℃)", lines[0]);
    }

    [Fact]
    public void BuildCsv_WritesAllColumnsInOrder()
    {
        var crash = new DateTime(2026, 10, 28, 12, 0, 0);
        var snapshot = new HardwareSnapshot
        {
            CrashTime = crash,
            Readings = new()
            {
                new()
                {
                    Timestamp = crash.AddSeconds(-10),
                    CpuTemperature = 85.5,
                    GpuTemperature = 76,
                    Voltage12V = 12.05,
                    CpuFanRpm = 1200,
                    SsdLife = 97,
                },
            },
        };

        var lines = HardwareCsvExporter.BuildCsv(snapshot)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var fields = lines[1].Split(',');

        Assert.Equal(2, lines.Length);
        Assert.Equal(20, fields.Length);
        Assert.Equal("2026-10-28 11:59:50.000", fields[0]);
        Assert.Equal("-10.0", fields[1]);
        Assert.Equal("85.50", fields[2]);
        Assert.Equal("76.00", fields[3]);
        Assert.Equal("12.05", fields[13]);
        Assert.Equal("1200.00", fields[17]);
        Assert.Equal("97.00", fields[19]);
    }

    [Fact]
    public void BuildCsv_OrdersReadingsByTime()
    {
        var crash = new DateTime(2026, 10, 28, 12, 0, 0);
        var snapshot = new HardwareSnapshot
        {
            CrashTime = crash,
            Readings = new()
            {
                new() { Timestamp = crash.AddSeconds(2), CpuTemperature = 70 },
                new() { Timestamp = crash.AddSeconds(-2), CpuTemperature = 60 },
            },
        };

        var lines = HardwareCsvExporter.BuildCsv(snapshot)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("2026-10-28 11:59:58.000,-2.0,60.00", lines[1]);
        Assert.StartsWith("2026-10-28 12:00:02.000,2.0,70.00", lines[2]);
    }
}
