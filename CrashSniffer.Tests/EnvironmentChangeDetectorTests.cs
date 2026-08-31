using Xunit;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.Tests;

public class EnvironmentChangeDetectorTests
{
    /// <summary>两份完全相同的快照 → 无变更</summary>
    [Fact]
    public void Detect_IdenticalSnapshots_ReturnsEmpty()
    {
        var changes = EnvironmentChangeDetector.Detect(BaseSnapshot(), BaseSnapshot());
        Assert.Empty(changes);
    }

    /// <summary>相同内容 → 相同指纹</summary>
    [Fact]
    public void FingerprintOf_IdenticalContent_SameFingerprint()
    {
        Assert.Equal(
            EnvironmentChangeDetector.FingerprintOf(BaseSnapshot()),
            EnvironmentChangeDetector.FingerprintOf(BaseSnapshot()));
    }

    /// <summary>驱动版本变化 → 指纹不同</summary>
    [Fact]
    public void FingerprintOf_DriverVersionChanged_DifferentFingerprint()
    {
        var a = BaseSnapshot();
        var b = BaseSnapshot();
        b.Gpus[0].DriverVersion = "32.0.23013";
        Assert.NotEqual(
            EnvironmentChangeDetector.FingerprintOf(a),
            EnvironmentChangeDetector.FingerprintOf(b));
    }

    /// <summary>一侧 GPU 列表为空（模拟 WMI 采集失败）→ 空侧不参与指纹，不误判</summary>
    [Fact]
    public void FingerprintOf_EmptyGpuSection_Ignored()
    {
        var a = BaseSnapshot();
        var b = BaseSnapshot();
        b.Gpus.Clear();
        b.CollectErrors.Add("GPU 采集失败");
        // 一侧为空时按"无该类别数据"处理：与去掉 GPU 段的指纹比较
        var c = BaseSnapshot();
        c.Gpus.Clear();
        Assert.Equal(
            EnvironmentChangeDetector.FingerprintOf(b),
            EnvironmentChangeDetector.FingerprintOf(c));
    }

    /// <summary>构造一份"完整"的基准快照，所有类别均有数据</summary>
    internal static EnvironmentSnapshot BaseSnapshot() => new()
    {
        CpuName = "AMD Ryzen 7 5700X",
        Motherboard = "B550M Mortar",
        BiosVersion = "1.2.0",
        BiosDate = "2024-01-01",
        OsName = "Windows 11 Pro",
        OsDisplayVersion = "23H2",
        OsBuild = "22621.1",
        MemorySummary = "32 GB DDR4",
        XmpNote = "XMP 已开启",
        Gpus =
        {
            new GpuInfo { Name = "AMD Radeon RX 7800 XT", DriverVersion = "31.0.24010", DriverDate = "2024-11-20", AdapterRamMb = 16384 },
        },
        // 注意：两根内存条 PartNumber 必须不同——硬件增删检测按 PartNumber 去重，
        // 相同料号无法体现"移除一根"
        MemorySticks =
        {
            new MemoryStickInfo { Slot = "DIMM2", CapacityGb = 16, Speed = 3600, ConfiguredSpeed = 3600, Manufacturer = "Kingston", PartNumber = "KF436C16RB1" },
            new MemoryStickInfo { Slot = "DIMM4", CapacityGb = 16, Speed = 3600, ConfiguredSpeed = 3600, Manufacturer = "Kingston", PartNumber = "KF436C16RB2" },
        },
        PlatformDrivers =
        {
            new PlatformDriverInfo { FileName = "amdgpio2.sys", Description = "AMD GPIO", Version = "2.2.0.130" },
        },
    };
}
