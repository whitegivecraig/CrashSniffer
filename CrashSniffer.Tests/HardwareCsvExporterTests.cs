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
                    CpuLoad = 40,
                    CpuMaxCoreLoad = 88,
                    CpuClock = 4200,
                    CpuPower = 120,
                    GpuTemperature = 76,
                    GpuHotspot = 90,
                    GpuMemoryTemperature = 88,
                    GpuLoad = 70,
                    GpuMemoryLoad = 55,
                    GpuClock = 2000,
                    GpuMemoryClock = 9000,
                    GpuPower = 180,
                    GpuMemoryUsedMb = 6144,
                    GpuMemoryTotalMb = 8192,
                    GpuFanPercent = 45,
                    GpuVoltage = 1.05,
                    MemoryLoad = 63,
                    VirtualMemoryLoad = 40,
                    MemoryUsedGb = 20.1,
                    MemoryTotalGb = 32,
                    VirtualMemoryUsedGb = 12.5,
                    VirtualMemoryTotalGb = 40,
                    MoboMaxTemperature = 55,
                    Voltage12V = 12.05,
                    Voltage5V = 5.01,
                    Voltage3V = 3.31,
                    Voltage3VStandby = 3.29,
                    CpuVoltage = 1.35,
                    CpuSocVoltage = 1.10,
                    CmosBatteryVoltage = 3.02,
                    CpuFanRpm = 1200,
                    GpuFanRpm = 1500,
                    SsdTemperature = 46,
                    SsdLife = 97,
                },
            },
        };

        var lines = HardwareCsvExporter.BuildCsv(snapshot)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        var fields = lines[1].Split(',');

        Assert.Equal(2, lines.Length);
        Assert.Equal(38, header.Length);
        Assert.Equal(38, fields.Length);
        Assert.Equal("2026-10-28 11:59:50.000", fields[0]);
        Assert.Equal("-10.0", fields[1]);
        Assert.Equal("85.50", fields[2]);
        Assert.Equal("88.00", fields[4]);
        Assert.Equal("76.00", fields[7]);
        Assert.Equal("88.00", fields[9]);
        Assert.Equal("55.00", fields[11]);
        Assert.Equal("45.00", fields[17]);
        Assert.Equal("1.05", fields[18]);
        Assert.Equal("63.00", fields[19]);
        Assert.Equal("20.10", fields[21]);
        Assert.Equal("55.00", fields[25]);
        Assert.Equal("12.05", fields[26]);
        Assert.Equal("3.29", fields[29]);
        Assert.Equal("1.10", fields[31]);
        Assert.Equal("3.02", fields[32]);
        Assert.Equal("1200.00", fields[33]);
        Assert.Equal("46.00", fields[35]);
        Assert.Equal("97.00", fields[36]);
    }

    [Fact]
    public void BuildCsv_AppendsPerDriveColumns()
    {
        var crash = new DateTime(2026, 10, 28, 12, 0, 0);
        var snapshot = new HardwareSnapshot
        {
            CrashTime = crash,
            Readings = new()
            {
                new()
                {
                    Timestamp = crash.AddSeconds(-2),
                    CpuTemperature = 50,
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
                            AvailableSparePercent = 100,
                            SpareThresholdPercent = 10,
                        },
                    },
                },
            },
        };

        var lines = HardwareCsvExporter.BuildCsv(snapshot)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        var fields = lines[1].Split(',');

        Assert.Equal(42, header.Length);
        Assert.Equal("磁盘 E: 温度(℃)", header[38]);
        Assert.Equal("磁盘 E: 寿命(%)", header[39]);
        Assert.Equal("磁盘 E: 已用(%)", header[40]);
        Assert.Equal("磁盘 E: 备用块(%)", header[41]);
        Assert.Equal(42, fields.Length);
        Assert.Equal("44.00", fields[38]);
        Assert.Equal("99.00", fields[39]);
        Assert.Equal("62.40", fields[40]);
        Assert.Equal("100.00", fields[41]);
    }

    [Fact]
    public void BuildCsv_AppendsPerCoreAndPerFanColumns()
    {
        var crash = new DateTime(2026, 10, 28, 12, 0, 0);
        var snapshot = new HardwareSnapshot
        {
            CrashTime = crash,
            Readings = new()
            {
                new()
                {
                    Timestamp = crash.AddSeconds(-2),
                    CpuCores = new()
                    {
                        new() { Index = 1, Load = 50, Clock = 4000 },
                        new() { Index = 2, Load = 30, Clock = 4100 },
                    },
                    Fans = new()
                    {
                        new() { Name = "Fan #1", Rpm = 800, Percent = 40 },
                        new() { Name = "Fan #2", Rpm = 1000, Percent = 55 },
                    },
                },
            },
        };

        var lines = HardwareCsvExporter.BuildCsv(snapshot)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        var fields = lines[1].Split(',');

        Assert.Equal(46, header.Length);
        Assert.Equal("核心 1 负载(%)", header[38]);
        Assert.Equal("核心 1 频率(MHz)", header[39]);
        Assert.Equal("核心 2 负载(%)", header[40]);
        Assert.Equal("核心 2 频率(MHz)", header[41]);
        Assert.Equal("风扇 1 转速(RPM)", header[42]);
        Assert.Equal("风扇 1 占空比(%)", header[43]);
        Assert.Equal("风扇 2 转速(RPM)", header[44]);
        Assert.Equal("风扇 2 占空比(%)", header[45]);
        Assert.Equal(46, fields.Length);
        Assert.Equal("50.00", fields[38]);
        Assert.Equal("4000.00", fields[39]);
        Assert.Equal("30.00", fields[40]);
        Assert.Equal("4100.00", fields[41]);
        Assert.Equal("800.00", fields[42]);
        Assert.Equal("40.00", fields[43]);
        Assert.Equal("1000.00", fields[44]);
        Assert.Equal("55.00", fields[45]);
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
