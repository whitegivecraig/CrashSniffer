using System.Xml.Linq;
using CrashSniffer.Models;

namespace CrashSniffer.Collectors;

/// <summary>
/// WHEA 事件细化解析：从事件 XML 中提取错误来源类型，
/// 给出"出错部位"结论（CPU / PCIe 设备 / 内存），比单纯 Event ID 更精确
/// </summary>
public static class WheaDetailParser
{
    /// <summary>
    /// 解析 WHEA 事件 XML，填充 ev.WheaHardwarePart（出错部位结论）
    /// </summary>
    public static void Enrich(CrashEvent ev, int eventId, string xml)
    {
        if (string.IsNullOrEmpty(xml)) return;

        string? sourceTypeRaw = null;
        string? errorTypeRaw = null;
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
            }
        }
        catch
        {
            // XML 解析失败就靠 eventId 判断
        }

        ev.WheaHardwarePart = BuildHardwarePart(eventId, sourceTypeRaw, errorTypeRaw, ev.WheaErrorType);
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
