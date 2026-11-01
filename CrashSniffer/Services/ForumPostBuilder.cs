using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 生成适合直接粘贴到贴吧 / NGA / Chiphell / V2EX / GitHub Issue 的结构化「崩溃求助速帖」摘要。
/// </summary>
public static class ForumPostBuilder
{
    /// <summary>
    /// 生成单次崩溃事件的发帖求助摘要（Markdown 兼容纯文本）
    /// </summary>
    public static string BuildForEvent(
        CrashEvent ev,
        EnvironmentSnapshot? env = null,
        HardwareSnapshot? hwSnapshot = null,
        IReadOnlyList<EnvironmentChange>? allChanges = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【CrashSniffer 崩溃诊断求助速帖】");
        sb.AppendLine($"⏱️ 事发时间：{ev.Time:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"🚨 崩溃类型：{TypeLabel(ev.Type)}");
        sb.AppendLine($"📌 摘要说明：{ev.Summary}");

        if (ev.BugCheckCode != 0)
        {
            sb.AppendLine($"🟦 停止代码：0x{ev.BugCheckCode:X8} ({ev.StopCode})");
            if (!string.IsNullOrWhiteSpace(ev.StopCodeChinese))
                sb.AppendLine($"📖 代码释义：{ev.StopCodeChinese}");
            sb.AppendLine($"🔢 参数列表：P0=0x{ev.BugCheckParams[0]:X16}, P1=0x{ev.BugCheckParams[1]:X16}, P2=0x{ev.BugCheckParams[2]:X16}, P3=0x{ev.BugCheckParams[3]:X16}");
        }

        if (!string.IsNullOrWhiteSpace(ev.FaultingModuleOffset))
            sb.AppendLine($"🎯 崩溃指令偏移：{ev.FaultingModuleOffset}" + (ev.ExceptionAddress != 0 ? $" (0x{ev.ExceptionAddress:X16})" : string.Empty));

        if (!string.IsNullOrWhiteSpace(ev.SuspectDriver))
            sb.AppendLine($"⚠️ 嫌疑驱动：{ev.SuspectDriver}");

        var dumps = ev.GetEffectiveDumpPaths();
        if (dumps.Count > 0)
        {
            string sizeNote = ev.DumpSize > 0 ? $" ({CrashEvent.FormatFileSize(ev.DumpSize)})" : string.Empty;
            sb.AppendLine($"💾 转储文件：{string.Join(" ; ", dumps)}{sizeNote}");
        }

        if (!string.IsNullOrWhiteSpace(ev.WheaErrorType) || !string.IsNullOrWhiteSpace(ev.WheaLocation))
        {
            sb.AppendLine($"🔥 WHEA 硬件错误：{ev.WheaErrorType} | 位置：{ev.WheaLocation}");
            if (!string.IsNullOrWhiteSpace(ev.WheaHardwarePart))
                sb.AppendLine($"🔎 报错硬件部位：{ev.WheaHardwarePart}");
        }

        // 硬件配置环境
        if (env != null)
        {
            sb.AppendLine();
            sb.AppendLine("【💻 硬件与驱动环境】");
            if (!string.IsNullOrWhiteSpace(env.CpuName))
                sb.AppendLine($"- CPU：{env.CpuName}");
            if (!string.IsNullOrWhiteSpace(env.Motherboard))
                sb.AppendLine($"- 主板：{env.Motherboard}" + (string.IsNullOrWhiteSpace(env.BiosVersion) ? "" : $" (BIOS: {env.BiosVersion} / {env.BiosDate})"));
            foreach (var gpu in env.Gpus)
                sb.AppendLine($"- 显卡：{gpu.Name} (驱动 {gpu.DriverVersion}, {gpu.DriverDate})");
            if (!string.IsNullOrWhiteSpace(env.MemorySummary))
                sb.AppendLine($"- 内存：{env.MemorySummary}" + (string.IsNullOrWhiteSpace(env.XmpNote) ? "" : $" [{env.XmpNote}]"));
            if (!string.IsNullOrWhiteSpace(env.OsName))
                sb.AppendLine($"- 系统：{env.OsName} {env.OsDisplayVersion} (Build {env.OsBuild})");
        }

        // 崩溃前传感器状态
        if (hwSnapshot != null)
        {
            var pre = HardwareAnomalyAnalyzer.GetPreCrashReadings(hwSnapshot);
            if (pre.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"【🌡️ 崩溃前硬件传感器状态（{pre.Count} 个采样点）】");
                var metrics = new List<string>();
                var cpuTemps = pre.Where(r => r.CpuTemperature.HasValue).Select(r => r.CpuTemperature!.Value).ToList();
                if (cpuTemps.Count > 0) metrics.Add($"CPU峰值 {cpuTemps.Max():F1}℃");
                var gpuTemps = pre.Where(r => r.GpuTemperature.HasValue).Select(r => r.GpuTemperature!.Value).ToList();
                if (gpuTemps.Count > 0) metrics.Add($"GPU峰值 {gpuTemps.Max():F1}℃");
                var hotspots = pre.Where(r => r.GpuHotspot.HasValue).Select(r => r.GpuHotspot!.Value).ToList();
                if (hotspots.Count > 0) metrics.Add($"GPU热点 {hotspots.Max():F1}℃");
                var memLoads = pre.Where(r => r.MemoryLoad.HasValue).Select(r => r.MemoryLoad!.Value).ToList();
                if (memLoads.Count > 0) metrics.Add($"内存占用 {memLoads.Max():F0}%");
                var v12 = pre.Where(r => r.Voltage12V.HasValue).Select(r => r.Voltage12V!.Value).ToList();
                if (v12.Count > 0) metrics.Add($"+12V {v12.Min():F2}~{v12.Max():F2}V");
                if (metrics.Count > 0)
                    sb.AppendLine("- 峰值概览：" + string.Join(" | ", metrics));

                var anomalies = HardwareAnomalyAnalyzer.Analyze(hwSnapshot);
                if (anomalies.Count == 0)
                {
                    sb.AppendLine("- 异常诊断：✅ 崩溃前未检测到过热、掉压或降频异常");
                }
                else
                {
                    foreach (var a in anomalies)
                        sb.AppendLine($"- 异常告警：[{a.Severity}] {a.Title} — {a.Detail}");
                }
            }
        }

        // 事发前环境变更
        if (allChanges is { Count: > 0 })
        {
            var before = allChanges.Where(c => c.Time <= ev.Time).OrderByDescending(c => c.Time).Take(5).ToList();
            if (before.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【🕐 事发前近期环境变更】");
                foreach (var c in before)
                {
                    sb.AppendLine($"- {EnvironmentChangeDetector.RelativeBefore(ev.Time, c.Time)} [{EnvironmentChangeDetector.CategoryLabel(c.Category)}] {c.Item}：{c.OldValue} → {c.NewValue}");
                }
            }
        }

        // 排障建议前 4 条
        if (ev.Troubleshooting.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【💡 CrashSniffer 初步诊断建议】");
            int count = Math.Min(4, ev.Troubleshooting.Count);
            for (int i = 0; i < count; i++)
                sb.AppendLine($"{i + 1}. {ev.Troubleshooting[i]}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 生成整体报告的多事件汇总求助速帖（用于 HTML 报告顶部一键复制）
    /// </summary>
    public static string BuildForReport(
        IReadOnlyList<CrashEvent> events,
        EnvironmentSnapshot? env = null,
        IReadOnlyList<SuspectRankEntry>? suspectRanking = null,
        IReadOnlyList<EnvironmentChange>? allChanges = null)
    {
        if (events.Count == 0)
            return "【CrashSniffer 崩溃诊断报告】当前时间范围内未检测到崩溃事件。";

        if (events.Count == 1)
        {
            var ev = events[0];
            HardwareSnapshot? snap = null;
            try
            {
                snap = HardwareSnapshotStore.FindByCrashTime(ev.Time)
                    ?? ContinuousSampleStore.BuildSnapshotForCrash(ev.Time);
            }
            catch { }
            return BuildForEvent(ev, env, snap, allChanges);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"【CrashSniffer 崩溃诊断汇总速帖（共 {events.Count} 次崩溃）】");
        if (env != null)
        {
            var envParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(env.CpuName)) envParts.Add(env.CpuName);
            if (!string.IsNullOrWhiteSpace(env.Motherboard)) envParts.Add(env.Motherboard);
            if (env.Gpus.Count > 0) envParts.Add(string.Join("/", env.Gpus.Select(g => $"{g.Name}({g.DriverVersion})")));
            if (!string.IsNullOrWhiteSpace(env.MemorySummary)) envParts.Add(env.MemorySummary);
            if (envParts.Count > 0)
                sb.AppendLine("💻 硬件配置：" + string.Join(" | ", envParts));
        }

        if (suspectRanking is { Count: > 0 })
        {
            string topDrivers = string.Join("、", suspectRanking.Take(3).Select(r => $"{r.Driver} ({r.Count}次)"));
            sb.AppendLine("🎯 头号嫌疑驱动：" + topDrivers);
        }

        sb.AppendLine();
        sb.AppendLine("📋 最近崩溃记录（前 5 条）：");
        int take = Math.Min(5, events.Count);
        for (int i = 0; i < take; i++)
        {
            var e = events[i];
            string code = e.BugCheckCode != 0 ? $" [0x{e.BugCheckCode:X} {e.StopCode}]" : "";
            string mod = !string.IsNullOrWhiteSpace(e.FaultingModuleOffset)
                ? $" @{e.FaultingModuleOffset}"
                : (!string.IsNullOrWhiteSpace(e.SuspectDriver) ? $" @{e.SuspectDriver}" : "");
            sb.AppendLine($"{i + 1}. {e.Time:yyyy-MM-dd HH:mm:ss} | {TypeLabel(e.Type)}{code}{mod} — {e.Summary}");
        }

        // 附上最近一次崩溃的完整详情
        sb.AppendLine();
        sb.AppendLine("─────────────────────────────");
        HardwareSnapshot? latestSnap = null;
        try
        {
            latestSnap = HardwareSnapshotStore.FindByCrashTime(events[0].Time)
                      ?? ContinuousSampleStore.BuildSnapshotForCrash(events[0].Time);
        }
        catch { }
        sb.AppendLine(BuildForEvent(events[0], env: null, latestSnap, allChanges));
        return sb.ToString().TrimEnd();
    }

    private static string TypeLabel(CrashType t) => t switch
    {
        CrashType.BSOD => "BSOD 蓝屏",
        CrashType.WHEA_Error => "WHEA 硬件错误",
        CrashType.GPU_TDR => "GPU TDR 驱动重置",
        CrashType.KernelPower_41 => "Kernel-Power 41 异常断电重启",
        CrashType.Shutdown_6008 => "Event 6008 意外关机",
        CrashType.LiveKernel => "LiveKernel 内核挂死转储",
        _ => t.ToString(),
    };
}
