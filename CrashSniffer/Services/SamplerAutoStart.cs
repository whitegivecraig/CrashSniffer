using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CrashSniffer.Services;

/// <summary>
/// 后台采样器的开机自启管理：通过计划任务（登录时触发、最高权限）运行 CrashSniffer.exe --sampler。
/// 使用计划任务而非 Run 注册表项，避免 requireAdministrator 清单在登录时弹出 UAC。
/// </summary>
public static class SamplerAutoStart
{
    public const string TaskName = "CrashSnifferSampler";

    /// <summary>计划任务是否已注册</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var p = Process.Start(CreateStartInfo(new[] { "/Query", "/TN", TaskName }));
            if (p == null) return false;
            // 先异步读取再等待退出，避免 schtasks 挂起时 ReadToEnd 永久阻塞
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>注册开机采样任务，并立即启动一次</summary>
    public static bool Enable(string exePath, out string error)
    {
        if (!RunSchtasks(BuildCreateArgs(exePath), 10000, out error)) return false;
        RunSchtasks(new[] { "/Run", "/TN", TaskName }, 5000, out _);
        return true;
    }

    /// <summary>停止正在运行的采样任务并删除注册</summary>
    public static bool Disable(out string error)
    {
        RunSchtasks(new[] { "/End", "/TN", TaskName }, 5000, out _);
        return RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" }, 10000, out error);
    }

    internal static List<string> BuildCreateArgs(string exePath)
        => new()
        {
            "/Create",
            "/TN", TaskName,
            "/TR", $"\"{exePath}\" --sampler",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F",
        };

    internal static ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }

    private static bool RunSchtasks(IEnumerable<string> args, int timeoutMs, out string error)
    {
        error = string.Empty;
        try
        {
            using var p = Process.Start(CreateStartInfo(args));
            if (p == null)
            {
                error = "无法启动 schtasks.exe。";
                return false;
            }
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                try { p.WaitForExit(2000); } catch { }
                error = "schtasks.exe 执行超时。";
                return false;
            }
            string stdout = stdoutTask.Wait(2000) ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.Wait(2000) ? stderrTask.Result : string.Empty;
            if (p.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
