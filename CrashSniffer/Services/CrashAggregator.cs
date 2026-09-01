using System.Diagnostics.Eventing.Reader;
using CrashSniffer.Collectors;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 负责聚合 Minidump + 事件日志，并在时间窗口内合并为一条 CrashEvent
/// 合并优先级（严重度高的保留为主事件）：BSOD > WHEA > TDR > 41 > 6008
/// </summary>
public static class CrashAggregator
{
    /// <summary>同一次崩溃事件的时间窗口：30 分钟内视为同一次</summary>
    private const int MERGE_WINDOW_MINUTES = 30;

    /// <summary>取关联事件时前后各扩展多少分钟</summary>
    private const int RELATED_WINDOW_MINUTES = 5;

    /// <summary>单组聚合窗口的硬上限：防止高频事件把窗口链式延长到无边界，整批事件串成一个巨型组</summary>
    private static readonly TimeSpan MERGE_WINDOW_HARD_CAP = TimeSpan.FromHours(2);

    /// <summary>合并 EventMessage 的长度上限，超出后停止追加（防 O(n²) 字符串膨胀）</summary>
    private const int MAX_MERGED_MESSAGE_LENGTH = 8000;

    /// <summary>
    /// 采集并归并，返回按时间倒序的崩溃事件列表
    /// </summary>
    public static (List<CrashEvent> events, int dumpCount, int eventLogCount, int liveKernelCount)
        CollectAndAggregate(DateTime startTime, DateTime endTime, bool includeRelated = true)
    {
        // 并行采集
        var dumpTask = System.Threading.Tasks.Task.Run(() => MinidumpParser.Collect(startTime, endTime));
        var logTask = System.Threading.Tasks.Task.Run(() => EventLogCollector.Collect(startTime, endTime));
        var liveTask = System.Threading.Tasks.Task.Run(() => LiveKernelReportCollector.Collect(startTime, endTime));
        System.Threading.Tasks.Task.WaitAll(dumpTask, logTask, liveTask);

        List<CrashEvent> dumps = dumpTask.Result;
        List<CrashEvent> logs = logTask.Result;
        List<CrashEvent> lives = liveTask.Result;
        int dumpCount = dumps.Count;
        int eventLogCount = logs.Count;
        int liveKernelCount = lives.Count;

        // 合并 + 归并
        var all = new List<CrashEvent>(dumps.Count + logs.Count + lives.Count);
        all.AddRange(dumps);
        all.AddRange(logs);
        all.AddRange(lives);

        // 按时间升序做归并
        all = all.OrderBy(e => e.Time).ToList();
        var merged = new List<CrashEvent>();

        int i = 0;
        while (i < all.Count)
        {
            CrashEvent primary = all[i];
            DateTime windowStart = primary.Time;
            DateTime windowEnd = primary.Time.AddMinutes(MERGE_WINDOW_MINUTES);
            // 硬上限：窗口最多从首个事件延展 2 小时，防止链式延长把全部事件并成一组
            DateTime windowHardCap = windowStart.Add(MERGE_WINDOW_HARD_CAP);

            var group = new List<CrashEvent> { primary };
            i++;
            while (i < all.Count && all[i].Time <= windowEnd)
            {
                group.Add(all[i]);
                // 延展窗口尾 (BSOD+1001+41 可能跨越几十秒)，但不超过硬上限
                DateTime candidate = all[i].Time.AddMinutes(MERGE_WINDOW_MINUTES);
                if (candidate > windowEnd)
                    windowEnd = candidate > windowHardCap ? windowHardCap : candidate;
                i++;
            }

            // 从组中取严重级别最高的作为主事件，其它作为补充信息合并进去
            CrashEvent leader = group.OrderBy(e => (int)e.Type).First();

            // 用 minidump 解析出来的信息优先：如果组里有 dump 解析结果，就拿 dump 当 leader
            var dumpLeader = group.FirstOrDefault(e => e.Type == CrashType.BSOD && !string.IsNullOrEmpty(e.DumpPath));
            if (dumpLeader != null && dumpLeader != leader) leader = dumpLeader;

            // 合并其它成员的信息到 leader
            MergeGroupInfo(leader, group);

            merged.Add(leader);
        }

        // 附关联事件（±5分钟 System 日志）
        if (includeRelated)
        {
            // 共用一个 EventLogSession：N 个事件原先要建 N 次会话，
            // 开销主要在连接事件日志服务；会话建不起来时回退到 GetRelatedEvents 自建
            EventLogSession? session = null;
            try { session = new EventLogSession(); }
            catch { /* 无权限等场景：留空，走每次自建回退 */ }
            try
            {
                foreach (var ev in merged)
                {
                    try
                    {
                        ev.RelatedEvents.AddRange(
                            EventLogCollector.GetRelatedEvents(ev.Time, RELATED_WINDOW_MINUTES, session));
                    }
                    catch { /* 忽略 */ }
                }
            }
            finally
            {
                session?.Dispose();
            }
        }

        // 按时间倒序返回（最近的排前面）
        merged.Sort((a, b) => DateTime.Compare(b.Time, a.Time));
        return (merged, dumpCount, eventLogCount, liveKernelCount);
    }

    /// <summary>
    /// 嫌疑驱动排行：统计所有事件中嫌疑驱动出现次数，附上系统内实际驱动版本
    /// </summary>
    public static List<SuspectRankEntry> BuildSuspectRanking(List<CrashEvent> events)
    {
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SuspectDriver))
            .Select(e => ExtractDriverFileName(e.SuspectDriver))
            .Where(n => !string.IsNullOrEmpty(n))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SuspectRankEntry(g.Key, g.Count(), GetDriverVersion(g.Key)))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Driver, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return groups;
    }

    /// <summary>
    /// 从嫌疑驱动字符串（可能带 [描述] 后缀）提取纯文件名
    /// </summary>
    private static string ExtractDriverFileName(string suspect)
    {
        string s = StripBracket(suspect).Trim();
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return Path.GetFileName(s);
    }

    private static string StripBracket(string s)
    {
        int i = s.IndexOf('[');
        return i > 0 ? s.Substring(0, i).Trim() : s;
    }

    /// <summary>
    /// 查系统 drivers 目录取驱动实际版本号
    /// </summary>
    private static string GetDriverVersion(string driverFile)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "drivers", driverFile);
            if (!File.Exists(path)) return string.Empty;
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return vi.FileVersion ?? vi.ProductVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void MergeGroupInfo(CrashEvent leader, List<CrashEvent> group)
    {
        foreach (var other in group)
        {
            if (ReferenceEquals(other, leader)) continue;

            // BugCheckCode 优先从 dump 或 1001 事件拿
            if (leader.BugCheckCode == 0 && other.BugCheckCode != 0)
            {
                leader.BugCheckCode = other.BugCheckCode;
                leader.StopCode = other.StopCode;
                leader.StopCodeChinese = other.StopCodeChinese;
                leader.BugCheckParams = other.BugCheckParams;
                if (other.Troubleshooting.Count > 0 && leader.Troubleshooting.Count == 0)
                {
                    leader.Troubleshooting.Clear();
                    leader.Troubleshooting.AddRange(other.Troubleshooting);
                }
            }

            // dump 文件路径优先保留有解析的
            if (string.IsNullOrEmpty(leader.DumpPath) && !string.IsNullOrEmpty(other.DumpPath))
            {
                leader.DumpPath = other.DumpPath;
                leader.DumpSize = other.DumpSize;
            }

            // 嫌疑驱动：优先保留带「描述」的长版本
            if (string.IsNullOrEmpty(leader.SuspectDriver) && !string.IsNullOrEmpty(other.SuspectDriver))
            {
                leader.SuspectDriver = other.SuspectDriver;
            }
            else if (!string.IsNullOrEmpty(other.SuspectDriver) && other.SuspectDriver.Length > leader.SuspectDriver.Length)
            {
                leader.SuspectDriver = other.SuspectDriver;
            }

            // GPU 厂商
            if (string.IsNullOrEmpty(leader.GpuVendor) && !string.IsNullOrEmpty(other.GpuVendor))
                leader.GpuVendor = other.GpuVendor;

            // WHEA 位置补充
            if (string.IsNullOrEmpty(leader.WheaErrorType) && !string.IsNullOrEmpty(other.WheaErrorType))
                leader.WheaErrorType = other.WheaErrorType;
            if (string.IsNullOrEmpty(leader.WheaLocation) && !string.IsNullOrEmpty(other.WheaLocation))
                leader.WheaLocation = other.WheaLocation;
            if (string.IsNullOrEmpty(leader.WheaHardwarePart) && !string.IsNullOrEmpty(other.WheaHardwarePart))
                leader.WheaHardwarePart = other.WheaHardwarePart;

            // 合并排障建议（去重）
            foreach (var sug in other.Troubleshooting)
            {
                if (!leader.Troubleshooting.Contains(sug))
                    leader.Troubleshooting.Add(sug);
            }

            // 合并 EventMessage（如果 leader 空就拿 other 的）
            if (string.IsNullOrEmpty(leader.EventMessage) && !string.IsNullOrEmpty(other.EventMessage))
                leader.EventMessage = other.EventMessage;

            // 如果 leader 时间比 other 早，把 other 事件消息里的内容串到 EventMessage 后
            // 超过长度上限后停止追加，防止高频事件把合并消息膨胀到 MB 级
            if (leader.EventMessage.Length < MAX_MERGED_MESSAGE_LENGTH
                && !string.IsNullOrEmpty(other.EventMessage) && !leader.EventMessage.Contains(other.EventMessage))
            {
                string append = $" [{other.Type}: {other.Summary}] {other.EventMessage}";
                if (leader.EventMessage.Length + append.Length <= MAX_MERGED_MESSAGE_LENGTH)
                    leader.EventMessage += append;
            }
        }

        // 如果 leader 的摘要不够有信息量，重新生成
        if (string.IsNullOrWhiteSpace(leader.Summary))
            leader.Summary = BuildAutoSummary(leader, group);
    }

    private static string BuildAutoSummary(CrashEvent leader, List<CrashEvent> group)
    {
        return leader.Type switch
        {
            CrashType.BSOD => leader.BugCheckCode > 0
                ? $"BSOD 0x{leader.BugCheckCode:X} {leader.StopCode}"
                : "BSOD dump",
            CrashType.WHEA_Error => $"WHEA 硬件错误: {leader.WheaErrorType}",
            CrashType.GPU_TDR => $"GPU TDR 重置: {ExtractDriverName(leader.SuspectDriver)}",
            CrashType.KernelPower_41 => $"异常重启 (Event 41)",
            CrashType.Shutdown_6008 => $"系统意外关闭 (Event 6008)",
            CrashType.LiveKernel => leader.BugCheckCode > 0
                ? $"LiveKernel 挂死 (未蓝屏) 0x{leader.BugCheckCode:X} {leader.StopCode}"
                : "LiveKernel 挂死 dump (未蓝屏)",
            _ => "崩溃事件",
        };
    }

    private static string ExtractDriverName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "未知驱动";
        int idx = s.IndexOf('[');
        return (idx > 0 ? s.Substring(0, idx) : s).Trim();
    }
}

/// <summary>
/// 嫌疑驱动排行条目
/// </summary>
/// <param name="Driver">驱动文件名，如 amdkmdag.sys</param>
/// <param name="Count">在本次扫描事件中出现的次数</param>
/// <param name="Version">系统内该驱动的实际版本（取不到为空）</param>
public sealed record SuspectRankEntry(string Driver, int Count, string Version);
