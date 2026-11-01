using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 导出崩溃数据为 zip 报告包：
///   report.html (带内联样式与交互筛选的现代化分析报告)
///   events.json  (结构化事件 + 原始事件XML)
///   dumps/*.dmp  (自动收集并导出关联的所有转储文件)
/// </summary>
public static class ReportExporter
{
    /// <summary>单个 dump 超过这个大小则不打包进 zip (500MB)</summary>
    private const long MAX_DUMP_COPY_SIZE = 500L * 1024 * 1024;

    /// <summary>单个 .evtx 日志文件导出体积上限（50MB，防 System channel 过大撑爆报告包）</summary>
    private const long MAX_EVTX_SIZE = 50L * 1024 * 1024;

    /// <summary>dumps 目录总体积预算（2GB）：事件多且各带大 dump 时防止把 %TEMP%（通常在 C 盘）塞满</summary>
    private const long MAX_TOTAL_DUMP_SIZE = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 将单个崩溃事件关联的所有 .dmp 转储文件直接导出到指定文件夹
    /// </summary>
    public static (int copiedCount, List<string> copiedFiles, List<string> errors) ExportEventDumpsToFolder(
        CrashEvent ev, string targetDirectory)
    {
        var copiedFiles = new List<string>();
        var errors = new List<string>();
        Directory.CreateDirectory(targetDirectory);

        var paths = ResolveAllDumpPathsForEvent(ev);
        foreach (string srcPath in paths)
        {
            if (!File.Exists(srcPath))
            {
                errors.Add($"{srcPath}（原文件不存在或已被系统清理）");
                continue;
            }
            try
            {
                string fileName = Path.GetFileName(srcPath);
                string destPath = Path.Combine(targetDirectory, fileName);
                int seq = 1;
                while (File.Exists(destPath) && !string.Equals( Path.GetFullPath(srcPath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    destPath = Path.Combine(targetDirectory,
                        $"{Path.GetFileNameWithoutExtension(fileName)}_{seq++}{Path.GetExtension(fileName)}");
                }

                using (var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    src.CopyTo(dst);
                }
                copiedFiles.Add(destPath);
            }
            catch (Exception ex)
            {
                errors.Add($"{srcPath}（复制失败: {ex.Message}）");
            }
        }
        return (copiedFiles.Count, copiedFiles, errors);
    }

    /// <summary>
    /// 解析某条崩溃事件关联的所有转储路径（包含事件自身记录的路径 + 磁盘 ±30 分钟时间窗自动匹配补全）
    /// </summary>
    public static List<string> ResolveAllDumpPathsForEvent(CrashEvent ev)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string p in ev.GetEffectiveDumpPaths())
        {
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                list.Add(p);
        }

        // 如果事件还没有任何磁盘上实际存在的 .dmp，主动在系统转储目录中按 ±30 分钟时间窗口匹配补全
        if (!list.Any(File.Exists))
        {
            foreach (string candidate in DiscoverSystemDumpsNearTime(ev.Time, TimeSpan.FromMinutes(30)))
            {
                if (seen.Add(candidate))
                {
                    list.Add(candidate);
                    ev.AddDumpPath(candidate);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 在系统 Minidump、注册表自定义目录及 LiveKernelReports 中查找指定时间附近的 .dmp 文件
    /// </summary>
    private static List<string> DiscoverSystemDumpsNearTime(DateTime crashTime, TimeSpan tolerance)
    {
        var matches = new List<string>();
        if (crashTime == DateTime.MinValue) return matches;

        DateTime minTime = crashTime - tolerance;
        DateTime maxTime = crashTime + tolerance;

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir))
        {
            dirs.Add(Path.Combine(winDir, "Minidump"));
            dirs.Add(Path.Combine(winDir, "LiveKernelReports"));
        }
        dirs.Add(@"C:\Windows\Minidump");
        dirs.Add(@"C:\Windows\LiveKernelReports");

        try
        {
            var cfg = CrashDumpConfigService.Inspect();
            if (!string.IsNullOrWhiteSpace(cfg.MinidumpDir))
                dirs.Add(cfg.MinidumpDir);
        }
        catch { }

        foreach (string dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                bool isLiveKernel = dir.EndsWith("LiveKernelReports", StringComparison.OrdinalIgnoreCase);
                var searchOpt = isLiveKernel ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (string f in Directory.EnumerateFiles(dir, "*.dmp", searchOpt))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length == 0) continue;
                        DateTime wt = fi.LastWriteTime;
                        DateTime ct = fi.CreationTime;
                        if ((wt >= minTime && wt <= maxTime) || (ct >= minTime && ct <= maxTime))
                            matches.Add(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return matches;
    }

    /// <summary>
    /// 导出报告到指定 zip 路径
    /// </summary>
    public static ExportResult Export(string zipPath, List<CrashEvent> events, DateTime startTime, DateTime endTime,
        EnvironmentSnapshot? env = null, List<SuspectRankEntry>? suspectRanking = null,
        EnvironmentHistoryData? envHistory = null)
    {
        var result = new ExportResult();
        try
        {
            // 先清理目标文件（容错：存在才删）
            if (File.Exists(zipPath)) File.Delete(zipPath);

            string tempDir = Path.Combine(Path.GetTempPath(), $"CrashSniffer_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // 0. 预先为所有事件补全关联的 .dmp 路径，确保 events.json 和 dumps/ 完整一致
                foreach (var ev in events)
                {
                    try { _ = ResolveAllDumpPathsForEvent(ev); } catch { }
                }

                // 1. events.json
                string jsonPath = Path.Combine(tempDir, "events.json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(events, JsonOpts), Encoding.UTF8);
                result.FilesIncluded.Add("events.json");

                // 1.5 environment.json (环境快照)
                if (env != null)
                {
                    string envPath = Path.Combine(tempDir, "environment.json");
                    File.WriteAllText(envPath, JsonSerializer.Serialize(env, JsonOpts), Encoding.UTF8);
                    result.FilesIncluded.Add("environment.json");
                }

                // 1.6 environment_history.json (环境变更历史)
                if (envHistory != null)
                {
                    string envHistPath = Path.Combine(tempDir, "environment_history.json");
                    File.WriteAllText(envHistPath, JsonSerializer.Serialize(envHistory, JsonOpts), Encoding.UTF8);
                    result.FilesIncluded.Add("environment_history.json");
                }

                // 1.7 hardware_snapshots/ 崩溃硬件回放快照
                try
                {
                    var snaps = HardwareSnapshotStore.ListSnapshots();
                    if (snaps.Count > 0)
                    {
                        string hwDir = Path.Combine(tempDir, "hardware_snapshots");
                        Directory.CreateDirectory(hwDir);
                        foreach (var snap in snaps)
                        {
                            string snapFile = Path.Combine(hwDir, $"{snap.EventId}.json");
                            File.WriteAllText(snapFile, JsonSerializer.Serialize(snap, JsonOpts), Encoding.UTF8);
                        }
                        result.FilesIncluded.Add($"hardware_snapshots/ ({snaps.Count} snapshots)");
                    }
                }
                catch { /* 硬件快照导出失败不阻断 */ }

                // 2. dumps 目录：导出每个事件的所有关联 dump 文件
                string dumpsDir = Path.Combine(tempDir, "dumps");
                Directory.CreateDirectory(dumpsDir);
                var copiedDumps = new Dictionary<string, ExportedDumpInfo>(StringComparer.OrdinalIgnoreCase);
                var reportedMissingDumps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedDestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long totalDumpBytes = 0;

                for (int evIdx = 0; evIdx < events.Count; evIdx++)
                {
                    var ev = events[evIdx];
                    var candidatePaths = ResolveAllDumpPathsForEvent(ev);
                    foreach (string dumpPath in candidatePaths)
                    {
                        if (string.IsNullOrWhiteSpace(dumpPath)) continue;

                        if (copiedDumps.TryGetValue(dumpPath, out var existingInfo))
                        {
                            result.MapEventDump(evIdx, existingInfo);
                            continue;
                        }

                        if (!File.Exists(dumpPath))
                        {
                            if (reportedMissingDumps.Add(dumpPath))
                            {
                                result.SkippedDumps.Add($"{dumpPath}（事件日志记录了该转储路径，但原文件不存在或已被系统清理）");
                            }
                            continue;
                        }

                        var fi = new FileInfo(dumpPath);
                        if (fi.Length > MAX_DUMP_COPY_SIZE)
                        {
                            if (reportedMissingDumps.Add(dumpPath))
                            {
                                result.SkippedDumps.Add($"{dumpPath}（{CrashEvent.FormatFileSize(fi.Length)}，超过 500MB 单文件上限，建议手动用 WinDbg 打开）");
                            }
                            continue;
                        }
                        if (totalDumpBytes + fi.Length > MAX_TOTAL_DUMP_SIZE)
                        {
                            if (reportedMissingDumps.Add(dumpPath))
                            {
                                result.SkippedDumps.Add($"{dumpPath}（dumps 总体积已达 {CrashEvent.FormatFileSize(totalDumpBytes)}，超出 2GB 预算，未包含）");
                            }
                            continue;
                        }

                        try
                        {
                            string fileName = Path.GetFileName(dumpPath);
                            string destName = fileName;
                            int seq = 1;
                            while (usedDestNames.Contains(destName))
                                destName = $"{Path.GetFileNameWithoutExtension(fileName)}_{seq++}{Path.GetExtension(fileName)}";
                            usedDestNames.Add(destName);

                            string dest = Path.Combine(dumpsDir, destName);
                            using (var src = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write))
                                src.CopyTo(dst);

                            string relPath = $"dumps/{destName}";
                            var info = new ExportedDumpInfo(
                                SourcePath: dumpPath,
                                RelativeZipPath: relPath,
                                FileName: destName,
                                SizeBytes: fi.Length,
                                LastWriteTime: fi.LastWriteTime,
                                PrimaryEventIndex: evIdx,
                                StopCodeSummary: ev.BugCheckCode != 0 ? $"0x{ev.BugCheckCode:X8} ({ev.StopCode})" : ev.Summary);

                            copiedDumps[dumpPath] = info;
                            result.ExportedDumps.Add(info);
                            result.MapEventDump(evIdx, info);
                            totalDumpBytes += fi.Length;

                            result.FilesIncluded.Add(destName == fileName
                                ? relPath
                                : $"{relPath}（来自 {dumpPath}，同名文件自动重命名）");
                        }
                        catch (Exception ex)
                        {
                            if (reportedMissingDumps.Add(dumpPath))
                                result.SkippedDumps.Add($"{dumpPath}（复制失败: {ex.Message}）");
                        }
                    }
                }

                // 2.5 event_logs 目录：用 wevtutil 按时间窗导出 System channel 为标准 .evtx
                try
                {
                    string logsDir = Path.Combine(tempDir, "event_logs");
                    Directory.CreateDirectory(logsDir);
                    string evtxPath = Path.Combine(logsDir, "System.evtx");

                    string startUtc = startTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    string endUtc = endTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    string xpath = $"*[System[TimeCreated[@SystemTime>='{startUtc}' and @SystemTime<='{endUtc}']]]";

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wevtutil.exe",
                        Arguments = $"epl System \"{evtxPath}\" /q:\"{xpath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p != null)
                    {
                        Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(30000))
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(5000); } catch { }
                        }

                        string stderr;
                        try { stderr = stderrTask.Wait(2000) ? stderrTask.Result : string.Empty; }
                        catch { stderr = string.Empty; }

                        if (p.HasExited && p.ExitCode == 0 && File.Exists(evtxPath))
                        {
                            var fi = new FileInfo(evtxPath);
                            if (fi.Length == 0)
                            {
                                try { File.Delete(evtxPath); } catch { }
                            }
                            else if (fi.Length > MAX_EVTX_SIZE)
                            {
                                result.SkippedDumps.Add($"System.evtx（{CrashEvent.FormatFileSize(fi.Length)}，超过 {MAX_EVTX_SIZE / 1024 / 1024} MB 上限，建议手动用事件查看器导出）");
                                try { File.Delete(evtxPath); } catch { }
                            }
                            else
                            {
                                result.FilesIncluded.Add("event_logs/System.evtx");
                            }
                        }
                        else if (p.HasExited && p.ExitCode != 0)
                        {
                            result.SkippedDumps.Add($"System.evtx（wevtutil 失败: {stderr.Trim()}）");
                        }
                        else
                        {
                            result.SkippedDumps.Add("System.evtx（wevtutil 超时未退出，已强制终止）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.SkippedDumps.Add($"System.evtx（导出异常: {ex.Message}）");
                }

                // 3. report.html
                string htmlPath = Path.Combine(tempDir, "report.html");
                File.WriteAllText(htmlPath, BuildHtml(events, startTime, endTime, result, env, suspectRanking, envHistory), Encoding.UTF8);
                result.FilesIncluded.Insert(0, "report.html");

                // 4. 打包
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                result.ZipPath = zipPath;
                result.Success = true;
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        return result;
    }

    public sealed record ExportedDumpInfo(
        string SourcePath,
        string RelativeZipPath,
        string FileName,
        long SizeBytes,
        DateTime LastWriteTime,
        int PrimaryEventIndex,
        string StopCodeSummary);

    public class ExportResult
    {
        public bool Success { get; set; }
        public string ZipPath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public List<string> FilesIncluded { get; } = new();
        public List<string> SkippedDumps { get; } = new();
        public List<ExportedDumpInfo> ExportedDumps { get; } = new();
        private readonly Dictionary<int, List<ExportedDumpInfo>> _eventDumps = new();

        public void MapEventDump(int eventIndex, ExportedDumpInfo info)
        {
            if (!_eventDumps.TryGetValue(eventIndex, out var list))
            {
                list = new List<ExportedDumpInfo>();
                _eventDumps[eventIndex] = list;
            }
            if (!list.Any(x => string.Equals(x.RelativeZipPath, info.RelativeZipPath, StringComparison.OrdinalIgnoreCase)))
                list.Add(info);
        }

        public IReadOnlyList<ExportedDumpInfo> GetDumpsForEvent(int eventIndex)
            => _eventDumps.TryGetValue(eventIndex, out var list) ? list : Array.Empty<ExportedDumpInfo>();
    }

    // —— HTML 生成 ——

    private static string BuildHtml(List<CrashEvent> events, DateTime start, DateTime end, ExportResult progress,
        EnvironmentSnapshot? env = null, List<SuspectRankEntry>? suspectRanking = null,
        EnvironmentHistoryData? envHistory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head>");
        sb.AppendLine("<meta charset=\"UTF-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>");
        sb.AppendLine("<title>CrashSniffer 系统崩溃诊断报告</title>");
        sb.AppendLine(InlineCss());
        sb.AppendLine("</head><body><div class=\"container\">");

        // 顶部 Header 横幅
        sb.AppendLine("<header class=\"hero\">");
        sb.AppendLine("<div class=\"hero-main\">");
        sb.AppendLine("<div class=\"brand\"><span class=\"brand-icon\">🪦</span><div><h1>CrashSniffer 系统崩溃深度诊断报告</h1>");
        sb.AppendLine($"<p class=\"meta\">生成时间：<b>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</b> &nbsp;·&nbsp; 扫描区间：<b>{start:yyyy-MM-dd HH:mm}</b> → <b>{end:yyyy-MM-dd HH:mm}</b></p></div></div>");
        sb.AppendLine("<div class=\"hero-actions\">");
        sb.AppendLine("<button type=\"button\" class=\"btn btn-primary\" onclick=\"copyReportPost()\">📋 一键复制论坛求助速帖</button>");
        sb.AppendLine("<button type=\"button\" class=\"btn btn-ghost\" onclick=\"window.print()\">🖨️ 打印 / 存为 PDF</button>");
        sb.AppendLine("</div></div>");

        // 顶部 KPI 概览卡片
        int bsodCount = events.Count(e => e.Type == CrashType.BSOD);
        int wheaCount = events.Count(e => e.Type == CrashType.WHEA_Error);
        int tdrCount = events.Count(e => e.Type == CrashType.GPU_TDR || e.Type == CrashType.LiveKernel);
        string topSuspect = suspectRanking is { Count: > 0 }
            ? $"{suspectRanking[0].Driver} ({suspectRanking[0].Count}次)"
            : (events.Select(e => e.SuspectDriver).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "暂无明确驱动");
        long totalExportedDumpBytes = progress.ExportedDumps.Sum(d => d.SizeBytes);

        sb.AppendLine("<div class=\"kpi-grid\">");
        sb.AppendLine($"<div class=\"kpi-card\"><span class=\"kpi-label\">崩溃事件总数</span><strong class=\"kpi-val\">{events.Count}</strong><span class=\"kpi-sub\">合并归并后独立故障</span></div>");
        sb.AppendLine($"<div class=\"kpi-card kpi-bsod\"><span class=\"kpi-label\">BSOD 蓝屏 / 致命错误</span><strong class=\"kpi-val\">{bsodCount}</strong><span class=\"kpi-sub\">WHEA 硬件报错 {wheaCount} 次</span></div>");
        sb.AppendLine($"<div class=\"kpi-card kpi-tdr\"><span class=\"kpi-label\">GPU TDR / LiveKernel</span><strong class=\"kpi-val\">{tdrCount}</strong><span class=\"kpi-sub\">显卡重置或内核挂死</span></div>");
        sb.AppendLine($"<div class=\"kpi-card kpi-dmp\"><span class=\"kpi-label\">已自动导出转储 (.dmp)</span><strong class=\"kpi-val\">{progress.ExportedDumps.Count} 份</strong><span class=\"kpi-sub\">共 {CrashEvent.FormatFileSize(totalExportedDumpBytes)} (位于 dumps/)</span></div>");
        sb.AppendLine($"<div class=\"kpi-card kpi-drv\"><span class=\"kpi-label\">头号高频嫌疑模块</span><strong class=\"kpi-val mono\" style=\"font-size:16px\">{Escape(topSuspect)}</strong><span class=\"kpi-sub\">优先排查对象</span></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</header>");

        // 隐藏的整包论坛速帖文本（供顶部一键复制按钮读取）
        string reportForumPost = ForumPostBuilder.BuildForReport(events, env, suspectRanking, envHistory?.Changes);
        sb.AppendLine($"<textarea id=\"report-forum-post\" style=\"display:none\" readonly>{ System.Net.WebUtility.HtmlEncode(reportForumPost) }</textarea>");

        // 双栏概览：环境快照 + 统计 & 嫌疑驱动排行
        sb.AppendLine("<div class=\"overview-grid\">");
        if (env != null)
            sb.AppendLine(BuildEnvSection(env));

        sb.AppendLine("<div class=\"overview-col\">");
        // 统计摘要
        sb.AppendLine("<section class=\"stat\"><h3 class=\"sec-title\">📊 故障类型分布</h3>");
        var grp = events.GroupBy(e => e.Type).OrderBy(g => (int)g.Key).ToList();
        sb.AppendLine("<table class=\"stat-tbl\"><thead><tr><th>故障类型</th><th>次数</th><th>占比</th></tr></thead><tbody>");
        foreach (var g in grp)
        {
            double pct = events.Count > 0 ? g.Count() * 100.0 / events.Count : 0;
            sb.AppendLine($"<tr><td>{TypeName(g.Key)}</td><td><b>{g.Count()}</b></td><td><div class=\"bar-wrap\"><div class=\"bar-fill\" style=\"width:{pct:F0}%\"></div><span>{pct:F0}%</span></div></td></tr>");
        }
        sb.AppendLine("</tbody></table></section>");

        // 嫌疑驱动排行
        if (suspectRanking is { Count: > 0 })
        {
            sb.AppendLine("<section class=\"stat\"><h3 class=\"sec-title\">🎯 嫌疑驱动排行 (惯犯名单)</h3>");
            sb.AppendLine("<table class=\"stat-tbl\"><thead><tr><th>#</th><th>驱动模块</th><th>说明</th><th>系统版本</th><th>次数</th></tr></thead><tbody>");
            for (int i = 0; i < suspectRanking.Count && i < 10; i++)
            {
                var r = suspectRanking[i];
                string desc = DriverKnowledge.Describe(r.Driver);
                string ver = string.IsNullOrEmpty(r.Version) ? "-" : Escape(r.Version);
                sb.AppendLine($"<tr><td>{i + 1}</td><td class=\"mono warn\">{Escape(r.Driver)}</td><td>{Escape(desc)}</td><td class=\"mono\">{ver}</td><td><b>{r.Count}</b></td></tr>");
            }
            sb.AppendLine("</tbody></table>");
            if (suspectRanking.Count > 10)
                sb.AppendLine($"<p class=\"meta\" style=\"margin:8px 0 0\">共 {suspectRanking.Count} 个嫌疑驱动，仅显示前 10。</p>");
            sb.AppendLine("</section>");
        }
        sb.AppendLine("</div></div>");

        // 随包自动导出的转储文件清单 (Dumps)
        if (progress.ExportedDumps.Count > 0)
        {
            sb.AppendLine("<section class=\"stat dump-manifest\"><h3 class=\"sec-title\">💾 随包自动导出的转储文件清单 (Dumps)</h3>");
            sb.AppendLine("<p class=\"meta\" style=\"margin:0 0 10px\">以下 <code>.dmp</code> 转储文件已自动打包至压缩包内 <code>dumps/</code> 目录。解压报告包后可直接点击下方按钮下载或拖入 WinDbg 分析：</p>");
            sb.AppendLine("<table class=\"rel-tbl\"><thead><tr><th>文件名 (点击下载)</th><th>文件大小</th><th>文件修改时间</th><th>关联事件</th><th>错误代码 / 摘要</th><th>系统原始路径</th></tr></thead><tbody>");
            foreach (var d in progress.ExportedDumps)
            {
                sb.AppendLine($"<tr>" +
                              $"<td><a class=\"dmp-dl\" href=\"{EscapeAttr(d.RelativeZipPath)}\" download=\"{EscapeAttr(d.FileName)}\">📥 {Escape(d.FileName)}</a></td>" +
                              $"<td class=\"mono\">{CrashEvent.FormatFileSize(d.SizeBytes)}</td>" +
                              $"<td class=\"mono\">{d.LastWriteTime:yyyy-MM-dd HH:mm:ss}</td>" +
                              $"<td><a class=\"ev-jump\" href=\"#ev{d.PrimaryEventIndex}\">跳转至 #{d.PrimaryEventIndex + 1}</a></td>" +
                              $"<td class=\"mono\">{Escape(d.StopCodeSummary)}</td>" +
                              $"<td class=\"mono muted-sm\">{Escape(d.SourcePath)}</td>" +
                              $"</tr>");
            }
            sb.AppendLine("</tbody></table></section>");
        }

        // 交互式粘性筛选栏
        sb.AppendLine("<div class=\"filter-bar\">");
        sb.AppendLine("<div class=\"filter-pills\">");
        sb.AppendLine($"<button type=\"button\" class=\"pill active\" data-filter=\"ALL\" onclick=\"filterEvents('ALL', this)\">全部 ({events.Count})</button>");
        foreach (var g in grp)
        {
            sb.AppendLine($"<button type=\"button\" class=\"pill\" data-filter=\"{g.Key}\" onclick=\"filterEvents('{g.Key}', this)\">{TypeName(g.Key)} ({g.Count()})</button>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"search-box\"><input id=\"ev-search\" type=\"search\" placeholder=\"🔍 搜索停止代码、驱动、崩溃偏移、WHEA核心、摘要...\" oninput=\"applySearchAndFilter()\"/></div>");
        sb.AppendLine("</div>");

        // 逐条详情
        sb.AppendLine("<div id=\"events-list\">");
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            HardwareSnapshot? hwSnapshot = null;
            try
            {
                hwSnapshot = HardwareSnapshotStore.FindByCrashTime(ev.Time)
                          ?? ContinuousSampleStore.BuildSnapshotForCrash(ev.Time);
            }
            catch { }

            string singleForumPost = ForumPostBuilder.BuildForEvent(ev, env, hwSnapshot, envHistory?.Changes);
            string typeClass = $"type-{ev.Type.ToString().ToLowerInvariant()}";
            string searchBlob = $"{ev.Type} {ev.Summary} 0x{ev.BugCheckCode:X} {ev.StopCode} {ev.StopCodeChinese} {ev.SuspectDriver} {ev.FaultingModuleOffset} {ev.WheaErrorType} {ev.WheaLocation} {ev.WheaHardwarePart} {ev.DumpPath}";

            sb.AppendLine($"<article class=\"ev {typeClass}\" data-type=\"{ev.Type}\" data-search=\"{EscapeAttr(searchBlob.ToLowerInvariant())}\">");
            sb.AppendLine($"<div class=\"ev-head\"><h2 id=\"ev{i}\"><span class=\"ev-idx\">#{i + 1}</span> <span class=\"ev-badge {typeClass}\">{TypeName(ev.Type)}</span> <span class=\"ts\">{ev.Time:yyyy-MM-dd HH:mm:ss}</span></h2>");
            sb.AppendLine("<div class=\"ev-actions\">");

            var exportedForEv = progress.GetDumpsForEvent(i);
            foreach (var dmp in exportedForEv)
            {
                sb.AppendLine($"<a class=\"dmp-dl\" href=\"{EscapeAttr(dmp.RelativeZipPath)}\" download=\"{EscapeAttr(dmp.FileName)}\" title=\"下载已打包的转储文件 {EscapeAttr(dmp.FileName)}\">📥 下载 {Escape(dmp.FileName)} ({CrashEvent.FormatFileSize(dmp.SizeBytes)})</a>");
            }
            sb.AppendLine($"<button type=\"button\" class=\"btn-sm\" onclick=\"copySinglePost('post-ev-{i}')\">📋 复制此条求助帖</button>");
            sb.AppendLine("</div></div>");
            sb.AppendLine($"<textarea id=\"post-ev-{i}\" style=\"display:none\" readonly>{System.Net.WebUtility.HtmlEncode(singleForumPost)}</textarea>");

            sb.AppendLine($"<p class=\"sum\"><b>摘要：</b>{Escape(ev.Summary)}</p>");

            // 停止代码卡片
            if (ev.BugCheckCode != 0)
            {
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">停止代码</span><span class=\"v mono\"><b>0x{ev.BugCheckCode:X8}</b></span></div>");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">符号名</span><span class=\"v mono\">{Escape(ev.StopCode)}</span></div>");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">解释</span><span class=\"v\">{Escape(ev.StopCodeChinese)}</span></div>");
                sb.AppendLine("<div class=\"row\"><span class=\"k\">参数</span><span class=\"v mono\">");
                for (int p = 0; p < ev.BugCheckParams.Length; p++)
                    sb.Append($"P{p} = 0x{ev.BugCheckParams[p]:X16}<br/>");
                sb.AppendLine("</span></div>");
                sb.AppendLine("</div>");
            }

            // dump + 嫌疑驱动 + 模块偏移
            var effectiveDumps = ev.GetEffectiveDumpPaths();
            if (effectiveDumps.Count > 0 || !string.IsNullOrWhiteSpace(ev.SuspectDriver) || !string.IsNullOrWhiteSpace(ev.FaultingModuleOffset))
            {
                sb.AppendLine("<div class=\"card\">");
                if (effectiveDumps.Count > 0)
                {
                    sb.AppendLine("<div class=\"row\"><span class=\"k\">Dump 文件</span><span class=\"v mono\">");
                    for (int dIdx = 0; dIdx < effectiveDumps.Count; dIdx++)
                    {
                        string dp = effectiveDumps[dIdx];
                        long sz = (dIdx == 0 && ev.DumpSize > 0) ? ev.DumpSize : (File.Exists(dp) ? new FileInfo(dp).Length : 0);
                        string sizeText = sz > 0 ? $" <small>({CrashEvent.FormatFileSize(sz)})</small>" : string.Empty;
                        var exportedMatch = exportedForEv.FirstOrDefault(x => string.Equals(x.SourcePath, dp, StringComparison.OrdinalIgnoreCase));
                        string dlLink = exportedMatch != null
                            ? $" &nbsp;<a class=\"dmp-dl-inline\" href=\"{EscapeAttr(exportedMatch.RelativeZipPath)}\" download=\"{EscapeAttr(exportedMatch.FileName)}\">[📥 已随包导出: {Escape(exportedMatch.RelativeZipPath)}]</a>"
                            : string.Empty;
                        sb.Append($"{Escape(dp)}{sizeText}{dlLink}<br/>");
                    }
                    sb.AppendLine("</span></div>");
                }
                if (!string.IsNullOrWhiteSpace(ev.FaultingModuleOffset))
                {
                    string addrNote = ev.ExceptionAddress != 0 ? $" <small>(指令地址 0x{ev.ExceptionAddress:X16})</small>" : string.Empty;
                    sb.AppendLine($"<div class=\"row\"><span class=\"k\">崩溃偏移</span><span class=\"v mono crit\">{Escape(ev.FaultingModuleOffset)}{addrNote}</span></div>");
                }
                if (!string.IsNullOrWhiteSpace(ev.SuspectDriver))
                    sb.AppendLine($"<div class=\"row\"><span class=\"k\">嫌疑驱动</span><span class=\"v mono warn\">{Escape(ev.SuspectDriver)}</span></div>");
                if (!string.IsNullOrWhiteSpace(ev.GpuVendor))
                    sb.AppendLine($"<div class=\"row\"><span class=\"k\">GPU 厂商</span><span class=\"v\">{Escape(ev.GpuVendor)}</span></div>");
                sb.AppendLine("</div>");
            }

            // WHEA
            if (!string.IsNullOrWhiteSpace(ev.WheaErrorType) || !string.IsNullOrWhiteSpace(ev.WheaLocation)
                || !string.IsNullOrWhiteSpace(ev.WheaHardwarePart))
            {
                sb.AppendLine("<div class=\"card whea\">");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">WHEA 类型</span><span class=\"v warn\">{Escape(ev.WheaErrorType)}</span></div>");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">位置</span><span class=\"v mono\">{Escape(ev.WheaLocation)}</span></div>");
                if (!string.IsNullOrWhiteSpace(ev.WheaHardwarePart))
                    sb.AppendLine($"<div class=\"row\"><span class=\"k\">出错部位</span><span class=\"v warn\"><b>→ {Escape(ev.WheaHardwarePart)}</b></span></div>");
                sb.AppendLine("</div>");
            }

            // 崩溃前硬件分析
            if (hwSnapshot != null)
            {
                try { sb.AppendLine(BuildHardwareSection(hwSnapshot)); }
                catch { /* 硬件分析失败不阻断报告生成 */ }
            }

            // 事发前变更
            if (envHistory is { Changes.Count: > 0 })
            {
                var before = envHistory.Changes.Where(c => c.Time <= ev.Time)
                    .OrderByDescending(c => c.Time).ToList();
                if (before.Count > 0)
                {
                    sb.AppendLine("<div class=\"card\"><h3 style=\"margin:0 0 6px;color:var(--accent);font-size:15px\">🕐 事发前环境变更</h3>");
                    sb.AppendLine("<p class=\"meta\" style=\"margin:0 0 8px\">变更只能定位到两次扫描之间，精确时刻不可知</p>");
                    int show = Math.Min(10, before.Count);
                    for (int k = 0; k < show; k++)
                    {
                        var c = before[k];
                        sb.AppendLine($"<div class=\"row\"><span class=\"k\">{Escape(EnvironmentChangeDetector.RelativeBefore(ev.Time, c.Time))}</span><span class=\"v\">{Escape(c.Item)}（{EnvironmentChangeDetector.CategoryLabel(c.Category)}）：<span class=\"mono\">{Escape(c.OldValue)}</span> → <span class=\"mono warn\">{Escape(c.NewValue)}</span></span></div>");
                    }
                    if (before.Count > show)
                        sb.AppendLine($"<p class=\"meta\" style=\"margin:8px 0 0\">另有 {before.Count - show} 条更早变更，见文末「环境变更时间线」。</p>");
                    sb.AppendLine("</div>");
                }
            }

            // 原始消息
            if (!string.IsNullOrWhiteSpace(ev.EventMessage))
            {
                sb.AppendLine("<details class=\"raw\"><summary>📄 事件原始消息</summary><pre>" + Escape(ev.EventMessage) + "</pre></details>");
            }

            // 排障建议
            if (ev.Troubleshooting.Count > 0)
            {
                sb.AppendLine("<div class=\"sug\"><h3>💡 针对性排障建议</h3><ol>");
                foreach (var s in ev.Troubleshooting)
                    sb.AppendLine($"<li>{Escape(s)}</li>");
                sb.AppendLine("</ol></div>");
            }

            // 关联事件
            if (ev.RelatedEvents.Count > 0)
            {
                sb.AppendLine("<details class=\"rel\"><summary>🔗 查看前后 5 分钟关联事件 (" + ev.RelatedEvents.Count + " 条)</summary>");
                sb.AppendLine("<table class=\"rel-tbl\"><thead><tr><th>时间</th><th>级别</th><th>Provider</th><th>ID</th><th>消息</th></tr></thead><tbody>");
                foreach (var r in ev.RelatedEvents)
                {
                    sb.Append($"<tr><td>{r.Time:HH:mm:ss.fff}</td><td>{Escape(r.Level)}</td><td>{Escape(r.Provider)}</td><td>{r.EventId}</td><td>{Escape(r.Message)}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</details>");
            }

            sb.AppendLine("</article>");
        }
        sb.AppendLine("</div>");

        // 跳过的 dump / 事件日志
        if (progress.SkippedDumps.Count > 0)
        {
            sb.AppendLine("<section class=\"note warn\"><h3>⚠ 未包含在 zip 中的源文件（dump / 事件日志）</h3><ul>");
            foreach (var s in progress.SkippedDumps)
                sb.AppendLine($"<li>{Escape(s)}</li>");
            sb.AppendLine("</ul></section>");
        }

        // 环境变更时间线（完整章节）
        if (envHistory != null)
        {
            if (envHistory.Changes.Count > 0)
            {
                sb.AppendLine("<section class=\"stat\"><h3 class=\"sec-title\">🕐 环境变更时间线</h3>");
                sb.AppendLine("<table class=\"rel-tbl\"><thead><tr><th>检测时间</th><th>类别</th><th>项目</th><th>旧值</th><th>新值</th></tr></thead><tbody>");
                foreach (var c in envHistory.Changes.OrderByDescending(c => c.Time))
                    sb.AppendLine($"<tr><td>{c.Time:yyyy-MM-dd HH:mm}</td><td>{EnvironmentChangeDetector.CategoryLabel(c.Category)}</td><td>{Escape(c.Item)}</td><td class=\"mono\">{Escape(c.OldValue)}</td><td class=\"mono warn\">{Escape(c.NewValue)}</td></tr>");
                sb.AppendLine("</tbody></table>");
                sb.AppendLine($"<p class=\"meta\" style=\"margin:8px 0 0\">共 {envHistory.Changes.Count} 条变更 · {envHistory.Snapshots.Count} 份环境快照</p>");
                sb.AppendLine("</section>");
            }
            else if (envHistory.Snapshots.Count > 0)
            {
                sb.AppendLine("<section class=\"stat\"><h3 class=\"sec-title\">🕐 环境变更时间线</h3><p class=\"meta\">已有环境快照记录，暂未检测到任何变更。</p></section>");
            }
        }

        sb.AppendLine("<div id=\"toast\" class=\"toast\">✅ 已复制到剪贴板，可直接粘贴至论坛或聊天窗口！</div>");
        sb.AppendLine("<footer><p>CrashSniffer v2.5 · 本报告及关联转储文件由工具自动整理生成，建议结合 WinDbg 深入分析。</p></footer>");
        sb.AppendLine(InlineJs());
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// 崩溃前硬件读数分析 HTML 区块
    /// </summary>
    private static string BuildHardwareSection(HardwareSnapshot snapshot)
    {
        var pre = HardwareAnomalyAnalyzer.GetPreCrashReadings(snapshot);
        if (pre.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"card\">");
        string sourceNote = snapshot.CrashType == "连续采样" ? "（后台连续采样）" : string.Empty;
        sb.AppendLine($"<h3 style=\"margin:0 0 6px;color:var(--accent);font-size:15px\">🩺 崩溃前硬件分析{sourceNote}</h3>");

        var parts = new List<string> { $"{pre.Count} 个采样点" };
        var cpuTemps = pre.Where(r => r.CpuTemperature.HasValue).Select(r => r.CpuTemperature!.Value).ToList();
        if (cpuTemps.Count > 0) parts.Add($"CPU 峰值 {cpuTemps.Max():F1}℃");
        var maxCoreLoads = pre.Where(r => r.CpuMaxCoreLoad.HasValue).Select(r => r.CpuMaxCoreLoad!.Value).ToList();
        if (maxCoreLoads.Count > 0) parts.Add($"最高核心负载 {maxCoreLoads.Max():F0}%");
        var gpuTemps = pre.Where(r => r.GpuTemperature.HasValue).Select(r => r.GpuTemperature!.Value).ToList();
        if (gpuTemps.Count > 0) parts.Add($"GPU 峰值 {gpuTemps.Max():F1}℃");
        var hotspots = pre.Where(r => r.GpuHotspot.HasValue).Select(r => r.GpuHotspot!.Value).ToList();
        if (hotspots.Count > 0) parts.Add($"热点峰值 {hotspots.Max():F1}℃");
        var vramTemps = pre.Where(r => r.GpuMemoryTemperature.HasValue).Select(r => r.GpuMemoryTemperature!.Value).ToList();
        if (vramTemps.Count > 0) parts.Add($"显存峰值 {vramTemps.Max():F1}℃");
        var gpuFans = pre.Where(r => r.GpuFanPercent.HasValue).Select(r => r.GpuFanPercent!.Value).ToList();
        if (gpuFans.Count > 0) parts.Add($"GPU 风扇峰值 {gpuFans.Max():F0}%");
        var moboTemps = pre.Where(r => r.MoboMaxTemperature.HasValue).Select(r => r.MoboMaxTemperature!.Value).ToList();
        if (moboTemps.Count > 0) parts.Add($"主板峰值 {moboTemps.Max():F1}℃");
        var memoryLoads = pre.Where(r => r.MemoryLoad.HasValue).Select(r => r.MemoryLoad!.Value).ToList();
        if (memoryLoads.Count > 0) parts.Add($"内存峰值 {memoryLoads.Max():F1}%");
        var memoryUsed = pre.Where(r => r.MemoryUsedGb.HasValue).Select(r => r.MemoryUsedGb!.Value).ToList();
        if (memoryUsed.Count > 0) parts.Add($"内存已用峰值 {memoryUsed.Max():F1}GB");
        var v12 = pre.Where(r => r.Voltage12V.HasValue).Select(r => r.Voltage12V!.Value).ToList();
        if (v12.Count > 0) parts.Add($"+12V {v12.Min():F2}–{v12.Max():F2}V");
        var cmos = pre.Where(r => r.CmosBatteryVoltage.HasValue).Select(r => r.CmosBatteryVoltage!.Value).ToList();
        if (cmos.Count > 0) parts.Add($"CMOS 电池 {cmos.Min():F2}V");
        sb.AppendLine($"<p class=\"meta\" style=\"margin:0 0 8px\">{string.Join(" · ", parts)}</p>");

        var anomalies = HardwareAnomalyAnalyzer.Analyze(snapshot);
        if (anomalies.Count == 0)
        {
            sb.AppendLine("<p class=\"ok\" style=\"margin:0\">✅ 未发现明显硬件异常</p>");
        }
        else
        {
            sb.AppendLine("<ul style=\"margin:0;padding-left:20px\">");
            foreach (var a in anomalies)
            {
                string cls = a.Severity switch
                {
                    AnomalySeverity.Critical => "crit",
                    AnomalySeverity.Warning => "warn",
                    _ => "meta",
                };
                string icon = a.Severity switch
                {
                    AnomalySeverity.Critical => "⛔",
                    AnomalySeverity.Warning => "⚠",
                    _ => "ℹ",
                };
                sb.AppendLine($"<li class=\"{cls}\">{icon} <b>{Escape(a.Title)}</b>：{Escape(a.Detail)}</li>");
            }
            sb.AppendLine("</ul>");
        }

        AppendDriveTable(sb, snapshot, pre);

        sb.AppendLine("<p class=\"meta\" style=\"margin:8px 0 0\">结论基于崩溃前窗口的传感器读数，均为疑似异常，请结合散热与供电情况判断。</p>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    /// <summary>崩溃前窗口内的逐盘摘要表</summary>
    private static void AppendDriveTable(StringBuilder sb, HardwareSnapshot snapshot, List<HardwareReading> pre)
    {
        var representatives = StorageLabel.Representatives(snapshot);
        if (representatives.Count == 0) return;

        var rows = new List<string>();
        foreach (var rep in representatives)
        {
            string key = StorageLabel.Key(rep);
            var samples = pre
                .Where(r => r.Storages != null)
                .SelectMany(r => r.Storages)
                .Where(s => StorageLabel.Key(s) == key)
                .ToList();
            if (samples.Count == 0) continue;

            string name = samples.Select(s => s.Name)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? rep.Name;
            string volumes = samples.Select(s => string.Join(", ", s.Volumes))
                .FirstOrDefault(v => v.Length > 0) ?? "—";
            var temps = samples.Where(s => s.Temperature.HasValue).Select(s => s.Temperature!.Value).ToList();
            var lives = samples.Where(s => s.LifePercent.HasValue).Select(s => s.LifePercent!.Value).ToList();
            var spares = samples.Where(s => s.AvailableSparePercent.HasValue).Select(s => s.AvailableSparePercent!.Value).ToList();
            var used = samples.Where(s => s.UsedSpacePercent.HasValue).Select(s => s.UsedSpacePercent!.Value).ToList();
            var written = samples.Where(s => s.TotalWrittenGb.HasValue).Select(s => s.TotalWrittenGb!.Value).ToList();

            rows.Add(
                $"<tr><td>{Escape(string.IsNullOrWhiteSpace(name) ? StorageLabel.Short(rep) : name)}</td>" +
                $"<td>{Escape(volumes)}</td>" +
                $"<td>{(temps.Count > 0 ? $"{temps.Max():F1}℃" : "—")}</td>" +
                $"<td>{(lives.Count > 0 ? $"{lives.Min():F0}%" : "—")}</td>" +
                $"<td>{(spares.Count > 0 ? $"{spares.Min():F0}%" : "—")}</td>" +
                $"<td>{(used.Count > 0 ? $"{used.Max():F1}%" : "—")}</td>" +
                $"<td>{(written.Count > 0 ? $"{written.Max() / 1024.0:F2} TB" : "—")}</td></tr>");
        }

        if (rows.Count == 0) return;

        sb.AppendLine("<table class=\"rel-tbl\" style=\"margin-top:10px\"><thead><tr>" +
                      "<th>磁盘</th><th>盘符</th><th>峰值温度</th><th>剩余寿命</th><th>备用块</th><th>已用空间</th><th>累计写入</th>" +
                      "</tr></thead><tbody>");
        foreach (string row in rows) sb.AppendLine(row);
        sb.AppendLine("</tbody></table>");
    }

    /// <summary>
    /// 环境快照 HTML 区块
    /// </summary>
    private static string BuildEnvSection(EnvironmentSnapshot env)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"stat env-sec\"><h3 class=\"sec-title\">🖥 硬件 / 驱动环境快照</h3>");
        sb.AppendLine("<div class=\"card\" style=\"margin:0\">");

        void Row(string k, string v)
        {
            if (!string.IsNullOrWhiteSpace(v))
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">{Escape(k)}</span><span class=\"v\">{Escape(v)}</span></div>");
        }

        Row("CPU", env.CpuName);
        Row("主板", env.Motherboard);
        Row("BIOS", string.IsNullOrEmpty(env.BiosVersion) ? "" : $"{env.BiosVersion}（{env.BiosDate}）");
        foreach (var gpu in env.Gpus)
            Row("显卡", $"{gpu.Name} · 驱动 {gpu.DriverVersion}（{gpu.DriverDate}）");
        Row("内存", env.MemorySummary);
        Row("内存状态", env.XmpNote);
        Row("内存诊断", env.MemoryDiag.Display);
        Row("系统", $"{env.OsName} {env.OsDisplayVersion} (Build {env.OsBuild})");

        if (env.PlatformDrivers.Count > 0)
        {
            string drivers = string.Join("；", env.PlatformDrivers.Select(d =>
                $"{d.FileName} {d.Version}"));
            Row("平台驱动", drivers);
        }

        if (env.CollectErrors.Count > 0)
            Row("采集说明", $"以下子项采集失败（不影响其余信息）：{string.Join("；", env.CollectErrors)}");

        sb.AppendLine("</div></section>");
        return sb.ToString();
    }

    private static string TypeName(CrashType t) => t switch
    {
        CrashType.BSOD => "🟦 BSOD 蓝屏",
        CrashType.WHEA_Error => "🔥 WHEA 硬件错误",
        CrashType.GPU_TDR => "🎮 GPU TDR 超时重置",
        CrashType.KernelPower_41 => "⚡ Kernel-Power 41 异常重启",
        CrashType.Shutdown_6008 => "🔌 系统意外关闭 6008",
        CrashType.LiveKernel => "🧩 LiveKernel 挂死 (未蓝屏)",
        _ => t.ToString(),
    };

    private static string Escape(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        return System.Net.WebUtility.HtmlEncode(s).Replace("\n", "<br/>");
    }

    private static string EscapeAttr(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return System.Net.WebUtility.HtmlEncode(s);
    }

    private static string InlineCss()
    {
        return @"
<style>
:root{--bg:#0b1120;--fg:#e2e8f0;--card:#151f32;--card-inner:#0d1526;--muted:#94a3b8;--accent:#38bdf8;--warn:#f59e0b;--crit:#f87171;--ok:#34d399;--border:#26354d}
*{box-sizing:border-box}
body{font-family:'Segoe UI','Microsoft YaHei UI','Microsoft YaHei',system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0;padding:24px 16px;line-height:1.6}
.container{max-width:1360px;margin:0 auto}
.hero{background:linear-gradient(135deg,#162238 0%,#0f172a 100%);border:1px solid var(--border);border-radius:16px;padding:22px 26px;margin-bottom:22px;box-shadow:0 10px 30px rgba(0,0,0,.35)}
.hero-main{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:16px;margin-bottom:18px;border-bottom:1px solid var(--border);padding-bottom:16px}
.brand{display:flex;align-items:center;gap:14px}
.brand-icon{font-size:36px;line-height:1;background:rgba(56,189,248,.12);padding:10px;border-radius:12px;border:1px solid rgba(56,189,248,.25)}
.hero h1{margin:0 0 4px;font-size:24px;color:#f8fafc;letter-spacing:.3px}
.meta{margin:0;color:var(--muted);font-size:13.5px}
.hero-actions{display:flex;gap:10px;flex-wrap:wrap}
.btn{cursor:pointer;border:1px solid var(--border);border-radius:8px;padding:8px 16px;font-size:13.5px;font-weight:600;transition:all .15s;font-family:inherit}
.btn-primary{background:linear-gradient(135deg,#0284c7,#2563eb);color:#fff;border-color:#38bdf8}
.btn-primary:hover{filter:brightness(1.12);transform:translateY(-1px)}
.btn-ghost{background:rgba(255,255,255,.05);color:var(--fg)}
.btn-ghost:hover{background:rgba(255,255,255,.12)}
.kpi-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:14px}
.kpi-card{background:var(--card-inner);border:1px solid var(--border);border-radius:12px;padding:14px 16px;display:flex;flex-direction:column;gap:4px;border-top:3px solid var(--accent)}
.kpi-bsod{border-top-color:#3b82f6}
.kpi-tdr{border-top-color:#a855f7}
.kpi-dmp{border-top-color:#10b981}
.kpi-drv{border-top-color:#f59e0b}
.kpi-label{font-size:12.5px;color:var(--muted)}
.kpi-val{font-size:22px;color:#f8fafc;line-height:1.2;word-break:break-all}
.kpi-sub{font-size:12px;color:var(--muted)}
.overview-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(460px,1fr));gap:18px;margin-bottom:20px}
.overview-col{display:flex;flex-direction:column;gap:18px}
.stat{background:var(--card);padding:18px 22px;border-radius:12px;margin-bottom:20px;border:1px solid var(--border)}
.overview-grid .stat{margin-bottom:0}
.sec-title{margin:0 0 12px;color:var(--accent);font-size:16px;display:flex;align-items:center;gap:6px}
.stat-tbl{border-collapse:collapse;width:100%}
.stat-tbl th,.stat-tbl td{text-align:left;padding:7px 10px;border-bottom:1px dashed var(--border);font-size:13.5px}
.stat-tbl th{color:var(--accent);font-weight:600}
.bar-wrap{display:flex;align-items:center;gap:8px}
.bar-fill{height:7px;background:var(--accent);border-radius:4px;min-width:4px;max-width:140px}
.dump-manifest{border-left:4px solid #10b981}
.dmp-dl{display:inline-flex;align-items:center;gap:4px;background:rgba(16,185,129,.14);color:#34d399;border:1px solid rgba(16,185,129,.4);padding:4px 10px;border-radius:6px;text-decoration:none;font-size:12.5px;font-weight:600;transition:all .15s}
.dmp-dl:hover{background:rgba(16,185,129,.26);color:#fff}
.dmp-dl-inline{color:#34d399;text-decoration:none;font-weight:600}
.dmp-dl-inline:hover{text-decoration:underline}
.ev-jump{color:var(--accent);text-decoration:none;font-weight:600}
.ev-jump:hover{text-decoration:underline}
.muted-sm{color:var(--muted);font-size:12px}
.filter-bar{position:sticky;top:10px;z-index:20;background:rgba(21,31,50,.92);backdrop-filter:blur(10px);border:1px solid var(--border);border-radius:12px;padding:12px 16px;margin-bottom:20px;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:12px;box-shadow:0 8px 20px rgba(0,0,0,.3)}
.filter-pills{display:flex;flex-wrap:wrap;gap:8px}
.pill{cursor:pointer;background:var(--card-inner);color:var(--muted);border:1px solid var(--border);border-radius:20px;padding:5px 13px;font-size:13px;font-family:inherit;transition:all .15s}
.pill:hover{color:#fff;border-color:var(--accent)}
.pill.active{background:rgba(56,189,248,.18);color:var(--accent);border-color:var(--accent);font-weight:600}
.search-box{flex:1;min-width:250px;max-width:380px}
.search-box input{width:100%;background:var(--card-inner);color:var(--fg);border:1px solid var(--border);border-radius:8px;padding:7px 12px;font-size:13.5px;outline:none;font-family:inherit}
.search-box input:focus{border-color:var(--accent)}
.ev{background:var(--card);border:1px solid var(--border);border-left:5px solid var(--accent);border-radius:12px;padding:18px 22px;margin-bottom:18px;transition:transform .12s}
.ev.type-bsod{border-left-color:#3b82f6}
.ev.type-whea_error{border-left-color:#f97316}
.ev.type-gpu_tdr{border-left-color:#a855f7}
.ev.type-kernelpower_41{border-left-color:#eab308}
.ev.type-livekernel{border-left-color:#06b6d4}
.ev-head{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:10px;border-bottom:1px dashed var(--border);padding-bottom:10px;margin-bottom:12px}
.ev h2{margin:0;font-size:17px;color:#f8fafc;display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.ev-idx{color:var(--muted);font-weight:700}
.ev-badge{background:rgba(56,189,248,.14);border:1px solid rgba(56,189,248,.3);padding:2px 10px;border-radius:6px;font-size:14px}
.ev h2 .ts{color:var(--accent);font-size:14.5px;font-weight:500;font-family:Consolas,monospace}
.ev-actions{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.btn-sm{cursor:pointer;background:rgba(255,255,255,.06);color:var(--fg);border:1px solid var(--border);border-radius:6px;padding:4px 10px;font-size:12.5px;font-family:inherit}
.btn-sm:hover{background:rgba(56,189,248,.18);border-color:var(--accent);color:#fff}
.sum{color:#cbd5e1;margin:0 0 12px;font-size:14.5px}
.card{background:var(--card-inner);border:1px solid var(--border);border-radius:8px;padding:12px 16px;margin:8px 0}
.card.whea{border-left:4px solid var(--warn)}
.row{display:flex;padding:4px 0;border-bottom:1px dashed rgba(148,163,184,.1)}
.row:last-child{border-bottom:none}
.row .k{width:115px;color:var(--muted);font-size:13px;flex-shrink:0}
.row .v{flex:1;font-size:13.5px;word-break:break-all}
.mono{font-family:Consolas,'Cascadia Code',monospace;font-size:13px}
.warn{color:var(--warn)!important;font-weight:600}
.crit{color:var(--crit)!important;font-weight:600}
.ok{color:var(--ok)!important;font-weight:600}
.sug{background:rgba(56,189,248,.07);border:1px solid rgba(56,189,248,.28);border-radius:8px;padding:12px 18px;margin-top:12px}
.sug h3{margin:0 0 6px;color:var(--accent);font-size:15px}
.sug ol{margin:4px 0 4px 18px;padding:0}
.sug li{padding:2px 0;font-size:13.5px}
details{margin-top:10px;border:1px solid var(--border);border-radius:6px;padding:8px 12px;background:var(--card-inner)}
details summary{cursor:pointer;color:var(--accent);font-size:13.5px;user-select:none;font-weight:500}
details pre{margin:8px 0 0;font-size:12px;color:#cbd5e1;background:#060a14;padding:10px;border-radius:6px;max-height:260px;overflow:auto;white-space:pre-wrap}
.rel-tbl{width:100%;border-collapse:collapse;margin-top:8px;font-size:13px}
.rel-tbl th,.rel-tbl td{padding:6px 8px;border-bottom:1px dashed var(--border);text-align:left;vertical-align:top}
.rel-tbl th{color:var(--accent);font-weight:600}
.note{padding:14px 18px;border-radius:10px;background:var(--card);border:1px solid var(--border);margin-top:20px}
.note.warn{border-left:4px solid var(--warn)}
.note h3{margin:0 0 6px;color:var(--warn);font-size:15px}
.toast{position:fixed;bottom:24px;right:24px;background:#059669;color:#fff;padding:10px 18px;border-radius:8px;font-size:14px;font-weight:600;box-shadow:0 8px 24px rgba(0,0,0,.4);opacity:0;pointer-events:none;transition:opacity .25s;z-index:100}
.toast.show{opacity:1}
footer{margin-top:30px;color:var(--muted);font-size:12.5px;text-align:center;padding-top:16px;border-top:1px solid var(--border)}
@media print{
  body{background:#fff;color:#000;padding:0}
  .hero,.stat,.ev,.card,.sug,details{background:#fff!important;color:#000!important;border-color:#ccc!important;box-shadow:none!important}
  .filter-bar,.hero-actions,.ev-actions,.toast{display:none!important}
  .ev h2,.kpi-val,.sec-title,.sum{color:#000!important}
}
</style>";
    }

    private static string InlineJs()
    {
        return @"
<script>
let currentTypeFilter = 'ALL';
function filterEvents(type, btn) {
  currentTypeFilter = type;
  document.querySelectorAll('.filter-pills .pill').forEach(p => p.classList.remove('active'));
  if (btn) btn.classList.add('active');
  applySearchAndFilter();
}
function applySearchAndFilter() {
  const q = (document.getElementById('ev-search')?.value || '').trim().toLowerCase();
  document.querySelectorAll('#events-list .ev').forEach(el => {
    const t = el.getAttribute('data-type') || '';
    const s = el.getAttribute('data-search') || '';
    const typeOk = (currentTypeFilter === 'ALL' || t === currentTypeFilter);
    const searchOk = (!q || s.indexOf(q) !== -1);
    el.style.display = (typeOk && searchOk) ? '' : 'none';
  });
}
function copyText(text) {
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(showToast).catch(() => fallbackCopy(text));
  } else {
    fallbackCopy(text);
  }
}
function fallbackCopy(text) {
  const ta = document.createElement('textarea');
  ta.value = text;
  document.body.appendChild(ta);
  ta.select();
  try { document.execCommand('copy'); showToast(); } catch(e) {}
  document.body.removeChild(ta);
}
function copyReportPost() {
  const el = document.getElementById('report-forum-post');
  if (el) copyText(el.value);
}
function copySinglePost(id) {
  const el = document.getElementById(id);
  if (el) copyText(el.value);
}
function showToast() {
  const t = document.getElementById('toast');
  if (!t) return;
  t.classList.add('show');
  setTimeout(() => t.classList.remove('show'), 2600);
}
</script>";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 忽略临时目录删除失败
        }
    }
}
