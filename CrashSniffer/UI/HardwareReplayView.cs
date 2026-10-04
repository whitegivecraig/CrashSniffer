using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// 崩溃时刻硬件读数回放视图：展示崩溃前后窗口内的温度/电压/风扇时间序列曲线，
/// 用红色竖线标注崩溃时刻。GDI+ 自绘，不依赖第三方图表库。
/// </summary>
public class HardwareReplayView : UserControl
{
    private readonly Label _lblInfo = new()
    {
        Text = "无回放数据",
        AutoSize = true,
        ForeColor = Color.FromArgb(0x6b, 0x72, 0x80),
        Font = new Font("Microsoft YaHei UI", 9),
    };

    private readonly ComboBox _cmbMetric = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Panel _chartPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White };

    private HardwareSnapshot? _snapshot;

    private static readonly Color CrashLineColor = Color.FromArgb(0xdc, 0x26, 0x26);
    private static readonly Color LineColor = Color.FromArgb(0x1e, 0x3a, 0x8a);
    private static readonly Color GridColor = Color.FromArgb(226, 232, 240);

    private enum MetricKind { CpuTemp, GpuTemp, Voltage12V, CpuFan }

    public HardwareReplayView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(248, 250, 252);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        top.Controls.Add(new Label { Text = "指标：", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        _cmbMetric.Items.AddRange(new object[] { "CPU 温度", "GPU 温度", "+12V 电压", "CPU 风扇转速" });
        _cmbMetric.SelectedIndex = 0;
        _cmbMetric.SelectedIndexChanged += (_, _) => _chartPanel.Invalidate();
        top.Controls.Add(_cmbMetric);
        top.Controls.Add(new Label { Text = "  ", AutoSize = true });
        top.Controls.Add(_lblInfo);
        root.Controls.Add(top, 0, 0);

        _chartPanel.Paint += ChartPanel_Paint;
        root.Controls.Add(_chartPanel, 0, 1);

        Controls.Add(root);
    }

    /// <summary>加载崩溃快照并刷新曲线</summary>
    public void LoadSnapshot(HardwareSnapshot? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot == null || snapshot.Readings.Count == 0)
        {
            _lblInfo.Text = "该崩溃事件无硬件回放数据（监控未运行或窗口无读数）";
        }
        else
        {
            _lblInfo.Text = $"崩溃时刻 {snapshot.CrashTime:yyyy-MM-dd HH:mm:ss} · " +
                            $"窗口内 {snapshot.Readings.Count} 个采样点";
        }
        _chartPanel.Invalidate();
    }

    private void ChartPanel_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        if (_snapshot == null || _snapshot.Readings.Count < 2)
        {
            using var f = new Font("Microsoft YaHei UI", 10);
            g.DrawString("无足够数据绘制曲线", f, Brushes.Gray, _chartPanel.ClientRectangle,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            return;
        }

        var readings = _snapshot.Readings.OrderBy(r => r.Timestamp).ToList();
        DateTime tStart = readings[0].Timestamp;
        DateTime tEnd = readings[^1].Timestamp;
        double timeSpan = (tEnd - tStart).TotalSeconds;
        if (timeSpan <= 0) return;

        int padL = 60, padR = 20, padT = 20, padB = 40;
        int w = _chartPanel.ClientSize.Width - padL - padR;
        int h = _chartPanel.ClientSize.Height - padT - padB;
        if (w <= 0 || h <= 0) return;

        var metric = (MetricKind)_cmbMetric.SelectedIndex;
        var values = readings.Select(r => GetMetricValue(r, metric)).ToList();
        var validValues = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (validValues.Count == 0)
        {
            using var f = new Font("Microsoft YaHei UI", 10);
            g.DrawString("该指标无数据", f, Brushes.Gray, _chartPanel.ClientRectangle,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            return;
        }

        double yMin = validValues.Min();
        double yMax = validValues.Max();
        double yPad = (yMax - yMin) * 0.1;
        if (yPad < 0.01) yPad = 1;
        yMin -= yPad; yMax += yPad;

        using var gridPen = new Pen(GridColor) { DashStyle = DashStyle.Dash };
        using var axisFont = new Font("Microsoft YaHei UI", 8);
        for (int i = 0; i <= 4; i++)
        {
            int y = padT + h * i / 4;
            g.DrawLine(gridPen, padL, y, padL + w, y);
            double val = yMax - (yMax - yMin) * i / 4;
            g.DrawString(val.ToString("F1"), axisFont, Brushes.Gray, 2, y - 8);
        }

        g.DrawString(tStart.ToString("HH:mm:ss"), axisFont, Brushes.Gray, padL, padT + h + 5);
        g.DrawString(tEnd.ToString("HH:mm:ss"), axisFont, Brushes.Gray, padL + w - 50, padT + h + 5);

        using var linePen = new Pen(LineColor, 2);
        var points = new List<PointF>();
        for (int i = 0; i < readings.Count; i++)
        {
            if (values[i] == null) continue;
            double xFrac = (readings[i].Timestamp - tStart).TotalSeconds / timeSpan;
            double yFrac = (yMax - values[i]!.Value) / (yMax - yMin);
            points.Add(new PointF(padL + (float)(w * xFrac), padT + (float)(h * yFrac)));
        }
        if (points.Count >= 2)
            g.DrawLines(linePen, points.ToArray());

        double crashFrac = (_snapshot.CrashTime - tStart).TotalSeconds / timeSpan;
        if (crashFrac >= 0 && crashFrac <= 1)
        {
            int cx = padL + (int)(w * crashFrac);
            using var crashPen = new Pen(CrashLineColor, 2) { DashStyle = DashStyle.Dash };
            g.DrawLine(crashPen, cx, padT, cx, padT + h);
            using var crashBrush = new SolidBrush(CrashLineColor);
            g.DrawString("崩溃时刻", axisFont, crashBrush, cx + 4, padT + 2);
        }
    }

    private static double? GetMetricValue(HardwareReading r, MetricKind m) => m switch
    {
        MetricKind.CpuTemp => r.CpuTemperature,
        MetricKind.GpuTemp => r.GpuTemperature,
        MetricKind.Voltage12V => r.Voltage12V,
        MetricKind.CpuFan => r.CpuFanRpm,
        _ => null,
    };
}
