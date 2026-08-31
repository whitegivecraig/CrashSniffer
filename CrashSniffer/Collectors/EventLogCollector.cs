using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Xml.Linq;
using CrashSniffer.Models;

namespace CrashSniffer.Collectors;

/// <summary>
/// 读取 Windows System 事件日志，采集关键崩溃事件
/// Kernel-Power 41 / BugCheck 1001 / EventLog 6008 / WHEA-Logger / Display 4101 TDR
/// </summary>
public static class EventLogCollector
{
    /// <summary>
    /// 收集指定时间范围内的事件，并返回转换后的 CrashEvent 列表
    /// </summary>
    public static List<CrashEvent> Collect(DateTime startTime, DateTime endTime)
    {
        var result = new List<CrashEvent>();
        try
        {
            using var session = new EventLogSession();
            string xpath = BuildXPath(startTime, endTime);
            var query = new EventLogQuery("System", PathType.LogName, xpath)
            {
                ReverseDirection = true,
                Session = session,
            };

            using var reader = new EventLogReader(query);
            EventRecord? record;
            while ((record = reader.ReadEvent()) != null)
            {
                try
                {
                    if (record.TimeCreated == null) continue;
                    DateTime t = record.TimeCreated.Value;
                    if (t < startTime || t > endTime) continue; // 双保险

                    var ev = ConvertEvent(record, t);
                    if (ev != null) result.Add(ev);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"事件转换失败: {ex.Message}");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 非管理员权限会在会话层面拒绝
            throw new InvalidOperationException("读取系统事件日志需要管理员权限，请以管理员身份运行本程序。");
        }
        catch (EventLogNotFoundException)
        {
            // System 日志不存在或不可访问，忽略返回空列表
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"事件日志读取失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 额外方法：扫描指定时间窗口内全部 System 日志 (用于"关联事件"附在崩溃事件旁)
    /// </summary>
    public static List<RelatedEvent> GetRelatedEvents(DateTime center, int minutesWindow = 5)
    {
        DateTime start = center.AddMinutes(-minutesWindow);
        DateTime end = center.AddMinutes(minutesWindow);
        var list = new List<RelatedEvent>();
        try
        {
            using var session = new EventLogSession();
            string xpath = BuildXPath(start, end, allProvider: true);
            var query = new EventLogQuery("System", PathType.LogName, xpath)
            {
                ReverseDirection = true,
                Session = session,
            };

            using var reader = new EventLogReader(query);
            EventRecord? rec;
            int count = 0;
            while ((rec = reader.ReadEvent()) != null && count < 200)
            {
                try
                {
                    if (rec.TimeCreated == null) continue;
                    var lv = rec.LevelDisplayName ?? "Information";
                    string msg = SafeGetMessage(rec);
                    list.Add(new RelatedEvent
                    {
                        Time = rec.TimeCreated.Value,
                        Provider = rec.ProviderName ?? "?",
                        EventId = rec.Id,
                        Level = lv,
                        Message = Truncate(msg, 800),
                        RawXml = Truncate(rec.ToXml(), 4000),
                    });
                    count++;
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return list.OrderBy(e => e.Time).ToList();
    }

    // —— 私有辅助 ——

    private static string BuildXPath(DateTime start, DateTime end, bool allProvider = false)
    {
        string startIso = ToXmlDateTime(start);
        string endIso = ToXmlDateTime(end);

        string timePred =
            $"TimeCreated[@SystemTime&gt;='{startIso}' and @SystemTime&lt;='{endIso}']";

        if (allProvider)
        {
            return $"<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[{timePred}]]</Select></Query></QueryList>";
        }

        // 目标 Event ID / Provider 组合
        var filters = new List<string>
        {
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {timePred}]]",
            $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and {timePred}]]",
            $"*[System[Provider[@Name='EventLog'] and EventID=6008 and {timePred}]]",
            $"*[System[Provider[@Name='WHEA-Logger'] and {timePred}]]",
            $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and {timePred}]]",
            $"*[System[Provider[@Name='Display'] and (EventID=4101 or EventID=4102 or EventID=4103 or EventID=4104 or EventID=4105) and {timePred}]]",
            $"*[System[Provider[@Name='Microsoft-Windows-Display'] and (EventID=4101 or EventID=4102 or EventID=4103) and {timePred}]]",
            $"*[System[Provider[@Name='nvlddmkm'] and {timePred}]]",
            $"*[System[Provider[@Name='amdkmdag'] and {timePred}]]",
        };

        string selects = string.Join("", filters.Select(f =>
            $"<Select Path=\"System\">{f}</Select>"));

        return $"<QueryList><Query Id=\"0\" Path=\"System\">{selects}</Query></QueryList>";
    }

    private static CrashEvent? ConvertEvent(EventRecord rec, DateTime t)
    {
        var provider = rec.ProviderName ?? string.Empty;
        int id = rec.Id;
        string xml = SafeGetXml(rec);
        string message = SafeGetMessage(rec);

        // BugCheck 1001：停止代码在事件参数里
        if (id == 1001 && provider.Contains("SystemErrorReporting"))
            return FromBugCheckEvent(t, xml, message, rec);

        // EventLog 6008："前一次系统关机在 XX 时间是意外的"
        if (id == 6008 && provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase))
        {
            // 6008 事件参数里有"关机发生的实际时间"，优先用这个
            DateTime actual = ParseShutdownEvent6008(t, xml) ?? t;
            return new CrashEvent
            {
                Time = actual,
                Type = CrashType.Shutdown_6008,
                Summary = $"系统意外关闭记录 (Event ID 6008)",
                EventMessage = message,
                Troubleshooting =
                {
                    "说明系统没走完正常关机流程：断电 / 蓝屏重启 / 长按电源 / 电源故障 / 内存不稳都有可能",
                    "如果没有 dump 也没有 WHEA 事件，优先排查电源、插座、UPS、主板供电",
                },
            };
        }

        // Kernel-Power 41：重启时记录，BugCheckCode 有时在 EventData 里
        if (id == 41 && provider.Contains("Kernel-Power"))
        {
            uint bugCheck = TryGetUintFromXml(xml, "BugcheckCode");
            string powerReason = TryGetStringFromXml(xml, "PowerButtonTimestamp") != "0"
                ? " (可能按下了电源键/睡眠键)" : string.Empty;

            var ev = new CrashEvent
            {
                Time = t,
                Type = CrashType.KernelPower_41,
                Summary = bugCheck > 0
                    ? $"异常重启 (Event 41)，BugCheckCode=0x{bugCheck:X}{powerReason}"
                    : $"异常重启 (Event 41){powerReason}",
                EventMessage = message,
                BugCheckCode = bugCheck,
            };
            if (bugCheck > 0)
            {
                var info = BugCheckKnowledge.Get(bugCheck);
                ev.StopCode = info.Name;
                ev.StopCodeChinese = info.Chinese;
                ev.Troubleshooting.AddRange(info.Suggestions);
            }
            else
            {
                ev.Troubleshooting.AddRange(new[]
                {
                    "Event 41 没有 BugCheckCode：系统直接断电或内存 dump 失败，蓝屏根本没来得及写",
                    "排查电源、主板、是否有断电历史；内存/CPU 不稳也常出现",
                    "检查 BIOS 设置：关闭所有超频 PBO/XMP，升级 BIOS 与电源管理驱动",
                });
            }
            return ev;
        }

        // WHEA-Logger (旧 Provider 名 WHEA-Logger，新名 Microsoft-Windows-WHEA-Logger)
        if (provider.Contains("WHEA-Logger", StringComparison.OrdinalIgnoreCase))
            return FromWheaEvent(t, id, xml, message, rec);

        // Display TDR (4101 最常见)
        if ((provider.Equals("Display", StringComparison.OrdinalIgnoreCase)
             || provider.Contains("Microsoft-Windows-Display"))
            && (id is 4101 or 4102 or 4103 or 4104 or 4105))
            return FromTdrEvent(t, id, xml, message, provider);

        // 显卡驱动直接发的事件 (nvlddmkm / amdkmdag)
        if (provider.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("amdkmdag", StringComparison.OrdinalIgnoreCase))
        {
            string vendor = provider.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" : "AMD";
            return new CrashEvent
            {
                Time = t,
                Type = CrashType.GPU_TDR,
                GpuVendor = vendor,
                SuspectDriver = provider + ".sys",
                Summary = $"{vendor} 显卡驱动事件 (Provider={provider}, ID={id})",
                EventMessage = message,
                Troubleshooting =
                {
                    "显卡驱动或硬件层事件，若同时出现 4101/4102 则是 GPU 超时系列",
                    "先 DDU 重装显卡驱动，再用 FurMark/3DMark Time Spy 烤机",
                    "检查显卡电源、温度、供电接口是否插紧",
                },
            };
        }

        return null;
    }

    private static CrashEvent FromBugCheckEvent(DateTime t, string xml, string message, EventRecord rec)
    {
        uint code = TryGetHexUintFromXml(xml, "param1") ?? TryGetUintFromXml(xml, "param1");
        var ev = new CrashEvent
        {
            Time = t,
            Type = CrashType.BSOD,
            EventMessage = message,
            BugCheckCode = code,
            BugCheckParams =
            [
                TryGetHexUlongFromXml(xml, "param2") ?? TryGetUlongFromXml(xml, "param2"),
                TryGetHexUlongFromXml(xml, "param3") ?? TryGetUlongFromXml(xml, "param3"),
                TryGetHexUlongFromXml(xml, "param4") ?? TryGetUlongFromXml(xml, "param4"),
                TryGetHexUlongFromXml(xml, "param5") ?? TryGetUlongFromXml(xml, "param5"),
            ],
        };
        var info = BugCheckKnowledge.Get(code);
        ev.StopCode = info.Name;
        ev.StopCodeChinese = info.Chinese;
        ev.Troubleshooting.AddRange(info.Suggestions);

        // 一般 param6/7 有 dump 文件名
        string dumpName = TryGetStringFromXml(xml, "param6") ?? TryGetStringFromXml(xml, "param7");
        if (!string.IsNullOrWhiteSpace(dumpName) && dumpName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            ev.DumpPath = Path.IsPathRooted(dumpName) ? dumpName :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), dumpName);
        }

        ev.Summary = $"蓝屏记录 (Event 1001) 0x{code:X} {info.Name}";
        return ev;
    }

    private static CrashEvent FromWheaEvent(DateTime t, int id, string xml, string message, EventRecord rec)
    {
        string errorType = TryGetStringFromXml(xml, "ErrorType") ?? TryGetStringFromXml(xml, "Type") ?? "未知";
        string? apicId = TryGetStringFromXml(xml, "ApicId") ?? TryGetStringFromXml(xml, "ProcessorApicId");
        string? procBank = TryGetStringFromXml(xml, "CacheLevel") ?? TryGetStringFromXml(xml, "ErrorSourceId");
        string? pcieRequester = TryGetStringFromXml(xml, "RequesterId") ?? TryGetStringFromXml(xml, "DeviceName");

        // WHEA 错误类型解码：ErrorType 值=0~10
        string? wheaTypeText = DecodeWheaType(TryGetUintFromXml(xml, "ErrorType"));

        var ev = new CrashEvent
        {
            Time = t,
            Type = CrashType.WHEA_Error,
            EventMessage = message,
            WheaErrorType = wheaTypeText ?? errorType,
            WheaLocation = BuildWheaLocation(apicId, procBank, pcieRequester, id),
            Summary = $"WHEA 硬件错误 (ID={id}, 类型={wheaTypeText ?? errorType})",
        };
        ev.Troubleshooting.AddRange(BuildWheaSuggestion(wheaTypeText, id));

        // 细化解析：填充出错部位结论
        WheaDetailParser.Enrich(ev, id, xml);
        if (!string.IsNullOrEmpty(ev.WheaHardwarePart))
            ev.Summary += $" → {ev.WheaHardwarePart}";

        if (id == 18)
            ev.Summary += " [严重：CPU Cache 协议错误]";
        else if (id == 19)
            ev.Summary += " [核心 Bus 内部奇偶校验]";
        else if (id == 46)
            ev.Summary += " [PCIe 设备错误]";
        else if (id == 47)
            ev.Summary += " [PCIe 根端口错误]";
        else if (id == 17)
            ev.Summary += " [CPU 核心 MCi 状态]";

        return ev;
    }

    private static CrashEvent FromTdrEvent(DateTime t, int id, string xml, string message, string provider)
    {
        string driverName = TryGetStringFromXml(xml, "DriverName") ?? TryGetStringFromXml(xml, "param1");
        // Event 4101 描述形如：N/A 显示驱动程序 amdkmdag.sys 版本 ... 已停止响应并已成功恢复。
        string detected = driverName;
        if (string.IsNullOrWhiteSpace(detected))
        {
            if (message.Contains("amdkmdag", StringComparison.OrdinalIgnoreCase)) detected = "amdkmdag.sys";
            else if (message.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)) detected = "nvlddmkm.sys";
            else if (message.Contains("igdkmd", StringComparison.OrdinalIgnoreCase)) detected = "igdkmdn64.sys";
            else detected = "显示驱动 (未知)";
        }

        string vendor = DriverKnowledge.GuessGpuVendor(detected);
        string idDesc = id switch
        {
            4101 => "TDR 超时恢复成功 (驱动停止响应后恢复)",
            4102 => "TDR 超时重置 (驱动未能恢复)",
            4103 => "TDR 超时 - 尝试重置 3 次仍失败",
            4104 => "TDR 超时 - 设备重置",
            4105 => "TDR 超时 - 显示适配器重置",
            _ => $"Display ID {id}",
        };

        var ev = new CrashEvent
        {
            Time = t,
            Type = CrashType.GPU_TDR,
            GpuVendor = vendor,
            SuspectDriver = detected,
            EventMessage = message,
            Summary = $"GPU TDR (Event {id}) {idDesc} → {detected}",
            Troubleshooting =
            {
                $"{vendor} GPU 超时重置：90% 是驱动或 GPU 不稳压 / 超频",
                "使用 DDU 完全卸载显卡驱动，然后安装官方正式版驱动 (不要 Beta)",
                "若有 GPU 超频/显存超频 → 先全部还原默认；降 50-100MHz 显存频率测试",
                "FurMark + 3DMark 连续 10 分钟循环，观察温度、供电是否异常",
                "若 PCIe 开了 4.0 偶尔不稳 → BIOS 中降至 3.0 测试",
            },
        };
        if (!string.IsNullOrWhiteSpace(detected))
        {
            string desc = DriverKnowledge.Describe(detected);
            if (!string.IsNullOrWhiteSpace(desc))
                ev.SuspectDriver = $"{detected}  [{desc}]";
        }
        return ev;
    }

    // —— WHEA 解码辅助 ——

    private static string? DecodeWheaType(uint? v)
    {
        return v switch
        {
            0 => "Cache Error (CPU 缓存)",
            1 => "TLB Error (CPU TLB)",
            2 => "Bus Error (总线)",
            3 => "MicroArchitectural Error (微架构内部)",
            4 => "Unknown",
            5 => "MS Check",
            6 => "Chipset Error",
            7 => "IOMMU Error",
            8 => "Virtualization Error",
            9 => "PCIe Error",
            10 => "Memory Controller Error",
            _ => null,
        };
    }

    private static string BuildWheaLocation(string? apicId, string? cache, string? pcie, int eventId)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(apicId)) parts.Add($"CPU APIC=0x{apicId}");
        if (!string.IsNullOrWhiteSpace(cache)) parts.Add($"Cache/Bank={cache}");
        if (!string.IsNullOrWhiteSpace(pcie)) parts.Add($"PCIe RequesterID=0x{pcie}");
        if (eventId == 46 || eventId == 47) parts.Add("(PCIe 层：显卡 / NVMe / 网卡等扩展卡)");
        if (parts.Count == 0) parts.Add("(无具体位置信息，可看 WHEA 原始事件 XML)");
        return string.Join(", ", parts);
    }

    private static List<string> BuildWheaSuggestion(string? typeText, int id)
    {
        // 先按 event ID 分级给建议，再叠加 type
        var list = new List<string>();
        switch (id)
        {
            case 17:
                list.Add("Event ID 17：硬件级 MCi STATUS，常见 CPU Cache / TLB / 核心微架构故障或不稳");
                list.Add("立刻关闭 PBO / CPU 超频 / C-State 省电 → 若恢复正常则说明 CPU 频率/电压不稳");
                list.Add("可以尝试 CPU Vcore 手动 +25mV，或 FLCKS/VDDCR SOC 适当加电压");
                break;
            case 18:
                list.Add("Event ID 18：严重的 CPU Cache 协议一致性错误，往往指向 CPU 硬件或超频不稳");
                list.Add("必须立刻关闭 PBO / CPU OC / XMP，更新主板 BIOS 到最新版");
                list.Add("若仍复现 → CPU 硬件问题概率大，走 RMA 或降频长期使用");
                break;
            case 19:
                list.Add("Event ID 19：CPU 内部总线奇偶校验错误，通常由超频或 CPU 供电不稳引起");
                list.Add("关闭 PBO + 降低 CPU 频率 0.2GHz 测试；SOC/VDDG 电压略加 (5-15mV)");
                list.Add("BIOS 更新到最新版本很重要，AGESA 更新经常修复 WHEA 19");
                break;
            case 46:
                list.Add("Event ID 46：PCIe 设备自身报告的 AER 错误 (PCIe Advanced Error Reporting)");
                list.Add("重插显卡、NVMe、网卡、采集卡；清插槽灰；检查供电线");
                list.Add("BIOS 中 PCIe 从 Gen4 降到 Gen3 测试；关闭 PCIe ASPM 省电");
                list.Add("若是显卡 → 换 GPU 驱动 (DDU 彻底卸载后换旧版本)；测 GPU 稳不稳定");
                list.Add("若是 NVMe → 固件更新；看 CrystalDiskInfo SMART 健康；换主板上别的 M.2 口");
                break;
            case 47:
                list.Add("Event ID 47：PCIe 根端口报告的错误，通常也是扩展卡或 PCIe 链路不稳");
                list.Add("和 Event 46 相同排障路径：重插卡 + 降 PCIe Gen + 关 ASPM + 更新 BIOS");
                break;
            default:
                list.Add($"WHEA Event ID {id}：硬件层错误，建议先按通用路径排查");
                break;
        }

        if (typeText != null && typeText.Contains("Memory"))
        {
            list.Add("含 Memory Controller/内存控制器关键字：内存条不稳或 XMP/EXPO 超频失败概率大 → 关 XMP 测试");
        }

        list.Add("通用：关闭全部超频 (CPU PBO/CPB/XMP/EXPO/PCIe/显卡) 是最快定位手段，不要跳过！");
        list.Add("更新 BIOS + 主板芯片组驱动；排查电源功率与供电线");
        return list;
    }

    // —— XML 字段提取通用辅助 ——

    private static string SafeGetMessage(EventRecord rec)
    {
        try { return rec.FormatDescription() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeGetXml(EventRecord rec)
    {
        try { return rec.ToXml() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string? TryGetStringFromXml(string xml, string key)
    {
        try
        {
            var xdoc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var dataEl = xdoc.Descendants(ns + "EventData").Elements(ns + "Data")
                .FirstOrDefault(e => e.Attribute("Name")?.Value == key);
            if (dataEl != null) return dataEl.Value;
            // 无 Name 属性的 param 模式：第几个
            var list = xdoc.Descendants(ns + "EventData").Elements(ns + "Data").ToList();
            int idx = int.TryParse(key.Replace("param", ""), out int p) ? p - 1 : -1;
            if (idx >= 0 && idx < list.Count) return list[idx].Value;
            return null;
        }
        catch { return null; }
    }

    private static uint TryGetUintFromXml(string xml, string key)
    {
        string? s = TryGetStringFromXml(xml, key);
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (uint.TryParse(s, out uint v)) return v;
        return 0;
    }

    private static ulong TryGetUlongFromXml(string xml, string key)
    {
        string? s = TryGetStringFromXml(xml, key);
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (ulong.TryParse(s, out ulong v)) return v;
        return 0;
    }

    private static uint? TryGetHexUintFromXml(string xml, string key)
    {
        string? s = TryGetStringFromXml(xml, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("0x", "").Replace("0X", "").Trim();
        if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return v;
        return null;
    }

    private static ulong? TryGetHexUlongFromXml(string xml, string key)
    {
        string? s = TryGetStringFromXml(xml, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("0x", "").Replace("0X", "").Trim();
        if (ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ulong v)) return v;
        return null;
    }

    private static DateTime? ParseShutdownEvent6008(DateTime defaultTime, string xml)
    {
        // 6008 EventData param1=小时:分:秒, param2=日/月/年
        try
        {
            string? t = TryGetStringFromXml(xml, "param1");
            string? d = TryGetStringFromXml(xml, "param2");
            if (string.IsNullOrWhiteSpace(t) || string.IsNullOrWhiteSpace(d)) return defaultTime;
            if (DateTime.TryParse($"{d} {t}", out var actual))
                return actual;
            if (DateTime.TryParse($"{d.Replace('/', '-')} {t}", out var actual2))
                return actual2;
            return defaultTime;
        }
        catch { return defaultTime; }
    }

    private static string ToXmlDateTime(DateTime t)
    {
        // EventLog 查询的 SystemTime 需要 ISO8601 UTC 样式
        DateTime utc = t.ToUniversalTime();
        return $"{utc.Year:D4}-{utc.Month:D2}-{utc.Day:D2}T{utc.Hour:D2}:{utc.Minute:D2}:{utc.Second:D2}.000Z";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max) + "\n...[已截断]";
    }
}
