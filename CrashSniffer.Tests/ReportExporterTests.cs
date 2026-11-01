using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

[Collection("HardwareSnapshotStore")]
public class ReportExporterTests : IDisposable
{
    private readonly string _tempZip;
    private readonly string _tempDir;

    public ReportExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CrashSniffer_ExportTestDir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempZip = Path.Combine(_tempDir, "test_report.zip");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Export_CreatesZipWithReportHtmlAndEventsJson()
    {
        var events = new List<CrashEvent>
        {
            new()
            {
                Time = DateTime.Now,
                Type = CrashType.BSOD,
                BugCheckCode = 0x124,
                StopCode = "WHEA_UNCORRECTABLE_ERROR",
                Summary = "BSOD 0x124",
                Troubleshooting = { "关闭所有超频" },
            }
        };

        var result = ReportExporter.Export(
            _tempZip,
            events,
            DateTime.Now.AddDays(-1),
            DateTime.Now,
            env: new EnvironmentSnapshot { CpuName = "Test CPU" });

        Assert.True(result.Success);
        Assert.True(File.Exists(_tempZip));
        Assert.Contains("report.html", result.FilesIncluded);
        Assert.Contains("events.json", result.FilesIncluded);
        Assert.Contains("environment.json", result.FilesIncluded);

        using var archive = ZipFile.OpenRead(_tempZip);
        Assert.NotNull(archive.GetEntry("report.html"));
        Assert.NotNull(archive.GetEntry("events.json"));
        Assert.NotNull(archive.GetEntry("environment.json"));
    }

    [Fact]
    public void Export_AutomaticallyPackagesMultipleDumpsAndEmbedsHtmlLinks()
    {
        string dmp1 = Path.Combine(_tempDir, "090526-22109-01.dmp");
        string dmp2 = Path.Combine(_tempDir, "WATCHDOG-20261008-1855.dmp");
        File.WriteAllBytes(dmp1, new byte[4096]);
        File.WriteAllBytes(dmp2, new byte[8192]);

        var ev = new CrashEvent
        {
            Time = DateTime.Now,
            Type = CrashType.BSOD,
            BugCheckCode = 0x141,
            StopCode = "VIDEO_ENGINE_TIMEOUT_DETECTED",
            Summary = "BSOD 0x141 amdkmdag.sys",
            DumpPath = dmp1,
            DumpSize = 4096,
            FaultingModuleOffset = "amdkmdag.sys+0x1D0FB0",
            SuspectDriver = "amdkmdag.sys [AMD 显卡驱动]",
        };
        ev.AddDumpPath(dmp1);
        ev.AddDumpPath(dmp2);

        var result = ReportExporter.Export(
            _tempZip,
            new List<CrashEvent> { ev },
            DateTime.Now.AddDays(-1),
            DateTime.Now);

        Assert.True(result.Success);
        Assert.Equal(2, result.ExportedDumps.Count);
        Assert.Contains(result.ExportedDumps, d => d.RelativeZipPath == "dumps/090526-22109-01.dmp");
        Assert.Contains(result.ExportedDumps, d => d.RelativeZipPath == "dumps/WATCHDOG-20261008-1855.dmp");

        using var archive = ZipFile.OpenRead(_tempZip);
        Assert.NotNull(archive.GetEntry("dumps/090526-22109-01.dmp"));
        Assert.NotNull(archive.GetEntry("dumps/WATCHDOG-20261008-1855.dmp"));

        using var reader = new StreamReader(archive.GetEntry("report.html")!.Open());
        string html = reader.ReadToEnd();
        Assert.Contains("dumps/090526-22109-01.dmp", html);
        Assert.Contains("dumps/WATCHDOG-20261008-1855.dmp", html);
        Assert.Contains("amdkmdag.sys+0x1D0FB0", html);
        Assert.Contains("copyReportPost()", html);
        Assert.Contains("filterEvents(", html);
    }

    [Fact]
    public void ExportEventDumpsToFolder_CopiesAllAssociatedDumps()
    {
        string dmp1 = Path.Combine(_tempDir, "crash_a.dmp");
        string dmp2 = Path.Combine(_tempDir, "crash_b.dmp");
        File.WriteAllBytes(dmp1, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(dmp2, new byte[] { 5, 6, 7, 8 });

        var ev = new CrashEvent { Time = DateTime.Now, DumpPath = dmp1 };
        ev.AddDumpPath(dmp1);
        ev.AddDumpPath(dmp2);

        string outFolder = Path.Combine(_tempDir, "exported_dumps");
        var (copiedCount, copiedFiles, errors) = ReportExporter.ExportEventDumpsToFolder(ev, outFolder);

        Assert.Equal(2, copiedCount);
        Assert.Empty(errors);
        Assert.Equal(2, copiedFiles.Count);
        Assert.All(copiedFiles, f => Assert.True(File.Exists(f)));
    }
}
