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
            foreach (var file in Directory.EnumerateFiles(root, "*.dmp", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists) continue;
                    if (fi.LastWriteTime < startTime || fi.LastWriteTime > endTime) continue;

                    result.Add(BuildEvent(file, fi, root));
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

    private static CrashEvent BuildEvent(string file, FileInfo fi, string root)
    {
        // 尝试用 minidump 解析器提取 BugCheckCode（很多 LiveKernel dump 头里有 0x141/0x117 等）
        CrashEvent? parsed = null;
        try { parsed = MinidumpParser.ParseFile(file); }
        catch { parsed = null; }

        var ev = parsed ?? new CrashEvent();
        ev.Type = CrashType.LiveKernel;
        ev.Time = fi.LastWriteTime;
        ev.DumpPath = file;
        ev.DumpSize = fi.Length;

        // 子目录名（WATCHDOG / Wi-Fi 等）有诊断价值
        string subDir = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
        string where = string.IsNullOrEmpty(subDir) || subDir.Equals("LiveKernelReports", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : $" [{subDir}]";

        if (ev.BugCheckCode != 0)
            ev.Summary = $"LiveKernel 挂死 dump{where} 0x{ev.BugCheckCode:X} {ev.StopCode}（未蓝屏）";
        else
            ev.Summary = $"LiveKernel 挂死 dump{where}（未蓝屏，可用 WinDbg 打开分析）";

        ev.Troubleshooting.Clear();
        ev.Troubleshooting.AddRange(new[]
        {
            "LiveKernelReport：设备（多为 GPU）挂死但系统没有蓝屏，单独留下 dump 记录",
            "显卡类 (WATCHDOG 子目录)：DDU 重装显卡驱动；GPU 超频/显存超频先还原默认",
            "供电不足也会触发：检查显卡电源线、电源额定功率与 12V 输出",
            "建议用 WinDbg Preview 打开该 dump 执行 !analyze -v 看具体挂死原因",
        });

        return ev;
    }
}
