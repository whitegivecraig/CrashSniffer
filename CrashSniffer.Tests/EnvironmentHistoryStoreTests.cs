using Xunit;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.Tests;

public class EnvironmentHistoryStoreTests : IDisposable
{
    private readonly string _tmpFile;

    public EnvironmentHistoryStoreTests()
    {
        _tmpFile = Path.Combine(Path.GetTempPath(), $"envhist_{Guid.NewGuid():N}.json");
        EnvironmentHistoryStore.FilePathOverrideForTests = _tmpFile;
    }

    public void Dispose()
    {
        EnvironmentHistoryStore.FilePathOverrideForTests = null;
        try { if (File.Exists(_tmpFile)) File.Delete(_tmpFile); } catch { }
        try { if (File.Exists(_tmpFile + ".bak")) File.Delete(_tmpFile + ".bak"); } catch { }
        try { if (File.Exists(_tmpFile + ".tmp")) File.Delete(_tmpFile + ".tmp"); } catch { }
    }

    /// <summary>冷启动：首份快照作为基线，不产生变更</summary>
    [Fact]
    public void RecordSnapshot_ColdStart_StoresBaselineWithoutChanges()
    {
        int added = EnvironmentHistoryStore.RecordSnapshot(EnvironmentChangeDetectorTests.BaseSnapshot());

        Assert.Equal(0, added);
        var data = EnvironmentHistoryStore.Load();
        Assert.Single(data.Snapshots);
        Assert.Empty(data.Changes);
    }

    /// <summary>环境未变（指纹相同）→ 不入库不产生记录</summary>
    [Fact]
    public void RecordSnapshot_UnchangedEnvironment_Skips()
    {
        EnvironmentHistoryStore.RecordSnapshot(EnvironmentChangeDetectorTests.BaseSnapshot());

        var snap2 = EnvironmentChangeDetectorTests.BaseSnapshot();
        snap2.CollectedAt = DateTime.Now.AddDays(1);
        int added = EnvironmentHistoryStore.RecordSnapshot(snap2);

        Assert.Equal(0, added);
        Assert.Single(EnvironmentHistoryStore.Load().Snapshots);
    }

    /// <summary>驱动升级 → 快照入库 + 1 条变更，Time 取新快照时间</summary>
    [Fact]
    public void RecordSnapshot_GpuDriverChange_RecordsChangeWithTime()
    {
        EnvironmentHistoryStore.RecordSnapshot(EnvironmentChangeDetectorTests.BaseSnapshot());

        var snap2 = EnvironmentChangeDetectorTests.BaseSnapshot();
        snap2.CollectedAt = new DateTime(2026, 8, 20, 12, 0, 0);
        snap2.Gpus[0].DriverVersion = "32.0.23013";
        int added = EnvironmentHistoryStore.RecordSnapshot(snap2);

        Assert.Equal(1, added);
        var change = EnvironmentHistoryStore.LoadChanges().Single();
        Assert.Equal(ChangeCategory.GpuDriver, change.Category);
        Assert.Equal(snap2.CollectedAt, change.Time);
    }

    /// <summary>快照超过 100 份 → 淘汰最旧</summary>
    [Fact]
    public void RecordSnapshot_OverSnapshotCap_TrimsOldest()
    {
        for (int i = 0; i < 105; i++)
        {
            var s = EnvironmentChangeDetectorTests.BaseSnapshot();
            s.BiosVersion = $"1.{i}.0"; // 每份都不同，确保入库
            s.CollectedAt = DateTime.Now.AddDays(-300 + i);
            EnvironmentHistoryStore.RecordSnapshot(s);
        }

        var data = EnvironmentHistoryStore.Load();
        Assert.Equal(100, data.Snapshots.Count);
        // 最旧被淘汰：首份 1.0.0 不在了，保留的是 1.5.0 ~ 1.104.0
        Assert.DoesNotContain(data.Snapshots, s => s.Snapshot.BiosVersion == "1.0.0");
        Assert.Contains(data.Snapshots, s => s.Snapshot.BiosVersion == "1.5.0");
    }

    /// <summary>存档损坏 → 备份 .bak 并返回空数据</summary>
    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tmpFile)!);
        File.WriteAllText(_tmpFile, "{{{ not json");

        var data = EnvironmentHistoryStore.Load();

        Assert.Empty(data.Snapshots);
        Assert.Empty(data.Changes);
        Assert.True(File.Exists(_tmpFile + ".bak"));
    }
}
