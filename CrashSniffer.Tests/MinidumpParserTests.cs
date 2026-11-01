using System;
using System.IO;
using System.Text;
using CrashSniffer.Collectors;
using Xunit;

namespace CrashSniffer.Tests;

public class MinidumpParserTests
{
    [Fact]
    public void ParseFile_NonExistentFile_ReturnsNull()
    {
        var ev = MinidumpParser.ParseFile(@"C:\NonExistentDirectory\FakeDump.dmp");
        Assert.Null(ev);
    }

    [Fact]
    public void ParseFile_TooSmallFile_ReturnsNull()
    {
        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, new byte[10]);
            var ev = MinidumpParser.ParseFile(temp);
            Assert.Null(ev);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    [Fact]
    public void ParseFile_InvalidSignature_ReturnsNull()
    {
        string temp = Path.GetTempFileName();
        try
        {
            byte[] bytes = new byte[64];
            bytes[0] = 0xAA;
            bytes[1] = 0xBB;
            bytes[2] = 0xCC;
            bytes[3] = 0xDD;
            File.WriteAllBytes(temp, bytes);

            var ev = MinidumpParser.ParseFile(temp);
            Assert.Null(ev);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    [Fact]
    public void ParseFile_PageDu64TriageDump_ExtractsBugCheckAndDriverOffset()
    {
        string temp = Path.GetTempFileName();
        try
        {
            // 构造真实的 64 位内核 PAGEDU64 Triage Dump 结构
            byte[] buf = new byte[0x3000];
            Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(buf, 0);

            // 0x38: BugCheckCode = 0x141 (VIDEO_ENGINE_TIMEOUT_DETECTED)
            BitConverter.GetBytes(0x00000141u).CopyTo(buf, 0x38);
            // 0x3C: 4 字节对齐填充 "PAGE"
            Encoding.ASCII.GetBytes("PAGE").CopyTo(buf, 0x3C);
            // 0x40..0x5F: 4 个 8 字节 BugCheckParameter
            ulong driverBase = 0xFFFFF80550C40000UL;
            ulong faultAddr = driverBase + 0x1D0FB0UL;
            BitConverter.GetBytes(0xFFFFCF8F54A0E010UL).CopyTo(buf, 0x40);
            BitConverter.GetBytes(faultAddr).CopyTo(buf, 0x48);
            BitConverter.GetBytes(0UL).CopyTo(buf, 0x50);
            BitConverter.GetBytes(0xFFFFCF8F4F4D10C0UL).CopyTo(buf, 0x58);

            // 0xF88: DumpType = 4 (Triage Dump)
            BitConverter.GetBytes(4u).CopyTo(buf, 0xF88);

            // 0x2000: _TRIAGE_DUMP64
            int triageBase = 0x2000;
            BitConverter.GetBytes(0x00010004u).CopyTo(buf, triageBase + 0x00); // ServicePackBuild
            BitConverter.GetBytes(0x2000u).CopyTo(buf, triageBase + 0x04);     // SizeOfDump
            BitConverter.GetBytes(0xFFFFu).CopyTo(buf, triageBase + 0x08);     // ValidOffset
            BitConverter.GetBytes(0x2100u).CopyTo(buf, triageBase + 0x28);     // CallStackOffset
            BitConverter.GetBytes(16u).CopyTo(buf, triageBase + 0x2C);         // SizeOfCallStack (2 qwords)
            BitConverter.GetBytes(0x2200u).CopyTo(buf, triageBase + 0x30);     // DriverListOffset
            BitConverter.GetBytes(1u).CopyTo(buf, triageBase + 0x34);          // DriverCount = 1

            // CallStack @ 0x2100
            BitConverter.GetBytes(faultAddr).CopyTo(buf, 0x2100);

            // DriverEntry #0 @ 0x2200 (stride 0x90)
            BitConverter.GetBytes(0x2400u).CopyTo(buf, 0x2200 + 0x00);         // DriverNameOffset = 0x2400
            BitConverter.GetBytes(driverBase).CopyTo(buf, 0x2200 + 0x38);      // DllBase
            BitConverter.GetBytes(0x05000000u).CopyTo(buf, 0x2200 + 0x48);     // SizeOfImage

            // Counted Unicode string @ 0x2400
            string drvName = @"\SystemRoot\System32\DriverStore\amdkmdag.sys";
            byte[] strBytes = Encoding.Unicode.GetBytes(drvName);
            BitConverter.GetBytes((uint)strBytes.Length).CopyTo(buf, 0x2400);
            strBytes.CopyTo(buf, 0x2404);

            File.WriteAllBytes(temp, buf);

            var ev = MinidumpParser.ParseFile(temp);
            Assert.NotNull(ev);
            Assert.Equal(0x141u, ev!.BugCheckCode);
            Assert.False(string.IsNullOrWhiteSpace(ev.StopCode));
            Assert.Equal(faultAddr, ev.BugCheckParams[1]);
            Assert.Contains("amdkmdag.sys", ev.SuspectDriver);
            Assert.Equal("amdkmdag.sys+0x1D0FB0", ev.FaultingModuleOffset);
            Assert.Contains(temp, ev.AllDumpPaths);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
