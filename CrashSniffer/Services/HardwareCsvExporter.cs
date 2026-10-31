using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件读数 CSV 导出：按时间升序输出全部指标，附相对崩溃时刻的秒数。
/// 逐盘温度/寿命/已用/备用块、逐核心负载/频率、逐风扇转速/占空比
/// 按快照中出现的硬件动态追加列。调用方负责以 UTF-8 BOM 写盘（Excel 友好）。
/// </summary>
public static class HardwareCsvExporter
{
    private const string Header =
        "时间,相对崩溃(秒),CPU温度(℃),CPU负载(%),CPU最高核心负载(%),CPU频率(MHz),CPU功耗(W)," +
        "GPU温度(℃),GPU热点(℃),GPU显存温度(℃),GPU负载(%),GPU显存负载(%),GPU频率(MHz),GPU显存频率(MHz),GPU功耗(W),GPU显存已用(MB),GPU显存总量(MB),GPU风扇(%),GPU核心电压(V)," +
        "内存占用(%),虚拟内存占用(%),内存已用(GB),内存总量(GB),虚拟内存已用(GB),虚拟内存总量(GB)," +
        "主板温度(℃)," +
        "+12V(V),+5V(V),+3.3V(V),+3V待机(V),Vcore(V),CPU SoC(V),CMOS电池(V)," +
        "CPU风扇(RPM),GPU风扇(RPM),磁盘最高温度(℃),磁盘最低寿命(%),磁盘最低备用块(%)";

    public static string BuildCsv(HardwareSnapshot snapshot)
    {
        var readings = snapshot.Readings.OrderBy(r => r.Timestamp).ToList();
        var drives = StorageLabel.Representatives(snapshot);
        var coreIndices = readings
            .SelectMany(r => r.CpuCores)
            .Select(c => c.Index)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
        var fanNames = readings
            .SelectMany(r => r.Fans)
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(HardwareMonitorService.ParseTrailingIndex)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new StringBuilder();

        sb.Append(Header);
        foreach (var drive in drives)
        {
            string label = SafeLabel(StorageLabel.Short(drive));
            sb.Append($",磁盘 {label} 温度(℃),磁盘 {label} 寿命(%),磁盘 {label} 已用(%),磁盘 {label} 备用块(%)");
        }
        foreach (int core in coreIndices)
            sb.Append($",核心 {core} 负载(%),核心 {core} 频率(MHz)");
        foreach (string fan in fanNames)
        {
            string label = SafeLabel(FanLabel(fan));
            sb.Append($",风扇 {label} 转速(RPM),风扇 {label} 占空比(%)");
        }
        sb.AppendLine();

        foreach (var r in readings)
        {
            sb.Append(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(((r.Timestamp - snapshot.CrashTime).TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Format(r.CpuTemperature)).Append(',');
            sb.Append(Format(r.CpuLoad)).Append(',');
            sb.Append(Format(r.CpuMaxCoreLoad)).Append(',');
            sb.Append(Format(r.CpuClock)).Append(',');
            sb.Append(Format(r.CpuPower)).Append(',');
            sb.Append(Format(r.GpuTemperature)).Append(',');
            sb.Append(Format(r.GpuHotspot)).Append(',');
            sb.Append(Format(r.GpuMemoryTemperature)).Append(',');
            sb.Append(Format(r.GpuLoad)).Append(',');
            sb.Append(Format(r.GpuMemoryLoad)).Append(',');
            sb.Append(Format(r.GpuClock)).Append(',');
            sb.Append(Format(r.GpuMemoryClock)).Append(',');
            sb.Append(Format(r.GpuPower)).Append(',');
            sb.Append(Format(r.GpuMemoryUsedMb)).Append(',');
            sb.Append(Format(r.GpuMemoryTotalMb)).Append(',');
            sb.Append(Format(r.GpuFanPercent)).Append(',');
            sb.Append(Format(r.GpuVoltage)).Append(',');
            sb.Append(Format(r.MemoryLoad)).Append(',');
            sb.Append(Format(r.VirtualMemoryLoad)).Append(',');
            sb.Append(Format(r.MemoryUsedGb)).Append(',');
            sb.Append(Format(r.MemoryTotalGb)).Append(',');
            sb.Append(Format(r.VirtualMemoryUsedGb)).Append(',');
            sb.Append(Format(r.VirtualMemoryTotalGb)).Append(',');
            sb.Append(Format(r.MoboMaxTemperature)).Append(',');
            sb.Append(Format(r.Voltage12V)).Append(',');
            sb.Append(Format(r.Voltage5V)).Append(',');
            sb.Append(Format(r.Voltage3V)).Append(',');
            sb.Append(Format(r.Voltage3VStandby)).Append(',');
            sb.Append(Format(r.CpuVoltage)).Append(',');
            sb.Append(Format(r.CpuSocVoltage)).Append(',');
            sb.Append(Format(r.CmosBatteryVoltage)).Append(',');
            sb.Append(Format(r.CpuFanRpm)).Append(',');
            sb.Append(Format(r.GpuFanRpm)).Append(',');
            sb.Append(Format(r.SsdTemperature)).Append(',');
            sb.Append(Format(r.SsdLife)).Append(',');
            sb.Append(Format(MinSpare(r)));

            foreach (var drive in drives)
            {
                var storage = StorageLabel.Find(r, StorageLabel.Key(drive));
                sb.Append(',').Append(Format(storage?.Temperature));
                sb.Append(',').Append(Format(storage?.LifePercent));
                sb.Append(',').Append(Format(storage?.UsedSpacePercent));
                sb.Append(',').Append(Format(storage?.AvailableSparePercent));
            }

            foreach (int core in coreIndices)
            {
                var reading = r.CpuCores.FirstOrDefault(c => c.Index == core);
                sb.Append(',').Append(Format(reading?.Load));
                sb.Append(',').Append(Format(reading?.Clock));
            }

            foreach (string fan in fanNames)
            {
                var reading = r.Fans.FirstOrDefault(f =>
                    string.Equals(f.Name, fan, StringComparison.OrdinalIgnoreCase));
                sb.Append(',').Append(Format(reading?.Rpm));
                sb.Append(',').Append(Format(reading?.Percent));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static double? MinSpare(HardwareReading reading)
    {
        var spares = reading.Storages
            .Where(s => s.AvailableSparePercent.HasValue)
            .Select(s => s.AvailableSparePercent!.Value)
            .ToList();
        return spares.Count > 0 ? spares.Min() : null;
    }

    private static string FanLabel(string name)
    {
        int index = HardwareMonitorService.ParseTrailingIndex(name);
        return index > 0 ? index.ToString(CultureInfo.InvariantCulture) : name;
    }

    private static string Format(double? v)
        => v.HasValue ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;

    private static string SafeLabel(string label)
        => label.Replace(',', '；').Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
}
