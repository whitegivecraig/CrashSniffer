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

    /// <summary>BIOS 刷新 → 1 条 Bios 变更</summary>
    [Fact]
    public void Detect_BiosChanged_ReturnsBiosChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.BiosVersion = "2.1.0";
        neu.BiosDate = "2025-06-01";

        var changes = EnvironmentChangeDetector.Detect(old, neu);

        var c = Assert.Single(changes);
        Assert.Equal(ChangeCategory.Bios, c.Category);
        Assert.Contains("1.2.0", c.OldValue);
        Assert.Contains("2.1.0", c.NewValue);
    }

    /// <summary>Windows Build 变化 → WindowsUpdate 变更</summary>
    [Fact]
    public void Detect_OsBuildChanged_ReturnsWindowsUpdateChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.OsBuild = "22621.4";

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.WindowsUpdate, c.Category);
    }

    /// <summary>显卡驱动升级 → GpuDriver 变更，Item 含显卡名</summary>
    [Fact]
    public void Detect_GpuDriverChanged_ReturnsGpuDriverChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.Gpus[0].DriverVersion = "32.0.23013";
        neu.Gpus[0].DriverDate = "2025-01-15";

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.GpuDriver, c.Category);
        Assert.Contains("RX 7800 XT", c.Item);
        Assert.Contains("31.0.24010", c.OldValue);
        Assert.Contains("32.0.23013", c.NewValue);
    }

    /// <summary>内存实际频率 3600 → 2133（关 XMP）→ MemorySpeed 变更换带提示</summary>
    [Fact]
    public void Detect_MemorySpeedDown_HintsXmpOff()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.MemorySticks[0].ConfiguredSpeed = 2133;

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.MemorySpeed, c.Category);
        Assert.Contains("DIMM2", c.Item);
        Assert.Contains("2133", c.NewValue);
        Assert.Contains("XMP", c.NewValue); // 关闭提示
    }

    /// <summary>芯片组驱动版本变化 → PlatformDriver 变更</summary>
    [Fact]
    public void Detect_PlatformDriverChanged_ReturnsPlatformDriverChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.PlatformDrivers[0].Version = "3.1.0.55";

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.PlatformDriver, c.Category);
        Assert.Contains("amdgpio2.sys", c.Item);
    }

    /// <summary>新增显卡 + 移除内存条 → 2 条 Hardware 变更</summary>
    [Fact]
    public void Detect_HardwareAddedAndRemoved_ReturnsHardwareChanges()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.Gpus.Add(new GpuInfo { Name = "Intel Arc A310", DriverVersion = "1.0.1", DriverDate = "2024-05-01" });
        neu.MemorySticks.RemoveAt(1);

        var changes = EnvironmentChangeDetector.Detect(old, neu);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeCategory.Hardware, c.Category));
        Assert.Contains(changes, c => c.Item == "新增显卡" && c.NewValue == "Intel Arc A310");
        Assert.Contains(changes, c => c.Item == "移除内存条" && c.OldValue.Contains("KF436C16RB2"));
    }

    /// <summary>内存诊断从无错误 → 有错误 → MemoryDiag 变更</summary>
    [Fact]
    public void Detect_MemoryDiagDeteriorated_ReturnsMemoryDiagChange()
    {
        var old = BaseSnapshot();
        old.MemoryDiag.Found = true;
        old.MemoryDiag.ErrorsDetected = false;
        var neu = BaseSnapshot();
        neu.MemoryDiag.Found = true;
        neu.MemoryDiag.ErrorsDetected = true;

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.MemoryDiag, c.Category);
    }

    /// <summary>旧快照 GPU 采集失败（空列表）→ 不产生显卡相关变更（采集失败≠变更）</summary>
    [Fact]
    public void Detect_OldGpuCollectFailed_NoGpuChanges()
    {
        var old = BaseSnapshot();
        old.Gpus.Clear();
        old.CollectErrors.Add("GPU WMI 查询失败");
        var neu = BaseSnapshot();
        neu.Gpus[0].DriverVersion = "32.0.23013";

        var changes = EnvironmentChangeDetector.Detect(old, neu);
        Assert.DoesNotContain(changes, c => c.Category == ChangeCategory.GpuDriver);
        Assert.DoesNotContain(changes, c => c.Category == ChangeCategory.Hardware && c.Item.Contains("显卡"));
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
