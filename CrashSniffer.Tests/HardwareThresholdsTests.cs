using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HardwareThresholdsTests
{
    [Theory]
    [InlineData(85.0, HardwareHealth.Normal)]
    [InlineData(92.0, HardwareHealth.Warning)]
    [InlineData(97.0, HardwareHealth.Critical)]
    public void EvaluateCpuTemp_ReturnsCorrectLevel(double temp, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateCpuTemp(temp));
    }

    [Theory]
    [InlineData(75.0, HardwareHealth.Normal)]
    [InlineData(82.0, HardwareHealth.Warning)]
    [InlineData(90.0, HardwareHealth.Critical)]
    public void EvaluateGpuTemp_ReturnsCorrectLevel(double temp, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateGpuTemp(temp));
    }

    [Theory]
    [InlineData(12.0, HardwareHealth.Normal)]   // 偏差 0%
    [InlineData(11.3, HardwareHealth.Warning)]  // 偏差 ~5.8%
    [InlineData(10.0, HardwareHealth.Critical)] // 偏差 ~16.7%
    [InlineData(13.5, HardwareHealth.Critical)] // 偏差 ~12.5%
    public void EvaluateVoltage12V_ReturnsCorrectLevel(double v, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateVoltage12V(v));
    }

    [Fact]
    public void EvaluateOverall_NoReadings_ReturnsUnknown()
    {
        var r = new HardwareReading();
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateOverall(r));
    }

    [Fact]
    public void EvaluateOverall_CriticalItem_ReturnsCritical()
    {
        var r = new HardwareReading { CpuTemperature = 100 };
        Assert.Equal(HardwareHealth.Critical, HardwareThresholds.EvaluateOverall(r));
    }

    [Fact]
    public void EvaluateTemp_Null_ReturnsUnknown()
    {
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateCpuTemp(null));
    }

    [Theory]
    [InlineData(3.0, HardwareHealth.Normal)]
    [InlineData(2.7, HardwareHealth.Warning)]
    [InlineData(2.4, HardwareHealth.Critical)]
    public void EvaluateCmosBattery_ReturnsCorrectLevel(double voltage, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateCmosBattery(voltage));
    }

    [Theory]
    [InlineData(100.0, 10.0, HardwareHealth.Normal)]
    [InlineData(10.0, 10.0, HardwareHealth.Warning)]
    [InlineData(0.0, 10.0, HardwareHealth.Critical)]
    [InlineData(null, 10.0, HardwareHealth.Unknown)]
    [InlineData(100.0, null, HardwareHealth.Unknown)]
    public void EvaluateAvailableSpare_ReturnsCorrectLevel(double? spare, double? threshold, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateAvailableSpare(spare, threshold));
    }

    [Fact]
    public void EvaluateOverall_LowSpare_ReturnsWarning()
    {
        var r = new HardwareReading
        {
            Storages = new()
            {
                new()
                {
                    Id = "/nvme/0",
                    Temperature = 40,
                    LifePercent = 90,
                    AvailableSparePercent = 8,
                    SpareThresholdPercent = 10,
                },
            },
        };

        Assert.Equal(HardwareHealth.Warning, HardwareThresholds.EvaluateOverall(r));
    }

    [Fact]
    public void EvaluateOverall_LowCmosBattery_ReturnsWarning()
    {
        var r = new HardwareReading { CmosBatteryVoltage = 2.7 };
        Assert.Equal(HardwareHealth.Warning, HardwareThresholds.EvaluateOverall(r));
    }
}
