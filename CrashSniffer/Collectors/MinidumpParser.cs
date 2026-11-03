using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.Collectors;

/// <summary>
/// 解析 Windows Minidump (*.dmp) 与 PAGEDU64 内核/实时转储文件，
/// 提取 BSOD 停止代码、参数、崩溃指令偏移 (driver+0xOffset) 与嫌疑驱动。
/// 使用内置解析，不依赖 WinDbg。
/// </summary>
public static class MinidumpParser
{
    private const uint MDMP_SIGNATURE = 0x504D444D; // "MDMP"
    private const uint PAGE_DUMP_SIG_32 = 0x45474150; // "PAGE" (little-endian, for 32-bit)
    private const ulong PAGE_DUMP_SIG_64 = 0x3436554445474150; // "PAGEDU64" little-endian

    // Minidump Stream Types (MINIDUMP_STREAM_TYPE)
    private const uint STREAM_THREAD_LIST = 0x03;
    private const uint STREAM_MODULE_LIST = 0x04;          // ModuleListStream
    private const uint STREAM_EXCEPTION = 0x06;            // ExceptionStream
    private const uint STREAM_SYSTEM_INFO = 0x07;
    private const uint STREAM_UNLOADED_MODULE_LIST = 0x0E; // UnloadedModuleListStream (14)
    private const uint STREAM_BUGCHECK = 0x11;             // BugCheckInfoStream (旧版)
    private const uint STREAM_BUGCHECK_INFO = 0x15;        // BugCheckInfoStream (新版 0x15)

    // PAGEDU64 _TRIAGE_DUMP64 每个 _DUMP_DRIVER_ENTRY64 结构体固定 0x90 (144) 字节
    private const int TRIAGE_DRIVER_ENTRY_SIZE = 0x90;

    /// <summary>
    /// 收集指定时间范围内的 minidump / MEMORY.DMP 文件并解析（含注册表自定义转储路径）
    /// </summary>
    public static List<CrashEvent> Collect(DateTime startTime, DateTime endTime)
    {
        var result = new List<CrashEvent>();
        var scanPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 标准 minidump 目录与 MEMORY.DMP
        string miniDir = Path.Combine(winDir, "Minidump");
        if (Directory.Exists(miniDir))
            scanPaths.Add(miniDir);

        string memDump = Path.Combine(winDir, "MEMORY.DMP");
        if (File.Exists(memDump))
            scanPaths.Add(memDump);

        // 注册表 CrashControl 自定义路径补充
        try
        {
            var cfg = CrashDumpConfigService.Inspect();
            if (!string.IsNullOrWhiteSpace(cfg.MinidumpDir) && Directory.Exists(cfg.MinidumpDir))
                scanPaths.Add(cfg.MinidumpDir);
            if (!string.IsNullOrWhiteSpace(cfg.DumpFile) && File.Exists(cfg.DumpFile))
                scanPaths.Add(cfg.DumpFile);
        }
        catch { /* 忽略注册表读取异常 */ }

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scanPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    if (seenFiles.Add(Path.GetFullPath(path)))
                        ProcessDumpFile(path, startTime, endTime, result);
                }
                else if (Directory.Exists(path))
                {
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                    };
                    foreach (var file in Directory.EnumerateFiles(path, "*.dmp", opts))
                    {
                        if (seenFiles.Add(Path.GetFullPath(file)))
                            ProcessDumpFile(file, startTime, endTime, result);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"扫描 {path} 失败: {ex.Message}");
            }
        }

        // 扫描 Windows Error Reporting (WER) 归档目录中的转储及 Report.wer
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] werArchives =
        [
            Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"),
            Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"),
            Path.Combine(localAppData, @"Microsoft\Windows\WER\ReportArchive"),
        ];

        foreach (var werDir in werArchives)
        {
            if (Directory.Exists(werDir))
                ScanWerReports(werDir, startTime, endTime, seenFiles, result);
        }

        return result;
    }

    private static void ProcessDumpFile(string filePath, DateTime startTime, DateTime endTime, List<CrashEvent> result)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return;

            // 先放宽 ±30 分钟做文件修改时间粗筛（避免跨午夜蓝屏重启后文件落盘时间与内部崩溃时间戳偏差导致漏采）
            DateTime looseStart = startTime > DateTime.MinValue.AddMinutes(30) ? startTime.AddMinutes(-30) : DateTime.MinValue;
            DateTime looseEnd = endTime < DateTime.MaxValue.AddMinutes(-30) ? endTime.AddMinutes(30) : DateTime.MaxValue;
            if (fi.LastWriteTime < looseStart || fi.LastWriteTime > looseEnd)
                return;

            var ev = ParseFile(filePath);
            if (ev == null) return;

            // 只要文件修改时间或 dump 内部记录的崩溃时间落在查询区间内即收录
            bool fileInWindow = fi.LastWriteTime >= startTime && fi.LastWriteTime <= endTime;
            bool crashInWindow = ev.Time >= startTime && ev.Time <= endTime;
            if (fileInWindow || crashInWindow)
                result.Add(ev);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"解析 {filePath} 失败: {ex.Message}");
        }
    }

    private static void ScanWerReports(
        string werDir,
        DateTime startTime,
        DateTime endTime,
        HashSet<string> seenFiles,
        List<CrashEvent> result)
    {
        try
        {
            var dirInfo = new DirectoryInfo(werDir);
            if (!dirInfo.Exists) return;

            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                try
                {
                    // 1. 优先检查是否存在转储文件 (*.dmp, *.mdmp)
                    var dumpFiles = subDir.EnumerateFiles("*.dmp")
                        .Concat(subDir.EnumerateFiles("*.mdmp"))
                        .ToList();

                    bool dumpParsed = false;
                    foreach (var dmp in dumpFiles)
                    {
                        if (seenFiles.Add(dmp.FullName.ToLowerInvariant()))
                        {
                            var ev = ParseFile(dmp.FullName);
                            if (ev != null && (ev.Time >= startTime && ev.Time <= endTime || dmp.LastWriteTime >= startTime && dmp.LastWriteTime <= endTime))
                            {
                                result.Add(ev);
                                dumpParsed = true;
                            }
                        }
                    }

                    // 2. 若无 dmp 文件或 dmp 文件已被系统清理，解析 Report.wer 恢复故障元数据
                    if (!dumpParsed)
                    {
                        string werPath = Path.Combine(subDir.FullName, "Report.wer");
                        if (File.Exists(werPath) && seenFiles.Add(werPath.ToLowerInvariant()))
                        {
                            var fi = new FileInfo(werPath);
                            if (fi.LastWriteTime >= startTime && fi.LastWriteTime <= endTime)
                            {
                                var ev = ParseReportWer(werPath, fi);
                                if (ev != null && (ev.Time >= startTime && ev.Time <= endTime || fi.LastWriteTime >= startTime && fi.LastWriteTime <= endTime))
                                {
                                    result.Add(ev);
                                }
                            }
                        }
                    }
                }
                catch { /* 忽略单个子目录访问错误 */ }
            }
        }
        catch { /* 忽略顶层目录访问错误 */ }
    }

    /// <summary>
    /// 解析 Windows Error Reporting 生成的 Report.wer 文本报告
    /// </summary>
    internal static CrashEvent? ParseReportWer(string werFilePath, FileInfo fi)
    {
        try
        {
            string[] lines;
            using (var reader = new StreamReader(werFilePath, Encoding.Unicode, detectEncodingFromByteOrderMarks: true))
            {
                var content = reader.ReadToEnd();
                lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    dict[key] = val;
                }
            }

            string eventType = dict.GetValueOrDefault("EventType") ?? string.Empty;
            bool isKernel = eventType.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase)
                         || eventType.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)
                         || eventType.Contains("Kernel", StringComparison.OrdinalIgnoreCase)
                         || (dict.TryGetValue("Sig[0].Name", out var n0) && n0.Equals("BCCode", StringComparison.OrdinalIgnoreCase));

            // 专注于内核崩溃、硬件挂死与蓝屏事件
            if (!isKernel)
                return null;

            var ev = new CrashEvent
            {
                Type = eventType.Contains("LiveKernel", StringComparison.OrdinalIgnoreCase) ? CrashType.LiveKernel : CrashType.BSOD,
                Time = fi.LastWriteTime,
                DumpPath = werFilePath,
                DumpSize = fi.Length,
            };
            ev.AddDumpPath(werFilePath);

            if (dict.TryGetValue("EventTime", out var eventTimeStr) && ulong.TryParse(eventTimeStr, out ulong ft))
            {
                try
                {
                    var dt = DateTime.FromFileTimeUtc((long)ft).ToLocalTime();
                    if (dt.Year >= 2000 && dt <= DateTime.Now.AddDays(1))
                        ev.Time = dt;
                }
                catch { }
            }

            // 内核蓝屏/实时转储字段解析
            // Sig[0] 通常为 BCCode，Sig[1..4] 为 BCP1..BCP4
            uint bcCode = 0;
            if (dict.TryGetValue("Sig[0].Value", out var bcStr))
            {
                if (bcStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    uint.TryParse(bcStr.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bcCode);
                else if (uint.TryParse(bcStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
                    bcCode = hexVal;
                else
                    uint.TryParse(bcStr, out bcCode);
            }

            ulong[] bcp = new ulong[4];
            for (int i = 1; i <= 4; i++)
            {
                if (dict.TryGetValue($"Sig[{i}].Value", out var paramStr))
                {
                    if (paramStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        ulong.TryParse(paramStr.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bcp[i - 1]);
                    else
                        ulong.TryParse(paramStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bcp[i - 1]);
                }
            }

            ev.BugCheckCode = bcCode;
            ev.BugCheckParams = bcp;

            var info = BugCheckKnowledge.Get(bcCode);
            ev.StopCode = info.Name;
            ev.StopCodeChinese = info.Chinese;
            ev.Troubleshooting.AddRange(info.Suggestions);

            if (dict.TryGetValue("OriginalFilename", out var origFile) && !string.IsNullOrWhiteSpace(origFile))
                ev.SuspectDriver = origFile;
            else if (dict.TryGetValue("TargetAppId", out var targetApp) && !string.IsNullOrWhiteSpace(targetApp))
                ev.SuspectDriver = Path.GetFileName(targetApp);

            if (!string.IsNullOrWhiteSpace(ev.SuspectDriver))
            {
                string desc = DriverKnowledge.Describe(ev.SuspectDriver);
                if (!string.IsNullOrWhiteSpace(desc)) ev.SuspectDriver += $"  [{desc}]";
                string vendor = DriverKnowledge.GuessGpuVendor(ev.SuspectDriver);
                if (vendor != "未知") ev.GpuVendor = vendor;
            }

            ProblematicDriverKnowledge.EnrichEvent(ev);
            ev.Summary = $"[WER 归档] " + BuildSummary(ev);

            return ev;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析单个 dump 文件；失败时返回 null
    /// </summary>
    public static CrashEvent? ParseFile(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            if (filePath.EndsWith(".wer", StringComparison.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(filePath);
                return ParseReportWer(filePath, fi);
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            if (fs.Length < 0x20) return null;

            uint sig32 = br.ReadUInt32();
            fs.Position = 0;

            if (sig32 == MDMP_SIGNATURE)
                return ParseMinidump(br, filePath);

            // 判断是否为 64-bit kernel / triage dump (PAGEDU64)
            ulong sig64 = br.ReadUInt64();
            fs.Position = 0;
            if (sig64 == PAGE_DUMP_SIG_64)
                return ParseKernelDump64(br, filePath);

            // 32-bit kernel dump
            if (sig32 == PAGE_DUMP_SIG_32)
                return ParseKernelDump32(br, filePath);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static CrashEvent ParseMinidump(BinaryReader br, string filePath)
    {
        var fi = new FileInfo(filePath);
        var ev = new CrashEvent
        {
            Type = CrashType.BSOD,
            Time = fi.LastWriteTime,
            DumpPath = filePath,
            DumpSize = fi.Length,
        };
        ev.AddDumpPath(filePath);

        br.BaseStream.Position = 0x04;
        ushort version = br.ReadUInt16();
        ushort numStreams = br.ReadUInt16();
        uint streamDirRva = br.ReadUInt32();
        uint checksum = br.ReadUInt32();
        uint timeDateStamp = br.ReadUInt32();

        DateTime dumpTime = DateTimeOffset.FromUnixTimeSeconds(timeDateStamp).LocalDateTime;
        if (dumpTime > DateTime.MinValue.AddYears(1) && dumpTime < DateTime.Now.AddYears(1))
            ev.Time = dumpTime;

        BugCheckInfo bugCheck = new();
        List<ModuleInfo> modules = new();
        List<ModuleInfo> unloadedModules = new();
        uint exceptionCode = 0;
        ulong exceptionAddress = 0;

        long baseStreamLen = br.BaseStream.Length;

        for (int i = 0; i < numStreams; i++)
        {
            long pos = streamDirRva + (long)i * 12;
            if (pos + 12 > baseStreamLen) break;

            br.BaseStream.Position = pos;
            uint streamType = br.ReadUInt32();
            uint dataSize = br.ReadUInt32();
            uint rva = br.ReadUInt32();

            try
            {
                if (streamType == STREAM_BUGCHECK || streamType == STREAM_BUGCHECK_INFO)
                {
                    bugCheck = ReadBugCheckInfo(br, rva, dataSize);
                }
                else if (streamType == STREAM_MODULE_LIST)
                {
                    modules = ReadModuleList(br, rva, dataSize);
                }
                else if (streamType == STREAM_UNLOADED_MODULE_LIST)
                {
                    unloadedModules = ReadUnloadedModuleList(br, rva, dataSize);
                }
                else if (streamType == STREAM_EXCEPTION)
                {
                    (exceptionCode, exceptionAddress) = ReadExceptionStream(br, rva, dataSize);
                }
            }
            catch { /* ignore bad stream */ }
        }

        if (unloadedModules.Count > 0)
        {
            ev.UnloadedDrivers = unloadedModules
                .Select(m => Path.GetFileName(m.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        ev.BugCheckCode = bugCheck.Code;
        ev.BugCheckParams = bugCheck.Params.Length == 4 ? bugCheck.Params : new ulong[4];
        ev.ExceptionCode = exceptionCode;
        ev.ExceptionAddress = exceptionAddress;

        var info = BugCheckKnowledge.Get(bugCheck.Code);
        ev.StopCode = info.Name;
        ev.StopCodeChinese = info.Chinese;
        ev.Troubleshooting.AddRange(info.Suggestions);

        var candidates = new List<ulong>();
        if (exceptionAddress != 0) candidates.Add(exceptionAddress);
        candidates.AddRange(ev.BugCheckParams);

        ResolveSuspectAndOffset(ev, modules, unloadedModules, bugCheck.DriverName, candidates, Array.Empty<ulong>());

        ProblematicDriverKnowledge.EnrichEvent(ev);

        ev.Summary = BuildSummary(ev);
        return ev;
    }

    private static CrashEvent ParseKernelDump64(BinaryReader br, string filePath)
    {
        var fi = new FileInfo(filePath);
        var ev = new CrashEvent
        {
            Type = CrashType.BSOD,
            Time = fi.LastWriteTime,
            DumpPath = filePath,
            DumpSize = fi.Length,
        };
        ev.AddDumpPath(filePath);

        try
        {
            // _DUMP_HEADER64 标准布局：
            // 0x00: Signature ("PAGE") + ValidDump ("DU64") [8B]
            // 0x08: MajorVersion [4B], 0x0C: MinorVersion [4B]
            // 0x10: DirectoryTableBase [8B], 0x18: PfnDataBase [8B]
            // 0x20: PsLoadedModuleList [8B], 0x28: PsActiveProcessHead [8B]
            // 0x30: MachineImageType [4B], 0x34: NumberOfProcessors [4B]
            // 0x38: BugCheckCode [4B], 0x3C: 4B 对齐填充 ("PAGE" = 0x45474150)
            // 0x40..0x5F: BugCheckParameter1..4 [4 x 8B]
            br.BaseStream.Position = 0x38;
            uint codeAt38 = br.ReadUInt32();
            uint padAt3C = br.ReadUInt32();
            ulong p1 = br.ReadUInt64();
            ulong p2 = br.ReadUInt64();
            ulong p3 = br.ReadUInt64();
            ulong p4 = br.ReadUInt64();

            uint code = codeAt38;
            // 兼容非标准紧凑头（若 0x38 为 0 且 0x3C 不是 PAGE 填充，则尝试从 0x40 读取）
            if (code == 0 && padAt3C != PAGE_DUMP_SIG_32 && br.BaseStream.Length >= 0x68)
            {
                br.BaseStream.Position = 0x40;
                uint altCode = br.ReadUInt32();
                if (altCode != 0 && altCode <= 0x10000FFF)
                {
                    code = altCode;
                    p1 = br.ReadUInt64();
                    p2 = br.ReadUInt64();
                    p3 = br.ReadUInt64();
                    p4 = br.ReadUInt64();
                }
            }

            ev.BugCheckCode = code;
            ev.BugCheckParams = [p1, p2, p3, p4];

            var info = BugCheckKnowledge.Get(code);
            ev.StopCode = info.Name;
            ev.StopCodeChinese = info.Chinese;
            ev.Troubleshooting.AddRange(info.Suggestions);

            // 解析 _TRIAGE_DUMP64（Windows 64 位小内存转储 Minidump 与 LiveKernelReport 均采用此结构）
            var (modules, exCode, exAddr, ripAddr, stackAddrs) = ReadTriageDump64(br);
            if (exCode != 0) ev.ExceptionCode = exCode;
            if (exAddr != 0) ev.ExceptionAddress = exAddr;
            else if (ripAddr != 0) ev.ExceptionAddress = ripAddr;

            if (modules.Count > 0)
            {
                var candidates = new List<ulong>();
                if (exAddr != 0) candidates.Add(exAddr);
                if (ripAddr != 0 && ripAddr != exAddr) candidates.Add(ripAddr);
                candidates.AddRange(ev.BugCheckParams);

                ResolveSuspectAndOffset(ev, modules, string.Empty, candidates, stackAddrs);
            }
        }
        catch
        {
            ev.StopCode = "未解析";
            ev.StopCodeChinese = "内核/完整内存 dump：请使用 WinDbg Preview 打开执行 !analyze -v";
            ev.Troubleshooting.Add("该 dump 为内核内存转储，请用 WinDbg Preview 打开并执行 !analyze -v 深入分析");
        }

        ev.Summary = BuildSummary(ev);
        return ev;
    }

    /// <summary>
    /// 从 PAGEDU64 的 _TRIAGE_DUMP64 区域提取驱动模块表、异常记录、线程上下文 RIP 与内核调用栈地址
    /// </summary>
    private static (List<ModuleInfo> modules, uint exCode, ulong exAddr, ulong rip, List<ulong> stackAddrs)
        ReadTriageDump64(BinaryReader br)
    {
        var modules = new List<ModuleInfo>();
        var stackAddrs = new List<ulong>();
        uint exCode = 0;
        ulong exAddr = 0;
        ulong rip = 0;

        long streamLen = br.BaseStream.Length;
        // _TRIAGE_DUMP64 通常位于偏移 0x2000（部分早期版本位于 0x1000）
        foreach (long headerBase in new[] { 0x2000L, 0x1000L })
        {
            if (headerBase + 0x40 > streamLen) continue;

            br.BaseStream.Position = headerBase + 0x0C;
            uint contextOffset = br.ReadUInt32();
            uint exceptionOffset = br.ReadUInt32();
            br.BaseStream.Position = headerBase + 0x28;
            uint callStackOffset = br.ReadUInt32();
            uint sizeOfCallStack = br.ReadUInt32();
            uint driverListOffset = br.ReadUInt32();
            uint driverCount = br.ReadUInt32();
            uint stringPoolOffset = br.ReadUInt32();

            if (driverCount == 0 || driverCount > 4096) continue;
            if (driverListOffset < 0x1000 || driverListOffset + (long)driverCount * TRIAGE_DRIVER_ENTRY_SIZE > streamLen)
                continue;
            if (stringPoolOffset != 0 && (stringPoolOffset < driverListOffset || stringPoolOffset >= streamLen))
                continue;

            // 读取驱动模块列表 (_DUMP_DRIVER_ENTRY64, 每项 0x90 字节)
            for (int i = 0; i < driverCount; i++)
            {
                long entryPos = driverListOffset + (long)i * TRIAGE_DRIVER_ENTRY_SIZE;
                if (entryPos + TRIAGE_DRIVER_ENTRY_SIZE > streamLen) break;

                br.BaseStream.Position = entryPos;
                uint nameOffset = br.ReadUInt32();

                br.BaseStream.Position = entryPos + 0x38;
                ulong baseOfImage = br.ReadUInt64();

                br.BaseStream.Position = entryPos + 0x48;
                uint sizeOfImage = br.ReadUInt32();

                br.BaseStream.Position = entryPos + 0x88;
                uint timeDateStamp = br.ReadUInt32();

                string name = ReadTriageCountedString(br, nameOffset, streamLen);
                if (!string.IsNullOrWhiteSpace(name) && baseOfImage != 0)
                {
                    modules.Add(new ModuleInfo(baseOfImage, sizeOfImage, timeDateStamp, name));
                }
            }

            if (modules.Count == 0) continue;

            // 读取 _EXCEPTION_RECORD64 (ExceptionCode at +0x00, ExceptionAddress at +0x10)
            if (exceptionOffset >= 0x200 && exceptionOffset + 0x20 <= streamLen)
            {
                try
                {
                    br.BaseStream.Position = exceptionOffset;
                    exCode = br.ReadUInt32();
                    br.BaseStream.Position = exceptionOffset + 0x10;
                    ulong addr = br.ReadUInt64();
                    if (addr > 0xFFFF000000000000UL) exAddr = addr;
                }
                catch { }
            }

            // 读取 x64 _CONTEXT 的 RIP (+0xF8)
            if (contextOffset >= 0x200 && contextOffset + 0x100 <= streamLen)
            {
                try
                {
                    br.BaseStream.Position = contextOffset + 0xF8;
                    ulong candidateRip = br.ReadUInt64();
                    if (candidateRip > 0xFFFF000000000000UL) rip = candidateRip;
                }
                catch { }
            }

            // 读取调用栈区的内核返回地址
            if (callStackOffset >= 0x200 && sizeOfCallStack is > 0 and <= 0x10000 &&
                callStackOffset + sizeOfCallStack <= streamLen)
            {
                try
                {
                    br.BaseStream.Position = callStackOffset;
                    int count = (int)(sizeOfCallStack / 8);
                    for (int s = 0; s < count; s++)
                    {
                        ulong ptr = br.ReadUInt64();
                        if (ptr > 0xFFFF800000000000UL)
                            stackAddrs.Add(ptr);
                    }
                }
                catch { }
            }

            break;
        }

        return (modules, exCode, exAddr, rip, stackAddrs);
    }

    private static string ReadTriageCountedString(BinaryReader br, uint offset, long streamLen)
    {
        if (offset == 0 || offset + 4 > streamLen) return string.Empty;
        try
        {
            br.BaseStream.Position = offset;
            uint byteLen = br.ReadUInt32();
            if (byteLen == 0 || byteLen > 1024) return string.Empty;
            int byteCount = (int)byteLen;
            if (offset + 4 + byteCount > streamLen) return string.Empty;
            byte[] bytes = br.ReadBytes(byteCount);
            string s = Encoding.Unicode.GetString(bytes);
            int nul = s.IndexOf('\0');
            return (nul >= 0 ? s.Substring(0, nul) : s).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ResolveSuspectAndOffset(
        CrashEvent ev,
        List<ModuleInfo> modules,
        string explicitDriver,
        IReadOnlyList<ulong> primaryAddresses,
        IReadOnlyList<ulong> stackAddresses)
        => ResolveSuspectAndOffset(ev, modules, null, explicitDriver, primaryAddresses, stackAddresses);

    private static void ResolveSuspectAndOffset(
        CrashEvent ev,
        List<ModuleInfo> modules,
        List<ModuleInfo>? unloadedModules,
        string explicitDriver,
        IReadOnlyList<ulong> primaryAddresses,
        IReadOnlyList<ulong> stackAddresses)
    {
        string suspect = explicitDriver ?? string.Empty;
        string thirdPartyOffset = string.Empty;
        string subsystemOffset = string.Empty;
        string subsystemDriver = string.Empty;
        string kernelOffset = string.Empty;

        // 1. 先检查异常地址、RIP 与 BugCheck 4 个参数
        foreach (ulong addr in primaryAddresses)
        {
            if (addr == 0) continue;
            var m = FindModuleByAddress(modules, addr);
            if (m != null)
            {
                string fileName = Path.GetFileName(m.Name);
                string formattedOffset = $"{fileName}+0x{addr - m.BaseAddr:X}";

                if (!IsCoreKernelModule(fileName) && !IsSubsystemWrapperModule(fileName))
                {
                    if (string.IsNullOrEmpty(thirdPartyOffset))
                        thirdPartyOffset = formattedOffset;
                    if (string.IsNullOrWhiteSpace(suspect))
                        suspect = fileName;
                    break;
                }
                else if (IsSubsystemWrapperModule(fileName))
                {
                    if (string.IsNullOrEmpty(subsystemOffset))
                    {
                        subsystemOffset = formattedOffset;
                        subsystemDriver = fileName;
                    }
                }
                else if (string.IsNullOrEmpty(kernelOffset))
                {
                    kernelOffset = formattedOffset;
                }
            }
            else if (unloadedModules != null)
            {
                var um = FindModuleByAddress(unloadedModules, addr);
                if (um != null)
                {
                    string fileName = Path.GetFileName(um.Name);
                    string formattedOffset = $"{fileName}+0x{addr - um.BaseAddr:X} [已卸载]";
                    if (string.IsNullOrEmpty(thirdPartyOffset))
                        thirdPartyOffset = formattedOffset;
                    if (string.IsNullOrWhiteSpace(suspect))
                        suspect = $"{fileName} [已卸载]";
                    break;
                }
            }
        }

        // 2. 若主参数未命中第三方驱动，扫描调用栈寻找首个第三方驱动或图形/硬件子系统驱动
        if (string.IsNullOrWhiteSpace(suspect) && stackAddresses.Count > 0)
        {
            foreach (ulong addr in stackAddresses)
            {
                var m = FindModuleByAddress(modules, addr);
                if (m != null)
                {
                    string fileName = Path.GetFileName(m.Name);
                    string formattedOffset = $"{fileName}+0x{addr - m.BaseAddr:X}";

                    if (!IsCoreKernelModule(fileName) && !IsSubsystemWrapperModule(fileName))
                    {
                        suspect = fileName;
                        if (string.IsNullOrEmpty(thirdPartyOffset))
                            thirdPartyOffset = formattedOffset;
                        break;
                    }
                    if (IsSubsystemWrapperModule(fileName) && string.IsNullOrEmpty(subsystemOffset))
                    {
                        subsystemOffset = formattedOffset;
                        subsystemDriver = fileName;
                    }
                    else if (string.IsNullOrEmpty(kernelOffset))
                    {
                        kernelOffset = formattedOffset;
                    }
                }
                else if (unloadedModules != null)
                {
                    var um = FindModuleByAddress(unloadedModules, addr);
                    if (um != null)
                    {
                        string fileName = Path.GetFileName(um.Name);
                        suspect = $"{fileName} [已卸载]";
                        if (string.IsNullOrEmpty(thirdPartyOffset))
                            thirdPartyOffset = $"{fileName}+0x{addr - um.BaseAddr:X} [已卸载]";
                        break;
                    }
                }
            }
        }

        // 3. 确定最终的崩溃指令偏移 (FaultingModuleOffset)
        ev.FaultingModuleOffset = !string.IsNullOrEmpty(thirdPartyOffset) ? thirdPartyOffset
            : !string.IsNullOrEmpty(subsystemOffset) ? subsystemOffset
            : kernelOffset;

        // 4. 确定嫌疑驱动
        if (string.IsNullOrWhiteSpace(suspect))
        {
            if (!string.IsNullOrEmpty(subsystemDriver))
            {
                suspect = subsystemDriver;
            }
            else if (modules.Count > 0)
            {
                // 兜底：选 TimeDateStamp 最新（编译时间最近）的非内核核心模块
                suspect = modules
                    .Where(m => !string.IsNullOrWhiteSpace(m.Name) && !IsCoreKernelModule(Path.GetFileName(m.Name)))
                    .OrderByDescending(m => m.TimeDateStamp)
                    .FirstOrDefault()?.Name ?? string.Empty;
            }
        }

        ev.SuspectDriver = suspect ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(ev.SuspectDriver))
        {
            string cleanName = ev.SuspectDriver.Replace(" [已卸载]", "").Trim();
            string desc = DriverKnowledge.Describe(Path.GetFileName(cleanName));
            if (!string.IsNullOrWhiteSpace(desc))
                ev.SuspectDriver += $"  [{desc}]";

            string vendor = DriverKnowledge.GuessGpuVendor(Path.GetFileName(cleanName));
            if (vendor != "未知")
                ev.GpuVendor = vendor;
        }
    }

    private static ModuleInfo? FindModuleByAddress(List<ModuleInfo> modules, ulong addr)
    {
        foreach (var m in modules)
        {
            if (m.Size > 0 && addr >= m.BaseAddr && addr < m.BaseAddr + m.Size)
                return m;
        }
        return null;
    }

    private static bool IsCoreKernelModule(string fileName)
    {
        return fileName.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ntkrnlmp.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ntkrnlpa.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("hal.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("kdcom.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("pshed.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("werkernel.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ntosext.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("watchdog.sys", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubsystemWrapperModule(string fileName)
    {
        return fileName.Equals("dxgkrnl.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dxgmms2.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dxgmms1.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("win32k.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("win32kbase.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("win32kfull.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Wdf01000.sys", StringComparison.OrdinalIgnoreCase);
    }

    private static CrashEvent ParseKernelDump32(BinaryReader br, string filePath)
    {
        var fi = new FileInfo(filePath);
        var ev = new CrashEvent
        {
            Type = CrashType.BSOD,
            Time = fi.LastWriteTime,
            DumpPath = filePath,
            DumpSize = fi.Length,
        };
        ev.AddDumpPath(filePath);

        try
        {
            br.BaseStream.Position = 0x20;
            uint code = br.ReadUInt32();
            uint p1 = br.ReadUInt32();
            uint p2 = br.ReadUInt32();
            uint p3 = br.ReadUInt32();
            uint p4 = br.ReadUInt32();

            ev.BugCheckCode = code;
            ev.BugCheckParams = [p1, p2, p3, p4];

            var info = BugCheckKnowledge.Get(code);
            ev.StopCode = info.Name;
            ev.StopCodeChinese = info.Chinese;
            ev.Troubleshooting.AddRange(info.Suggestions);
        }
        catch
        {
            ev.StopCode = "未解析";
            ev.StopCodeChinese = "32-bit 内核 dump，请使用 WinDbg 打开分析";
            ev.Troubleshooting.Add("请使用 WinDbg 打开分析该 32 位 dump");
        }

        ev.Summary = BuildSummary(ev);
        return ev;
    }

    private static (uint code, ulong address) ReadExceptionStream(BinaryReader br, uint rva, uint dataSize)
    {
        // MINIDUMP_EXCEPTION_STREAM:
        // ThreadId (4B) + __alignment (4B) + MINIDUMP_EXCEPTION (ExceptionCode 4B at +8, ExceptionAddress 8B at +24)
        if (rva == 0 || dataSize < 32 || rva + 32 > (ulong)br.BaseStream.Length)
            return (0, 0);

        br.BaseStream.Position = rva + 8;
        uint code = br.ReadUInt32();
        br.BaseStream.Position = rva + 24;
        ulong address = br.ReadUInt64();
        return (code, address);
    }

    private sealed class BugCheckInfo
    {
        public uint Code;
        public ulong[] Params = Array.Empty<ulong>();
        public string DriverName = string.Empty;
        public BugCheckInfo() { }
        public BugCheckInfo(uint code, ulong[] ps, string driver)
        {
            Code = code; Params = ps ?? Array.Empty<ulong>(); DriverName = driver ?? string.Empty;
        }
    }

    private static BugCheckInfo ReadBugCheckInfo(BinaryReader br, uint rva, uint size)
    {
        if (rva >= (ulong)br.BaseStream.Length) return new BugCheckInfo();
        br.BaseStream.Position = rva;
        long available = br.BaseStream.Length - rva;

        uint code = 0;
        ulong p1 = 0, p2 = 0, p3 = 0, p4 = 0;
        if (available >= 4) code = br.ReadUInt32();
        if (available >= 12) p1 = br.ReadUInt64();
        if (available >= 20) p2 = br.ReadUInt64();
        if (available >= 28) p3 = br.ReadUInt64();
        if (available >= 36) p4 = br.ReadUInt64();

        string driverName = string.Empty;
        try
        {
            if (size >= 4 + 32 + 8 && available >= 4 + 32 + 8)
            {
                uint driverRva = br.ReadUInt32();
                uint reasonRva = br.ReadUInt32();
                if (driverRva != 0 && driverRva < (ulong)br.BaseStream.Length)
                {
                    driverName = ReadMinidumpString(br, driverRva);
                }
            }
        }
        catch { /* ignore */ }

        return new BugCheckInfo(code, [p1, p2, p3, p4], driverName);
    }

    private static string ReadMinidumpString(BinaryReader br, uint rva)
    {
        try
        {
            br.BaseStream.Position = rva;
            uint lengthBytes = br.ReadUInt32();
            if (lengthBytes == 0 || lengthBytes > 0x400) return string.Empty;
            byte[] bytes = br.ReadBytes((int)lengthBytes);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class ModuleInfo
    {
        public ulong BaseAddr;
        public uint Size;
        public uint TimeDateStamp;
        public string Name = string.Empty;
        public ModuleInfo() { }
        public ModuleInfo(ulong ba, uint sz, uint ts, string n)
        {
            BaseAddr = ba; Size = sz; TimeDateStamp = ts; Name = n ?? string.Empty;
        }
    }

    private static List<ModuleInfo> ReadModuleList(BinaryReader br, uint rva, uint dataSize)
    {
        var list = new List<ModuleInfo>();
        try
        {
            br.BaseStream.Position = rva;
            uint count = br.ReadUInt32();
            if (count > 4096) count = 4096;

            const int MODULE_SIZE = 108;

            for (int i = 0; i < count; i++)
            {
                long entryPos = rva + 4 + (long)i * MODULE_SIZE;
                if (entryPos + MODULE_SIZE > br.BaseStream.Length) break;
                br.BaseStream.Position = entryPos;
                ulong baseAddr = br.ReadUInt64();
                uint size = br.ReadUInt32();
                uint checksum = br.ReadUInt32();
                uint ts = br.ReadUInt32();
                uint nameRva = br.ReadUInt32();
                string name = string.Empty;
                if (nameRva != 0 && nameRva < (ulong)br.BaseStream.Length)
                    name = ReadMinidumpString(br, nameRva);

                list.Add(new ModuleInfo(baseAddr, size, ts, name));
            }
        }
        catch { /* ignore */ }
        return list;
    }

    private static List<ModuleInfo> ReadUnloadedModuleList(BinaryReader br, uint rva, uint dataSize)
    {
        var list = new List<ModuleInfo>();
        try
        {
            if (rva + 12 > br.BaseStream.Length) return list;
            br.BaseStream.Position = rva;
            uint sizeOfHeader = br.ReadUInt32();
            uint sizeOfEntry = br.ReadUInt32();
            uint count = br.ReadUInt32();

            if (sizeOfHeader < 12 || sizeOfEntry < 24 || count == 0 || count > 4096)
                return list;

            for (int i = 0; i < count; i++)
            {
                long entryPos = rva + sizeOfHeader + (long)i * sizeOfEntry;
                if (entryPos + sizeOfEntry > br.BaseStream.Length) break;
                br.BaseStream.Position = entryPos;
                ulong baseAddr = br.ReadUInt64();
                uint size = br.ReadUInt32();
                uint checksum = br.ReadUInt32();
                uint ts = br.ReadUInt32();
                uint nameRva = br.ReadUInt32();
                string name = string.Empty;
                if (nameRva != 0 && nameRva < (ulong)br.BaseStream.Length)
                    name = ReadMinidumpString(br, nameRva);

                list.Add(new ModuleInfo(baseAddr, size, ts, name));
            }
        }
        catch { /* ignore */ }
        return list;
    }

    private static string BuildSummary(CrashEvent ev)
    {
        string sizeStr = ev.DumpSize > 0 ? $" ({CrashEvent.FormatFileSize(ev.DumpSize)})" : string.Empty;
        if (ev.BugCheckCode == 0)
            return $"dump 文件{sizeStr}，停止代码未解析";

        string prefix = $"0x{ev.BugCheckCode:X}";
        string codeName = ev.StopCode == "UNKNOWN" ? prefix : $"{prefix} {ev.StopCode}";
        string target = !string.IsNullOrWhiteSpace(ev.FaultingModuleOffset)
            ? $"，位置: {ev.FaultingModuleOffset}"
            : !string.IsNullOrWhiteSpace(ev.SuspectDriver)
                ? $"，嫌疑: {ExtractDriverBase(ev.SuspectDriver)}"
                : string.Empty;
        return $"{codeName}{target}{sizeStr}";
    }

    private static string ExtractDriverBase(string s)
    {
        int idx = s.IndexOf('[');
        return (idx > 0 ? s.Substring(0, idx) : s).Trim();
    }
}
