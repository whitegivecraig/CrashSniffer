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
///   report.html (带内联样式的可读报告)
///   events.json  (结构化事件 + 原始事件XML)
///   dumps/*.dmp  (小 dump 原文件，MEMORY.DMP 默认忽略)
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

                // 2. dumps 目录
                string dumpsDir = Path.Combine(tempDir, "dumps");
                Directory.CreateDirectory(dumpsDir);
                var copiedDumps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // 已占用的目标文件名：Minidump\ 与 LiveKernelReports\各子目录下可能存在同名 dump，
                // 直接 FileMode.Create 会静默覆盖，冲突时追加序号区分
                var usedDestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long totalDumpBytes = 0;
                foreach (var ev in events)
                {
                    if (string.IsNullOrWhiteSpace(ev.DumpPath)) continue;
                    if (!File.Exists(ev.DumpPath)) continue;
                    if (copiedDumps.Contains(ev.DumpPath)) continue;

                    var fi = new FileInfo(ev.DumpPath);
                    if (fi.Length > MAX_DUMP_COPY_SIZE)
                    {
                        result.SkippedDumps.Add($"{ev.DumpPath}（{fi.Length / 1024 / 1024} MB，超过 500MB，建议手动用 WinDbg 打开）");
                        continue;
                    }
                    if (totalDumpBytes + fi.Length > MAX_TOTAL_DUMP_SIZE)
                    {
                        result.SkippedDumps.Add($"{ev.DumpPath}（dumps 总体积已达 {totalDumpBytes / 1024 / 1024} MB 上限，超出 2GB 预算，未包含）");
                        continue;
                    }
                    try
                    {
                        string fileName = Path.GetFileName(ev.DumpPath);
                        string destName = fileName;
                        int seq = 1;
                        while (usedDestNames.Contains(destName))
                            destName = $"{Path.GetFileNameWithoutExtension(fileName)}_{seq++}{Path.GetExtension(fileName)}";
                        usedDestNames.Add(destName);

                        string dest = Path.Combine(dumpsDir, destName);
                        using (var src = new FileStream(ev.DumpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write))
                            src.CopyTo(dst);
                        copiedDumps.Add(ev.DumpPath);
                        totalDumpBytes += fi.Length;
                        result.FilesIncluded.Add(destName == fileName
                            ? $"dumps/{fileName}"
                            : $"dumps/{destName}（来自 {ev.DumpPath}，与已有 dump 同名已重命名）");
                    }
                    catch (Exception ex)
                    {
                        result.SkippedDumps.Add($"{ev.DumpPath}（复制失败: {ex.Message}）");
                    }
                }

                // 2.5 event_logs 目录：用 wevtutil 按时间窗导出 System channel 为标准 .evtx
                // 失败/超限不阻断主流程（与 dump 导出同样原则）
                try
                {
                    string logsDir = Path.Combine(tempDir, "event_logs");
                    Directory.CreateDirectory(logsDir);
                    string evtxPath = Path.Combine(logsDir, "System.evtx");

                    // wevtutil 要求 UTC ISO 8601 时间
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
                        // 先启动异步读取再等待退出：同步 ReadToEnd 在 wevtutil 挂起（无输出且不退出）时
                        // 会永久阻塞，导致下面的超时和 Kill 永远执行不到
                        Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(30000)) // 30s 超时
                        {
                            try { p.Kill(); } catch { }
                            // Kill 是异步通知：等进程真正退出后 ExitCode 才可访问，否则抛 InvalidOperationException
                            try { p.WaitForExit(5000); } catch { }
                        }

                        // 进程退出后管道关闭，读取立即完成；Wait 超时仅防御 Kill 失败的极端情况
                        string stderr;
                        try { stderr = stderrTask.Wait(2000) ? stderrTask.Result : string.Empty; }
                        catch { stderr = string.Empty; }

                        if (p.HasExited && p.ExitCode == 0 && File.Exists(evtxPath))
                        {
                            var fi = new FileInfo(evtxPath);
                            if (fi.Length == 0)
                            {
                                // 时间窗内 System channel 无事件，删除空文件
                                try { File.Delete(evtxPath); } catch { }
                            }
                            else if (fi.Length > MAX_EVTX_SIZE)
                            {
                                result.SkippedDumps.Add($"System.evtx（{fi.Length / 1024 / 1024} MB，超过 {MAX_EVTX_SIZE / 1024 / 1024} MB 上限，建议手动用事件查看器导出）");
                                try { File.Delete(evtxPath); } catch { }
                            }
                            else
                            {
                                result.FilesIncluded.Add($"event_logs/System.evtx");
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

    public class ExportResult
    {
        public bool Success { get; set; }
        public string ZipPath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public List<string> FilesIncluded { get; } = new();
        public List<string> SkippedDumps { get; } = new();
    }

    // —— HTML 生成 ——

    private static string BuildHtml(List<CrashEvent> events, DateTime start, DateTime end, ExportResult progress,
        EnvironmentSnapshot? env = null, List<SuspectRankEntry>? suspectRanking = null,
        EnvironmentHistoryData? envHistory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head>");
        sb.AppendLine("<meta charset=\"UTF-8\"/><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"/>");
        sb.AppendLine("<title>CrashSniffer 系统崩溃分析报告</title>");
        sb.AppendLine(InlineCss());
        sb.AppendLine("</head><body>");
        sb.AppendLine("<header><h1>🪦 CrashSniffer 系统崩溃分析报告</h1>");
        sb.AppendLine($"<p class=\"meta\">生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss} &nbsp;|&nbsp; 查询时间范围：<b>{start:yyyy-MM-dd HH:mm}</b> → <b>{end:yyyy-MM-dd HH:mm}</b> &nbsp;|&nbsp; 共 <b>{events.Count}</b> 次崩溃事件</p>");
        sb.AppendLine("</header>");

        // 环境快照
        if (env != null)
            sb.AppendLine(BuildEnvSection(env));

        // 统计摘要
        sb.AppendLine("<section class=\"stat\">");
        var grp = events.GroupBy(e => e.Type).OrderBy(g => (int)g.Key).ToList();
        sb.AppendLine("<table class=\"stat-tbl\"><thead><tr><th>类型</th><th>数量</th></tr></thead><tbody>");
        foreach (var g in grp)
            sb.AppendLine($"<tr><td>{TypeName(g.Key)}</td><td>{g.Count()}</td></tr>");
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</section>");

        // 嫌疑驱动排行
        if (suspectRanking is { Count: > 0 })
        {
            sb.AppendLine("<section class=\"stat\"><h3 style=\"margin:0 0 10px;color:var(--accent)\">嫌疑驱动排行 (惯犯名单)</h3>");
            sb.AppendLine("<table class=\"stat-tbl\" style=\"width:auto;min-width:560px\"><thead><tr><th>#</th><th>驱动</th><th>说明</th><th>系统内版本</th><th>出现次数</th></tr></thead><tbody>");
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

        // 逐条详情
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            sb.AppendLine($"<article class=\"ev\"><h2 id=\"ev{i}\">#{i + 1} &nbsp; {TypeName(ev.Type)} &nbsp; <span class=\"ts\">{ev.Time:yyyy-MM-dd HH:mm:ss}</span></h2>");
            sb.AppendLine($"<p class=\"sum\"><b>摘要：</b>{Escape(ev.Summary)}</p>");

            // 停止代码卡片
            if (ev.BugCheckCode != 0)
            {
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">停止代码</span><span class=\"v mono\">0x{ev.BugCheckCode:X8}</span></div>");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">符号名</span><span class=\"v mono\">{Escape(ev.StopCode)}</span></div>");
                sb.AppendLine($"<div class=\"row\"><span class=\"k\">解释</span><span class=\"v\">{Escape(ev.StopCodeChinese)}</span></div>");
                sb.AppendLine("<div class=\"row\"><span class=\"k\">参数</span><span class=\"v mono\">");
                for (int p = 0; p < ev.BugCheckParams.Length; p++)
                    sb.Append($"P{p} = 0x{ev.BugCheckParams[p]:X16}<br/>");
                sb.AppendLine("</span></div>");
                sb.AppendLine("</div>");
            }

            // dump + 嫌疑驱动
            if (!string.IsNullOrWhiteSpace(ev.DumpPath) || !string.IsNullOrWhiteSpace(ev.SuspectDriver))
            {
                sb.AppendLine("<div class=\"card\">");
                if (!string.IsNullOrWhiteSpace(ev.DumpPath))
                    sb.AppendLine($"<div class=\"row\"><span class=\"k\">Dump 文件</span><span class=\"v mono\">{Escape(ev.DumpPath)} <small>({ev.DumpSize / 1024 / 1024} MB)</small></span></div>");
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
                sb.AppendLine("<details class=\"raw\"><summary>事件原始消息</summary><pre>" + Escape(ev.EventMessage) + "</pre></details>");
            }

            // 排障建议
            if (ev.Troubleshooting.Count > 0)
            {
                sb.AppendLine("<div class=\"sug\"><h3>💡 排障建议</h3><ol>");
                foreach (var s in ev.Troubleshooting)
                    sb.AppendLine($"<li>{Escape(s)}</li>");
                sb.AppendLine("</ol></div>");
            }

            // 关联事件
            if (ev.RelatedEvents.Count > 0)
            {
                sb.AppendLine("<details class=\"rel\"><summary>查看前后 5 分钟关联事件 (" + ev.RelatedEvents.Count + " 条)</summary>");
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
                sb.AppendLine("<section class=\"stat\"><h3 style=\"margin:0 0 10px;color:var(--accent)\">🕐 环境变更时间线</h3>");
                sb.AppendLine("<table class=\"rel-tbl\"><thead><tr><th>检测时间</th><th>类别</th><th>项目</th><th>旧值</th><th>新值</th></tr></thead><tbody>");
                foreach (var c in envHistory.Changes.OrderByDescending(c => c.Time))
                    sb.AppendLine($"<tr><td>{c.Time:yyyy-MM-dd HH:mm}</td><td>{EnvironmentChangeDetector.CategoryLabel(c.Category)}</td><td>{Escape(c.Item)}</td><td class=\"mono\">{Escape(c.OldValue)}</td><td class=\"mono warn\">{Escape(c.NewValue)}</td></tr>");
                sb.AppendLine("</tbody></table>");
                sb.AppendLine($"<p class=\"meta\" style=\"margin:8px 0 0\">共 {envHistory.Changes.Count} 条变更 · {envHistory.Snapshots.Count} 份环境快照</p>");
                sb.AppendLine("</section>");
            }
            else if (envHistory.Snapshots.Count > 0)
            {
                sb.AppendLine("<section class=\"stat\"><h3 style=\"margin:0 0 10px;color:var(--accent)\">🕐 环境变更时间线</h3><p class=\"meta\">已有环境快照记录，暂未检测到任何变更。</p></section>");
            }
        }

        sb.AppendLine("<footer><p>CrashSniffer v2.0 · 本报告由工具自动生成，建议结合 WinDbg 深入分析。</p></footer>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// 环境快照 HTML 区块
    /// </summary>
    private static string BuildEnvSection(EnvironmentSnapshot env)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<section class=\"stat\"><h3 style=\"margin:0 0 10px;color:var(--accent)\">🖥 硬件 / 驱动环境快照</h3>");
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

    private static string InlineCss()
    {
        return @"
<style>
:root{--bg:#0f172a;--fg:#e2e8f0;--card:#1e293b;--muted:#94a3b8;--accent:#38bdf8;--warn:#f59e0b;--border:#334155}
*{box-sizing:border-box}
body{font-family: 'Segoe UI','Microsoft YaHei',system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0;padding:24px;line-height:1.6}
header{margin-bottom:24px;border-bottom:1px solid var(--border);padding-bottom:16px}
header h1{margin:0 0 8px;font-size:26px;color:#f8fafc}
.meta{margin:0;color:var(--muted);font-size:14px}
.stat{background:var(--card);padding:16px 20px;border-radius:10px;margin-bottom:24px;border:1px solid var(--border)}
.stat-tbl{border-collapse:collapse;width:420px}
.stat-tbl th,.stat-tbl td{text-align:left;padding:6px 12px;border-bottom:1px dashed var(--border);font-size:14px}
.stat-tbl th{color:var(--accent);font-weight:600}
.ev{background:var(--card);border:1px solid var(--border);border-radius:12px;padding:20px 24px;margin-bottom:20px}
.ev h2{margin:0 0 12px;font-size:18px;color:#f8fafc;border-bottom:1px dashed var(--border);padding-bottom:8px}
.ev h2 .ts{color:var(--accent);font-size:15px;font-weight:500;margin-left:8px}
.sum{color:#cbd5e1;margin:0 0 14px}
.card{background:#0b1220;border:1px solid var(--border);border-radius:8px;padding:12px 16px;margin:8px 0}
.card.whea{border-left:4px solid var(--warn)}
.row{display:flex;padding:4px 0;border-bottom:1px dashed rgba(148,163,184,.1)}
.row:last-child{border-bottom:none}
.row .k{width:110px;color:var(--muted);font-size:13px;flex-shrink:0}
.row .v{flex:1;font-size:14px;word-break:break-all}
.mono{font-family:Consolas,'Cascadia Code',monospace;font-size:13px}
.warn{color:var(--warn)!important;font-weight:600}
.sug{background:rgba(56,189,248,.08);border:1px solid rgba(56,189,248,.3);border-radius:8px;padding:12px 18px;margin-top:12px}
.sug h3{margin:0 0 6px;color:var(--accent);font-size:15px}
.sug ol{margin:4px 0 4px 18px;padding:0}
.sug li{padding:2px 0;font-size:14px}
details{margin-top:10px;border:1px solid var(--border);border-radius:6px;padding:8px 12px;background:#0b1220}
details summary{cursor:pointer;color:var(--accent);font-size:14px;user-select:none}
details pre{margin:8px 0 0;font-size:12px;color:#cbd5e1;background:#060a14;padding:10px;border-radius:6px;max-height:240px;overflow:auto;white-space:pre-wrap}
.rel-tbl{width:100%;border-collapse:collapse;margin-top:10px;font-size:13px}
.rel-tbl th,.rel-tbl td{padding:5px 8px;border-bottom:1px dashed var(--border);text-align:left;vertical-align:top}
.rel-tbl th{color:var(--accent);font-weight:600}
.note{padding:14px 18px;border-radius:10px;background:var(--card);border:1px solid var(--border);margin-top:20px}
.note.warn{border-left:4px solid var(--warn)}
.note h3{margin:0 0 6px;color:var(--warn);font-size:15px}
footer{margin-top:30px;color:var(--muted);font-size:12px;text-align:center;padding-top:16px;border-top:1px solid var(--border)}
</style>";
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
