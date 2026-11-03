using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CrashSniffer.Services;

/// <summary>
/// WinDbg / cdb.exe 分析结果
/// </summary>
public record WinDbgAnalysisResult(
    bool Success,
    string? FailureBucket,
    string? ProcessName,
    string? CallStack,
    string RawOutput,
    string? ErrorMessage);

/// <summary>
/// 本地 WinDbg / cdb.exe 调试引擎桥接服务：
/// 自动检测系统中已安装的 Windows SDK 或 Microsoft Store WinDbg Preview 调试器，
/// 支持对 Minidump / MEMORY.DMP 执行自动化的 !analyze -v 深度调用栈与符号解析。
/// 若系统未安装调试器，提供友好的安装与配置指引。
/// </summary>
public static class WinDbgService
{
    private static string? _cachedDebuggerPath;
    private static bool _probed;

    /// <summary>当前系统是否检测到可用的 cdb.exe / kd.exe 调试引擎</summary>
    public static bool IsAvailable => !string.IsNullOrEmpty(GetDebuggerPath());

    /// <summary>获取检测到的调试器完整路径，未找到返回 null</summary>
    public static string? GetDebuggerPath()
    {
        if (_probed) return _cachedDebuggerPath;
        _probed = true;
        _cachedDebuggerPath = ProbeDebuggerPath();
        return _cachedDebuggerPath;
    }

    /// <summary>重置缓存以重新探测系统调试器路径</summary>
    public static void ResetProbe()
    {
        _probed = false;
        _cachedDebuggerPath = null;
    }

    private static string? ProbeDebuggerPath()
    {
        // 1. 检查环境变量 PATH 中是否有 cdb.exe 或 kd.exe
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var split = pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in split)
            {
                try
                {
                    string cdb = Path.Combine(dir, "cdb.exe");
                    if (File.Exists(cdb)) return cdb;
                    string kd = Path.Combine(dir, "kd.exe");
                    if (File.Exists(kd)) return kd;
                }
                catch { }
            }
        }

        // 2. 检查常见 Windows Kits (SDK) 调试器路径
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] standardKits =
        [
            Path.Combine(programFilesX86, @"Windows Kits\10\Debuggers\x64\cdb.exe"),
            Path.Combine(programFilesX86, @"Windows Kits\11\Debuggers\x64\cdb.exe"),
            Path.Combine(programFilesX86, @"Windows Kits\10\Debuggers\x86\cdb.exe"),
            Path.Combine(programFilesX86, @"Windows Kits\11\Debuggers\x86\cdb.exe"),
            Path.Combine(programFilesX86, @"Windows Kits\10\Debuggers\x64\kd.exe"),
        ];
        foreach (var p in standardKits)
        {
            if (File.Exists(p)) return p;
        }

        // 3. 检查 Microsoft Store WinDbg Preview / WindowsApps 应用目录
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userAppsCdb = Path.Combine(localAppData, @"Microsoft\WindowsApps\cdb.exe");
        if (File.Exists(userAppsCdb)) return userAppsCdb;

        try
        {
            string winApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(winApps))
            {
                var matches = Directory.GetDirectories(winApps, "Microsoft.WinDbg_*");
                foreach (var match in matches)
                {
                    string target = Path.Combine(match, "cdb.exe");
                    if (File.Exists(target)) return target;
                    string subTarget = Path.Combine(match, "amd64", "cdb.exe");
                    if (File.Exists(subTarget)) return subTarget;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 异步调用 cdb.exe 执行 !analyze -v，提取符号化调用栈与故障特征
    /// </summary>
    public static async Task<WinDbgAnalysisResult> AnalyzeAsync(
        string dumpPath,
        string? customDebuggerPath = null,
        CancellationToken ct = default)
    {
        string? debugger = customDebuggerPath ?? GetDebuggerPath();
        if (string.IsNullOrWhiteSpace(debugger) || !File.Exists(debugger))
        {
            return new WinDbgAnalysisResult(
                Success: false,
                FailureBucket: null,
                ProcessName: null,
                CallStack: null,
                RawOutput: string.Empty,
                ErrorMessage: "系统未检测到 cdb.exe 或 kd.exe 调试器。请安装 Windows SDK Debugging Tools 或从微软商店安装 WinDbg Preview。");
        }

        if (string.IsNullOrWhiteSpace(dumpPath) || !File.Exists(dumpPath))
        {
            return new WinDbgAnalysisResult(
                Success: false,
                FailureBucket: null,
                ProcessName: null,
                CallStack: null,
                RawOutput: string.Empty,
                ErrorMessage: $"转储文件不存在或已被移动：{dumpPath}");
        }

        var sb = new StringBuilder();
        var startInfo = new ProcessStartInfo
        {
            FileName = debugger,
            Arguments = $"-z \"{dumpPath}\" -y \"srv*https://msdl.microsoft.com/download/symbols\" -c \"!analyze -v; q\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) sb.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) sb.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 超时保护：单次 !analyze -v 最多等待 50 秒（含从微软服务器在线下载符号的时间）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(50));

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new WinDbgAnalysisResult(
                    Success: false,
                    FailureBucket: null,
                    ProcessName: null,
                    CallStack: null,
                    RawOutput: sb.ToString(),
                    ErrorMessage: ct.IsCancellationRequested
                        ? "分析已被用户取消。"
                        : "分析超时（超过 50 秒），可能是微软符号服务器网络拉取缓慢，已中止调试进程。");
            }

            string raw = sb.ToString();
            var parsed = ParseAnalyzeOutput(raw);
            return new WinDbgAnalysisResult(
                Success: true,
                FailureBucket: parsed.FailureBucket,
                ProcessName: parsed.ProcessName,
                CallStack: parsed.CallStack,
                RawOutput: raw,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new WinDbgAnalysisResult(
                Success: false,
                FailureBucket: null,
                ProcessName: null,
                CallStack: null,
                RawOutput: sb.ToString(),
                ErrorMessage: $"执行调试引擎异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 从 !analyze -v 的原始标准输出中提取 STACK_TEXT 调用栈、FAILURE_BUCKET_ID 与 PROCESS_NAME
    /// </summary>
    internal static (string? FailureBucket, string? ProcessName, string? CallStack) ParseAnalyzeOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null, null);

        string? bucket = null;
        string? procName = null;
        string? stack = null;

        var bucketMatch = Regex.Match(text, @"FAILURE_BUCKET_ID:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        if (bucketMatch.Success)
            bucket = bucketMatch.Groups[1].Value.Trim();

        var procMatch = Regex.Match(text, @"PROCESS_NAME:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        if (procMatch.Success)
            procName = procMatch.Groups[1].Value.Trim();

        // 提取 STACK_TEXT: 区域直到下一个大写关键字行或空行
        var stackMatch = Regex.Match(text, @"STACK_TEXT:\s*\r?\n([\s\S]*?)(?=\r?\n\r?\n|\r?\n[A-Z0-9_]{3,}:|\z)", RegexOptions.IgnoreCase);
        if (stackMatch.Success && !string.IsNullOrWhiteSpace(stackMatch.Groups[1].Value))
        {
            stack = stackMatch.Groups[1].Value.Trim();
        }
        else
        {
            // 备选调用栈匹配模式：带地址与模块的连续行
            var altStackMatch = Regex.Match(text, @"(?:#\s+Child-SP|\bRetAddr\b)[^\r\n]*\r?\n((?:[0-9a-fA-F`]+[^\r\n]+\r?\n)+)", RegexOptions.IgnoreCase);
            if (altStackMatch.Success)
                stack = altStackMatch.Groups[1].Value.TrimEnd();
        }

        return (bucket, procName, stack);
    }
}
