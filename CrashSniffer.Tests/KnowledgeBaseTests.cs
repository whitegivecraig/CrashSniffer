using CrashSniffer.Models;
using Xunit;

namespace CrashSniffer.Tests;

public class KnowledgeBaseTests
{
    [Theory]
    [InlineData(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL")]
    [InlineData(0x0000001A, "MEMORY_MANAGEMENT")]
    [InlineData(0x0000003B, "SYSTEM_SERVICE_EXCEPTION")]
    [InlineData(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA")]
    [InlineData(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED")]
    [InlineData(0x0000009C, "MACHINE_CHECK_EXCEPTION")]
    [InlineData(0x000000C4, "DRIVER_VERIFIER_DETECTED_VIOLATION")]
    [InlineData(0x00000101, "CLOCK_WATCHDOG_TIMEOUT")]
    [InlineData(0x00000109, "CRITICAL_STRUCTURE_CORRUPTION")]
    [InlineData(0x0000010E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL")]
    [InlineData(0x00000113, "VIDEO_DXGKRNL_FATAL_ERROR")]
    [InlineData(0x00000116, "VIDEO_TDR_ERROR")]
    [InlineData(0x00000124, "WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(0x00000133, "DPC_WATCHDOG_VIOLATION")]
    [InlineData(0x00000139, "KERNEL_SECURITY_CHECK_FAILURE")]
    [InlineData(0x00000141, "VIDEO_TDR_FAILURE")]
    public void BugCheckKnowledge_KnownCodes_ReturnExpectedName(uint code, string expectedName)
    {
        var info = BugCheckKnowledge.Get(code);
        Assert.Equal(code, info.Code);
        Assert.Equal(expectedName, info.Name);
        Assert.False(string.IsNullOrWhiteSpace(info.Chinese));
        Assert.NotEmpty(info.Suggestions);
    }

    [Fact]
    public void BugCheckKnowledge_UnknownCode_ReturnsFormattedHex()
    {
        uint code = 0xDEADBEEF;
        var info = BugCheckKnowledge.Get(code);
        Assert.Equal(code, info.Code);
        Assert.Equal("UNKNOWN", info.Name);
        Assert.Contains("0xDEADBEEF", info.Chinese);
        Assert.NotEmpty(info.Suggestions);
    }

    [Theory]
    [InlineData("amdkmdag.sys", "AMD")]
    [InlineData("atikmdag.sys", "AMD")]
    [InlineData("nvlddmkm.sys", "NVIDIA")]
    [InlineData("igdkmd64.sys", "Intel")]
    [InlineData("igfx.sys", "Intel")]
    [InlineData("ntoskrnl.exe", "未知")]
    [InlineData("", "未知")]
    [InlineData(null, "未知")]
    public void DriverKnowledge_GuessGpuVendor_IdentifiesVendorsCorrectly(string? driver, string expectedVendor)
    {
        string vendor = DriverKnowledge.GuessGpuVendor(driver!);
        Assert.Equal(expectedVendor, vendor);
    }

    [Theory]
    [InlineData("amdkmdag.sys", "AMD 显卡内核模式驱动")]
    [InlineData("nvlddmkm.sys", "NVIDIA 显卡内核模式驱动")]
    [InlineData("stornvme.sys", "Windows 自带 NVMe 存储驱动")]
    [InlineData("vgk.sys", "Riot Vanguard 反作弊内核驱动")]
    [InlineData("easyanticheat.sys", "Easy Anti-Cheat 游戏反作弊驱动")]
    [InlineData("bedrive.sys", "BattlEye 游戏反作弊驱动")]
    [InlineData("randomdriver.sys", "第三方驱动 (randomdriver.sys)")]
    public void DriverKnowledge_Describe_ReturnsMeaningfulDescription(string driver, string expectedSubstr)
    {
        string desc = DriverKnowledge.Describe(driver);
        Assert.Contains(expectedSubstr, desc);
    }

    [Fact]
    public void DriverKnowledge_Describe_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(DriverKnowledge.Describe(""));
        Assert.Empty(DriverKnowledge.Describe("   "));
        Assert.Empty(DriverKnowledge.Describe(null!));
    }
}
