using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace CrashSniffer.Services;

/// <summary>
/// 系统蓝屏转储 (CrashControl) 配置状态
/// </summary>
public sealed class CrashDumpConfigStatus
{
    public int CrashDumpEnabled { get; set; } = 7;
    public string DumpModeName => CrashDumpEnabled switch
    {
        0 => "未启用 (0 - 蓝屏不保存任何转储)",
        1 => "完整内存转储 (1 - Complete Memory Dump)",
        2 => "核心内存转储 (2 - Kernel Memory Dump)",
        3 => "小内存转储 (3 - Small Memory Dump 256KB，推荐)",
        7 => "自动内存转储 (7 - Automatic Memory Dump，系统默认)",
        _ => $"未知模式 ({CrashDumpEnabled})",
    };

    public string MinidumpDir { get; set; } = string.Empty;
    public string DumpFile { get; set; } = string.Empty;
    public bool LogEventEnabled { get; set; } = true;
    public bool AlwaysKeepMemoryDump { get; set; }
    public bool HasActivePageFile { get; set; } = true;
    public bool MinidumpDirExists { get; set; }

    public List<string> Issues { get; } = new();

    /// <summary>是否处于推荐的健康状态</summary>
    public bool IsOptimal => CrashDumpEnabled != 0 && LogEventEnabled && AlwaysKeepMemoryDump && HasActivePageFile && MinidumpDirExists;
}

/// <summary>
/// 检查与一键优化 Windows 蓝屏转储注册表配置（HKLM\SYSTEM\CurrentControlSet\Control\CrashControl）
/// </summary>
public static class CrashDumpConfigService
{
    private const string CrashControlKeyPath = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const string MemoryManagementKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    /// <summary>
    /// 读取当前系统崩溃转储配置并进行健康体检
    /// </summary>
    public static CrashDumpConfigStatus Inspect()
    {
        var status = new CrashDumpConfigStatus();
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        status.MinidumpDir = Path.Combine(winDir, "Minidump");
        status.DumpFile = Path.Combine(winDir, "MEMORY.DMP");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CrashControlKeyPath, writable: false);
            if (key != null)
            {
                if (key.GetValue("CrashDumpEnabled") is int mode)
                    status.CrashDumpEnabled = mode;

                if (key.GetValue("MinidumpDir") is string miniDir && !string.IsNullOrWhiteSpace(miniDir))
                    status.MinidumpDir = Environment.ExpandEnvironmentVariables(miniDir.Trim());

                if (key.GetValue("DumpFile") is string dumpFile && !string.IsNullOrWhiteSpace(dumpFile))
                    status.DumpFile = Environment.ExpandEnvironmentVariables(dumpFile.Trim());

                if (key.GetValue("LogEvent") is int logEv)
                    status.LogEventEnabled = logEv != 0;

                if (key.GetValue("AlwaysKeepMemoryDump") is int keep)
                    status.AlwaysKeepMemoryDump = keep != 0;
            }
        }
        catch
        {
            // 无权限或注册表读取异常时保持默认推断
        }

        try
        {
            using var mmKey = Registry.LocalMachine.OpenSubKey(MemoryManagementKeyPath, writable: false);
            if (mmKey != null)
            {
                if (mmKey.GetValue("ExistingPageFiles") is string[] existing && existing.Length > 0)
                {
                    status.HasActivePageFile = true;
                }
                else if (mmKey.GetValue("PagingFiles") is string[] cfg)
                {
                    status.HasActivePageFile = cfg.Length > 0 && !string.IsNullOrWhiteSpace(cfg[0]);
                }
            }
        }
        catch { }

        status.MinidumpDirExists = !string.IsNullOrWhiteSpace(status.MinidumpDir) && Directory.Exists(status.MinidumpDir);

        EvaluateIssues(status);
        return status;
    }

    internal static void EvaluateIssues(CrashDumpConfigStatus status)
    {
        status.Issues.Clear();
        if (status.CrashDumpEnabled == 0)
            status.Issues.Add("当前已关闭内存转储 (CrashDumpEnabled=0)，蓝屏重启后不会生成任何 .dmp 文件。");

        if (!status.LogEventEnabled)
            status.Issues.Add("未开启将系统错误写入事件日志 (LogEvent=0)，蓝屏后不会记录 Event 1001。");

        if (!status.AlwaysKeepMemoryDump)
            status.Issues.Add("未开启「始终保留内存转储」(AlwaysKeepMemoryDump=0)，Windows 存储感知或磁盘剩余空间不足时会自动清理历史 Minidump 文件。");

        if (!status.MinidumpDirExists)
            status.Issues.Add($"Minidump 目录尚未创建：{status.MinidumpDir}");

        if (!status.HasActivePageFile)
            status.Issues.Add("未检测到活动分页文件（虚拟内存可能被禁用），Windows 蓝屏时必须依赖系统盘分页文件才能落盘生成 dump。");
    }

    /// <summary>
    /// 一键应用推荐的转储配置：开启小内存转储 (256KB) + 写入系统日志 + 永久保留防自动删除 + 确保目录存在
    /// </summary>
    public static bool ApplyOptimalConfig(bool setSmallDumpMode, out string message)
    {
        try
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string defaultMiniDir = Path.Combine(winDir, "Minidump");

            using var key = Registry.LocalMachine.OpenSubKey(CrashControlKeyPath, writable: true);
            if (key == null)
            {
                message = "无法以写入模式打开注册表项 CrashControl，请确认程序以管理员身份运行。";
                return false;
            }

            int currentMode = key.GetValue("CrashDumpEnabled") is int m ? m : 7;
            if (setSmallDumpMode || currentMode == 0)
            {
                key.SetValue("CrashDumpEnabled", 3, RegistryValueKind.DWord);
            }

            key.SetValue("LogEvent", 1, RegistryValueKind.DWord);
            key.SetValue("Overwrite", 1, RegistryValueKind.DWord);
            key.SetValue("AlwaysKeepMemoryDump", 1, RegistryValueKind.DWord);

            string? existingMiniDir = key.GetValue("MinidumpDir") as string;
            if (string.IsNullOrWhiteSpace(existingMiniDir))
            {
                key.SetValue("MinidumpDir", @"%SystemRoot%\Minidump", RegistryValueKind.ExpandString);
                existingMiniDir = defaultMiniDir;
            }
            else
            {
                existingMiniDir = Environment.ExpandEnvironmentVariables(existingMiniDir.Trim());
            }

            if (!Directory.Exists(existingMiniDir))
                Directory.CreateDirectory(existingMiniDir);

            message = "已成功优化系统转储设置：\n" +
                      $"• 转储模式：{(setSmallDumpMode || currentMode == 0 ? "小内存转储 (256KB)" : $"保留原模式 ({currentMode})")}\n" +
                      "• 写入系统事件日志 (LogEvent)：已开启\n" +
                      "• 始终保留内存转储 (AlwaysKeepMemoryDump)：已开启（防系统自动清理）\n" +
                      $"• 转储目录：{existingMiniDir}（已就绪）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入转储设置失败：{ex.Message}";
            return false;
        }
    }
}
