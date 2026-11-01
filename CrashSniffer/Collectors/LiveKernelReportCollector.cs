using System;
using System.Collections.Generic;
using System.IO;
using CrashSniffer.Models;

namespace CrashSniffer.Collectors;

/// <summary>
/// 抓取 C:\Windows\LiveKernelReports 下的 dump：
/// GPU/设备挂死但未蓝屏的场景（如 0x141 TDR 未恢复成功）只在这里留痕
/// </summary>
public static class LiveKernelReportCollector
{
    /// <summary>单次扫描最多收录的 dump 文件数（与事件采集上限同级的防护）</summary>
    private const int MAX_COLLECT_FILES = 500;

    /// <summary>
    /// 收集指定时间范围内（按文件修改时间）的 LiveKernelReport dump
    /// </summary>
    public static List<CrashEvent> Collect(DateTime startTime, DateTime endTime)
    {
        var result = new List<CrashEvent>();

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string root = Path.Combine(winDir, "LiveKernelReports");
        if (!Directory.Exists(root)) return result;

        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };
            foreach (var file in Directory.EnumerateFiles(root, "*.dmp", opts))
            {
                if (result.Count >= MAX_COLLECT_FILES) break;
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists) continue;
                    if (fi.LastWriteTime < startTime || fi.LastWriteTime > endTime) continue;

                    result.Add(BuildEvent(file, fi));
                }
                catch
                {
                    // 单个文件失败不影响整体
                }
            }
        }
        catch
        {
            // 目录不可访问时静默返回
        }

        return result;
    }

    private static CrashEvent BuildEvent(string file, FileInfo fi)
    {
        CrashEvent? parsed = null;
        try { parsed = MinidumpParser.ParseFile(file); }
        catch { parsed = null; }

        var ev = parsed ?? new CrashEvent();
        ev.Type = CrashType.LiveKernel;
        ev.Time = fi.LastWriteTime;
        ev.DumpPath = file;
        ev.DumpSize = fi.Length;
        ev.AddDumpPath(file);

        // 子目录名（WATCHDOG / WHEA / Wi-Fi 等）有诊断价值
        string subDir = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
        string where = string.IsNullOrEmpty(subDir) || subDir.Equals("LiveKernelReports", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : $" [{subDir}]";
        string offsetNote = !string.IsNullOrWhiteSpace(ev.FaultingModuleOffset)
            ? $" · {ev.FaultingModuleOffset}" : string.Empty;
        string sizeNote = fi.Length > 0 ? $" ({CrashEvent.FormatFileSize(fi.Length)})" : string.Empty;

        if (ev.BugCheckCode != 0)
            ev.Summary = $"LiveKernel 挂死{where} 0x{ev.BugCheckCode:X} {ev.StopCode}{offsetNote}{sizeNote}";
        else
            ev.Summary = $"LiveKernel 挂死{where}{offsetNote}{sizeNote}（未蓝屏，可用 WinDbg 打开分析）";

        var generalTips = new[]
        {
            "LiveKernelReport：设备（多为 GPU 或 PCIe/WHEA）异常挂死但系统未蓝屏，自动留下内核实时转储",
            "显卡类 (WATCHDOG 子目录)：使用 DDU 彻底重装显卡驱动；GPU 超频/降压/显存超频先还原默认",
            "供电或线材接触不良也会触发：检查显卡供电接口、DP/HDMI 线材与电源 +12V 稳定性",
            "可用 WinDbg Preview 打开该 .dmp 文件执行 !analyze -v 查看完整调用栈",
        };
        foreach (var tip in generalTips)
        {
            if (!ev.Troubleshooting.Contains(tip))
                ev.Troubleshooting.Add(tip);
        }

        return ev;
    }
}
