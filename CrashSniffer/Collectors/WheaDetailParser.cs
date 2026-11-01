using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CrashSniffer.Models;

namespace CrashSniffer.Collectors;

/// <summary>
/// WHEA 事件细化解析：从事件 XML 中提取错误来源类型与报错处理器 APIC ID，
/// 给出"出错部位"结论（CPU 具体物理/逻辑核心 / PCIe 设备 / 内存），辅助超频与 Curve Optimizer 精准调校
/// </summary>
public static class WheaDetailParser
{
    /// <summary>
    /// 解析 WHEA 事件 XML，填充 ev.WheaHardwarePart 与 ev.WheaApicId
    /// </summary>
    public static void Enrich(CrashEvent ev, int eventId, string xml)
    {
        if (string.IsNullOrEmpty(xml)) return;

        string? sourceTypeRaw = null;
        string? errorTypeRaw = null;
        string? apicIdRaw = null;
        try
        {
            var xdoc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var data = xdoc.Descendants(ns + "EventData").Elements(ns + "Data").ToList();
            foreach (var d in data)
            {
                string? name = d.Attribute("Name")?.Value;
                if (name == "ErrorSourceType") sourceTypeRaw = d.Value;
                else if (name == "ErrorType") errorTypeRaw = d.Value;
                else if (name is "ApicId" or "ProcessorApicId" && string.IsNullOrWhiteSpace(apicIdRaw))
                    apicIdRaw = d.Value;
            }
        }
        catch
        {
            // XML 解析失败就靠 eventId 判断
        }

        int? apicId = ParseApicId(apicIdRaw);
        if (apicId.HasValue)
            ev.WheaApicId = apicId.Value;

        string part = BuildHardwarePart(eventId, sourceTypeRaw, errorTypeRaw, ev.WheaErrorType);

        if (apicId.HasValue && part.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
        {
            int logicalCore = apicId.Value;
            int physicalCore = logicalCore / 2;
            string corePin = $" [定位核心: 逻辑核 #{logicalCore} / 约物理核 #{physicalCore} (APIC={logicalCore})]";
            part += corePin;

            string coTip = $"🎯 超频/降压核心定位：报错来自 APIC ID {logicalCore}（逻辑核心 #{logicalCore}，开启 SMT/超线程时通常对应物理核心 #{physicalCore}）。若开启了 AMD PBO Curve Optimizer 或 Intel 分核降压，请单独将物理核心 #{physicalCore} 的负压调小（如向正方向回调 3~5）或加一点核心电压。";
            if (!ev.Troubleshooting.Contains(coTip))
                ev.Troubleshooting.Insert(0, coTip);
        }

        ev.WheaHardwarePart = part;
    }

    internal static int? ParseApicId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexVal) && hexVal >= 0)
                return hexVal;
        }
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int decVal) && decVal >= 0)
            return decVal;
        return null;
    }

    /// <summary>
    /// 综合事件 ID / ErrorSourceType / ErrorType 判断出错硬件部位
    /// </summary>
    private static string BuildHardwarePart(int eventId, string? sourceTypeRaw, string? errorTypeRaw, string typeText)
    {
        // 1. ErrorSourceType (WHEA_ERROR_SOURCE_TYPE) 是最可靠的信号
        if (int.TryParse(sourceTypeRaw, out int src))
        {
            switch (src)
            {
                case 0: // MCE：CPU 机器检查
                case 1: // CMC：CPU 更正机器检查
                case 2: // CPE：平台更正错误（内存/ECC 常走这里）
                    if (typeText.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                        (int.TryParse(errorTypeRaw, out int et) && et == 10))
                        return "内存条 / 内存控制器（先关 XMP/EXPO 降频测试）";
                    return "CPU / 主板供电（MCE 机器检查：先关 PBO/超频，加一点 Vcore/SOC 测试）";
                case 4: // PCIe AER
                    return "PCIe 设备 / 链路（显卡、NVMe、网卡、插槽：重插、降 Gen、关 ASPM）";
            }
        }

        // 2. 按事件 ID 兜底（与 WHEA-Logger 常见 ID 对应）
        return eventId switch
        {
            17 or 18 or 19 => "CPU / 主板供电（CPU 机器检查类错误：先关 PBO/超频，更新 BIOS/AGESA）",
            46 or 47 => "PCIe 设备 / 链路（显卡、NVMe、网卡、插槽：重插、降 Gen、关 ASPM）",
            _ when typeText.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                => "内存条 / 内存控制器（先关 XMP/EXPO 降频测试）",
            _ when typeText.Contains("PCIe", StringComparison.OrdinalIgnoreCase)
                => "PCIe 设备 / 链路（显卡、NVMe、网卡、插槽）",
            _ when typeText.Contains("Cache", StringComparison.OrdinalIgnoreCase)
                   || typeText.Contains("TLB", StringComparison.OrdinalIgnoreCase)
                   || typeText.Contains("微架构", StringComparison.Ordinal)
                => "CPU 核心 / 缓存（CPU 硬件或超频不稳概率大）",
            _ => string.Empty,
        };
    }
}
