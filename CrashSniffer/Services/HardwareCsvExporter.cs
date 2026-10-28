using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件读数 CSV 导出：按时间升序输出全部指标，附相对崩溃时刻的秒数。
/// 调用方负责以 UTF-8 BOM 写盘（Excel 友好）。
/// </summary>
public static class HardwareCsvExporter
{
    private const string Header =
        "时间,相对崩溃(秒),CPU温度(℃),GPU温度(℃),GPU热点(℃),SSD温度(℃)," +
        "CPU负载(%),GPU负载(%),CPU频率(MHz),GPU频率(MHz),GPU显存频率(MHz)," +
        "CPU功耗(W),GPU功耗(W),+12V(V),+5V(V),+3.3V(V),Vcore(V)," +
        "CPU风扇(RPM),GPU风扇(RPM),SSD寿命(%)";

    public static string BuildCsv(HardwareSnapshot snapshot)
    {
        var readings = snapshot.Readings.OrderBy(r => r.Timestamp).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(Header);

        foreach (var r in readings)
        {
            sb.Append(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(((r.Timestamp - snapshot.CrashTime).TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Format(r.CpuTemperature)).Append(',');
            sb.Append(Format(r.GpuTemperature)).Append(',');
            sb.Append(Format(r.GpuHotspot)).Append(',');
            sb.Append(Format(r.SsdTemperature)).Append(',');
            sb.Append(Format(r.CpuLoad)).Append(',');
            sb.Append(Format(r.GpuLoad)).Append(',');
            sb.Append(Format(r.CpuClock)).Append(',');
            sb.Append(Format(r.GpuClock)).Append(',');
            sb.Append(Format(r.GpuMemoryClock)).Append(',');
            sb.Append(Format(r.CpuPower)).Append(',');
            sb.Append(Format(r.GpuPower)).Append(',');
            sb.Append(Format(r.Voltage12V)).Append(',');
            sb.Append(Format(r.Voltage5V)).Append(',');
            sb.Append(Format(r.Voltage3V)).Append(',');
            sb.Append(Format(r.CpuVoltage)).Append(',');
            sb.Append(Format(r.CpuFanRpm)).Append(',');
            sb.Append(Format(r.GpuFanRpm)).Append(',');
            sb.Append(Format(r.SsdLife));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Format(double? v)
        => v.HasValue ? v.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;
}
