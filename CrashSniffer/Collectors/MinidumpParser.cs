using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CrashSniffer.Models;

namespace CrashSniffer.Collectors;

/// <summary>
/// 解析 Windows Minidump (*.dmp) 文件，提取 BSOD 停止代码、参数、嫌疑驱动
/// 使用内置解析，不依赖 WinDbg
/// </summary>
public static class MinidumpParser
{
    private const uint MDMP_SIGNATURE = 0x504D444D; // "MDMP"
    private const uint PAGE_DUMP_SIG_32 = 0x45474150; // "PAGE" (little-endian, for 32-bit)
    private const ulong PAGE_DUMP_SIG_64 = 0x3436554445474150; // "PAGEDU64" little-endian

    // Minidump Stream Types (MINIDUMP_STREAM_TYPE)
    private const uint STREAM_MODULE_LIST = 0x04;     // ModuleListStream
    private const uint STREAM_BUGCHECK = 0x11;        // BugCheckInfoStream (旧版)
    private const uint STREAM_BUGCHECK_INFO = 0x15;   // BugCheckInfoStream (新版 0x15)
    private const uint STREAM_THREAD_LIST = 0x03;
    private const uint STREAM_SYSTEM_INFO = 0x07;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct MinidumpHeader
    {
        public readonly uint Signature;       // 0x00
        public readonly ushort Version;       // 0x04
        public readonly ushort NumberOfStreams; // 0x06
        public readonly uint StreamDirRva;    // 0x08
        public readonly uint CheckSum;        // 0x0C
        public readonly uint TimeDateStamp;   // 0x10
        public readonly ulong Flags;          // 0x14 (Flags ULONG64 从 0x18开始? 不对，实际是从 TimeDateStamp 后 4 字节 + 4字节padding后8字节? 需实测)
        // 为避免 struct 字段对齐问题，直接用 reader 手动读
    }

    /// <summary>
    /// 收集指定时间范围内的 minidump 文件并解析
    /// </summary>
    public static List<CrashEvent> Collect(DateTime startTime, DateTime endTime)
    {
        var result = new List<CrashEvent>();
        var scanPaths = new List<string>();

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 标准 minidump 目录
        string miniDir = Path.Combine(winDir, "Minidump");
        if (Directory.Exists(miniDir))
            scanPaths.Add(miniDir);

        // MEMORY.DMP (系统盘根目录下的 WinDbg 内核 dump)
        string memDump = Path.Combine(winDir, "MEMORY.DMP");
        if (File.Exists(memDump))
            scanPaths.Add(memDump);

        foreach (var path in scanPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    ProcessDumpFile(path, startTime, endTime, result);
                }
                else if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*.dmp", SearchOption.TopDirectoryOnly))
                    {
                        ProcessDumpFile(file, startTime, endTime, result);
                    }
                }
            }
            catch (Exception ex)
            {
                // 单个 dump 失败不影响整体
                System.Diagnostics.Debug.WriteLine($"扫描 {path} 失败: {ex.Message}");
            }
        }

        return result;
    }

    private static void ProcessDumpFile(string filePath, DateTime startTime, DateTime endTime, List<CrashEvent> result)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return;

            // 按文件最后修改时间过滤
            if (fi.LastWriteTime < startTime || fi.LastWriteTime > endTime)
                return;

            var ev = ParseFile(filePath);
            if (ev != null)
                result.Add(ev);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"解析 {filePath} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析单个 dump 文件；失败时返回 null
    /// </summary>
    public static CrashEvent? ParseFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            if (fs.Length < 0x20) return null;

            uint sig32 = br.ReadUInt32();
            fs.Position = 0;

            if (sig32 == MDMP_SIGNATURE)
                return ParseMinidump(br, filePath);

            // 判断是否为 64-bit kernel dump (PAGEDU64)
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

        // 手动解析 Minidump Header
        // Offset 0x00: Signature uint -> already read ok
        br.BaseStream.Position = 0x04;
        ushort version = br.ReadUInt16();
        ushort numStreams = br.ReadUInt16();
        uint streamDirRva = br.ReadUInt32();
        uint checksum = br.ReadUInt32();
        uint timeDateStamp = br.ReadUInt32();

        // 如果文件时间戳和 dump 内部时间差异很大，优先用内部时间
        DateTime dumpTime = DateTimeOffset.FromUnixTimeSeconds(timeDateStamp).LocalDateTime;
        if (dumpTime > DateTime.MinValue.AddYears(1) && dumpTime < DateTime.Now.AddYears(1))
            ev.Time = dumpTime;

        // 遍历所有 stream，找 BugCheckInfo 和 ModuleList
        BugCheckInfo bugCheck = new BugCheckInfo();
        List<ModuleInfo> modules = new();

        long baseStreamLen = br.BaseStream.Length;

        for (int i = 0; i < numStreams; i++)
        {
            long pos = streamDirRva + i * 12; // 每个目录 12 字节
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
            }
            catch { /* ignore bad stream */ }
        }

        // 填充 BugCheck
        ev.BugCheckCode = bugCheck.Code;
        ev.BugCheckParams = bugCheck.Params;
        var info = BugCheckKnowledge.Get(bugCheck.Code);
        ev.StopCode = info.Name;
        ev.StopCodeChinese = info.Chinese;
        ev.Troubleshooting.AddRange(info.Suggestions);

        // 匹配嫌疑驱动：从 BugCheck 自带的关联驱动优先；否则用模块表里最后加载/最大的驱动
        string suspect = bugCheck.DriverName;
        if (string.IsNullOrWhiteSpace(suspect) && modules.Count > 0)
        {
            // 选最后 TimeDateStamp 最接近崩溃时间的 (TimeDateStamp 越大 = 越新)，作为"最后一个加载的有名字的驱动"
            suspect = modules
                .OrderByDescending(m => m.TimeDateStamp)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Name) &&
                                     !m.Name.Equals("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase) &&
                                     !m.Name.Equals("ntkrnlmp.exe", StringComparison.OrdinalIgnoreCase))
                ?.Name ?? string.Empty;
        }

        // 再次兜底：BugCheck param 里常常包含驱动地址，简化处理用 param1/2/3 搜索匹配模块基址
        if (string.IsNullOrWhiteSpace(suspect))
        {
            foreach (var p in bugCheck.Params)
            {
                if (p == 0) continue;
                var matched = modules.FirstOrDefault(m => p >= m.BaseAddr && p < m.BaseAddr + (ulong)m.Size);
                if (matched != null && !string.IsNullOrWhiteSpace(matched.Name))
                {
                    suspect = matched.Name;
                    break;
                }
            }
        }

        ev.SuspectDriver = suspect ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(ev.SuspectDriver))
        {
            string desc = DriverKnowledge.Describe(ev.SuspectDriver);
            if (!string.IsNullOrWhiteSpace(desc))
                ev.SuspectDriver += $"  [{desc}]";

            string vendor = DriverKnowledge.GuessGpuVendor(ev.SuspectDriver);
            if (vendor != "未知")
                ev.GpuVendor = vendor;
        }

        // 列表摘要
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

        try
        {
            // _DUMP_HEADER64: 偏移 Signature 8B, ValidDump 4B (optional), Major/Minor 各 4B, 
            // DirectoryTableBase 8B, PfnDataBase 8B, PsLoadedModuleList 8B, PsActiveProcessHead 8B,
            // MachineImageType 4B, NumberOfProcessors 4B, BugCheckCode 4B, 4x8B BugCheckParams...
            // 实际上 Microsoft 公开的 _DUMP_HEADER64 里 BugCheckCode 是 0x38 附近。实测 0x40。
            // 用保守方式：搜索 0x500-0x1000 区间查找已知 pattern，或者直接读标准偏移。
            br.BaseStream.Position = 0x40;
            uint code = br.ReadUInt32();
            ulong p1 = br.ReadUInt64();
            ulong p2 = br.ReadUInt64();
            ulong p3 = br.ReadUInt64();
            ulong p4 = br.ReadUInt64();

            // 粗略校验：如果 code=0 且全参数=0，则偏移不对，尝试 0x38
            if (code == 0 && p1 == 0 && p2 == 0 && p3 == 0 && p4 == 0)
            {
                br.BaseStream.Position = 0x38;
                code = br.ReadUInt32();
                p1 = br.ReadUInt64();
                p2 = br.ReadUInt64();
                p3 = br.ReadUInt64();
                p4 = br.ReadUInt64();
            }

            ev.BugCheckCode = code;
            ev.BugCheckParams = [p1, p2, p3, p4];

            var info = BugCheckKnowledge.Get(code);
            ev.StopCode = info.Name;
            ev.StopCodeChinese = info.Chinese;
            ev.Troubleshooting.AddRange(info.Suggestions);
        }
        catch
        {
            // Kernel dump 解析失败则留给用户用 WinDbg
            ev.StopCode = "未解析";
            ev.StopCodeChinese = "内核/完整内存 dump：请使用 WinDbg Preview 打开 MEMORY.DMP 执行 !analyze -v";
            ev.Troubleshooting.Add("该 dump 为完整内存 dump，内置解析仅提供元数据，请用 WinDbg Preview 打开并执行 !analyze -v 深入分析");
        }

        ev.Summary = BuildSummary(ev);
        return ev;
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

        try
        {
            // _DUMP_HEADER32: BugCheckCode offset 0x20
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
        // 边界校验：rva 越界时返回空结果，而不是让 EndOfStreamException
        // 在上游被吞掉导致整条 BugCheck 信息全部丢失
        if (rva >= (ulong)br.BaseStream.Length) return new BugCheckInfo();
        br.BaseStream.Position = rva;
        long available = br.BaseStream.Length - rva;

        // MINIDUMP_BUGCHECK_DATA: Code(4) + Param1..4(各 8) + DriverRva(4) + ReasonRva(4)
        // 字段级边界检查：数据不足时读到哪算哪，保留已读出的部分
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
            // 驱动名 Rva 区域：流内实际可读字节数也要够，否则跳过
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
            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
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
            if (count > 4096) count = 4096; // 防止异常值

            // MINIDUMP_MODULE:
            // ULONG64 BaseOfImage       8
            // ULONG32 SizeOfImage       4 (total 12)
            // ULONG32 CheckSum          4 (16)
            // ULONG32 TimeDateStamp     4 (20)
            // ULONG32 ModuleNameRva     4 (24)
            // VS_FIXEDFILEINFO: dwSignature..dwFileFlagsMask..共 13 ULONG32 = 52 bytes → offset 24+52 = 76 结束
            const int MODULE_SIZE = 108; // 实际 MINIDUMP_MODULE 各字段合计 108 字节 (可查 Windows DDK)
            // 保守用 sizeof-like: 实测官方 108

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

    private static string BuildSummary(CrashEvent ev)
    {
        string sizeStr = ev.DumpSize > 0 ? $" ({ev.DumpSize / 1024 / 1024} MB)" : string.Empty;
        if (ev.BugCheckCode == 0)
            return $"dump 文件{sizeStr}，停止代码未解析";

        string prefix = $"0x{ev.BugCheckCode:X}";
        string codeName = ev.StopCode == "UNKNOWN" ? prefix : $"{prefix} {ev.StopCode}";
        string driver = !string.IsNullOrWhiteSpace(ev.SuspectDriver)
            ? $"，嫌疑: {ExtractDriverBase(ev.SuspectDriver)}" : string.Empty;
        return $"{codeName}{driver}{sizeStr}";
    }

    private static string ExtractDriverBase(string s)
    {
        int idx = s.IndexOf('[');
        return (idx > 0 ? s.Substring(0, idx) : s).Trim();
    }
}
