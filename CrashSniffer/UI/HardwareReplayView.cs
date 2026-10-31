using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// 崩溃时刻硬件读数回放视图：展示崩溃前后窗口内的时间序列曲线，
/// 用红色竖线标注崩溃时刻。支持同单位多指标叠加、CSV / PNG 导出。
/// GDI+ 自绘，不依赖第三方图表库。
/// </summary>
public class HardwareReplayView : UserControl
{
    private readonly Label _lblInfo = new()
    {
        Text = "无回放数据",
        AutoSize = true,
        ForeColor = Color.FromArgb(0x6b, 0x72, 0x80),
        Font = new Font("Microsoft YaHei UI", 9),
        Margin = new Padding(2, 4, 0, 4),
    };

    private readonly CheckedListBox _clbMetrics = new()
    {
        CheckOnClick = true,
        MultiColumn = true,
        ColumnWidth = 150,
        IntegralHeight = false,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        Margin = new Padding(0, 4, 0, 6),
    };

    private readonly Button _btnCsv = new() { Text = "导出 CSV", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
    private readonly Button _btnPng = new() { Text = "导出 PNG", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly RichTextBox _rtbFindings = new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(248, 250, 252),
        Font = new Font("Microsoft YaHei UI", 9),
        Dock = DockStyle.Fill,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        DetectUrls = false,
        Margin = new Padding(2, 2, 2, 4),
    };
    private readonly Panel _chartPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White };

    private HardwareSnapshot? _snapshot;
    private bool _updatingChecks;

    private static readonly Color CrashLineColor = Color.FromArgb(0xdc, 0x26, 0x26);
    private static readonly Color GridColor = Color.FromArgb(226, 232, 240);

    private static readonly Color[] SeriesPalette =
    {
        Color.FromArgb(0x1e, 0x3a, 0x8a),
        Color.FromArgb(0x0e, 0x74, 0x90),
        Color.FromArgb(0x7c, 0x3a, 0xed),
        Color.FromArgb(0xb4, 0x53, 0x09),
    };

    private static readonly Font AxisFont = new("Microsoft YaHei UI", 8);
    private static readonly Font LegendFont = new("Microsoft YaHei UI", 9);
    private static readonly Font MessageFont = new("Microsoft YaHei UI", 10);
    private static readonly Font FindingsFont = new("Microsoft YaHei UI", 9);
    private static readonly Font FindingsBoldFont = new("Microsoft YaHei UI", 9, FontStyle.Bold);

    private sealed class MetricDef
    {
        public string Label { get; }
        public string Unit { get; }
        public Func<HardwareReading, double?> Get { get; }

        public MetricDef(string label, string unit, Func<HardwareReading, double?> get)
        {
            Label = label;
            Unit = unit;
            Get = get;
        }
    }

    private static readonly MetricDef[] BaseMetrics =
    {
        new("CPU 温度", "℃", r => r.CpuTemperature),
        new("GPU 温度", "℃", r => r.GpuTemperature),
        new("GPU 热点", "℃", r => r.GpuHotspot),
        new("GPU 显存温度", "℃", r => r.GpuMemoryTemperature),
        new("主板温度", "℃", r => r.MoboMaxTemperature),
        new("磁盘最高温度", "℃", r => r.SsdTemperature),
        new("+12V", "V", r => r.Voltage12V),
        new("+5V", "V", r => r.Voltage5V),
        new("+3.3V", "V", r => r.Voltage3V),
        new("+3V Standby", "V", r => r.Voltage3VStandby),
        new("Vcore", "V", r => r.CpuVoltage),
        new("CPU SoC", "V", r => r.CpuSocVoltage),
        new("GPU 电压", "V", r => r.GpuVoltage),
        new("CMOS 电池", "V", r => r.CmosBatteryVoltage),
        new("CPU 负载", "%", r => r.CpuLoad),
        new("CPU 最高核心负载", "%", r => r.CpuMaxCoreLoad),
        new("GPU 负载", "%", r => r.GpuLoad),
        new("GPU 显存负载", "%", r => r.GpuMemoryLoad),
        new("GPU 风扇", "%", r => r.GpuFanPercent),
        new("内存占用", "%", r => r.MemoryLoad),
        new("虚拟内存占用", "%", r => r.VirtualMemoryLoad),
        new("GPU 显存占用", "%", r =>
            r.GpuMemoryUsedMb.HasValue && r.GpuMemoryTotalMb is > 0
                ? r.GpuMemoryUsedMb.Value / r.GpuMemoryTotalMb.Value * 100.0
                : null),
        new("磁盘最低寿命", "%", r => r.SsdLife),
        new("磁盘最低备用块", "%", r =>
        {
            var spares = r.Storages
                .Where(s => s.AvailableSparePercent.HasValue)
                .Select(s => s.AvailableSparePercent!.Value)
                .ToList();
            return spares.Count > 0 ? spares.Min() : null;
        }),
        new("CPU 风扇", "RPM", r => r.CpuFanRpm),
        new("GPU 风扇", "RPM", r => r.GpuFanRpm),
        new("CPU 频率", "MHz", r => r.CpuClock),
        new("GPU 频率", "MHz", r => r.GpuClock),
        new("CPU 功耗", "W", r => r.CpuPower),
        new("GPU 功耗", "W", r => r.GpuPower),
        new("内存已用", "GB", r => r.MemoryUsedGb),
        new("虚拟内存已用", "GB", r => r.VirtualMemoryUsedGb),
    };

    private readonly List<MetricDef> _metrics = new();

    public HardwareReplayView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(248, 250, 252);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 标题 + 右侧导出按钮；指标选择框独占一行，避免在窄面板里只露出两三行
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "指标（同单位可叠加）：",
            AutoSize = true,
            Margin = new Padding(0, 8, 4, 0),
        }, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0),
        };
        _btnCsv.Margin = new Padding(0, 3, 8, 0);
        _btnPng.Margin = new Padding(0, 3, 0, 0);
        actions.Controls.Add(_btnCsv);
        actions.Controls.Add(_btnPng);
        header.Controls.Add(actions, 1, 0);
        root.Controls.Add(header, 0, 0);

        RebuildMetrics(null);
        _clbMetrics.SetItemChecked(0, true);
        _clbMetrics.SetItemChecked(1, true);
        _clbMetrics.ItemCheck += OnMetricItemCheck;
        root.Controls.Add(_clbMetrics, 0, 1);
        root.Controls.Add(_lblInfo, 0, 2);
        root.Controls.Add(_rtbFindings, 0, 3);

        _chartPanel.Paint += ChartPanel_Paint;
        root.Controls.Add(_chartPanel, 0, 4);
        Controls.Add(root);

        _btnCsv.Click += (_, _) => ExportCsv();
        _btnPng.Click += (_, _) => ExportPng();
        _btnCsv.Enabled = false;
        _btnPng.Enabled = false;
    }

    /// <summary>加载崩溃快照并刷新曲线</summary>
    public void LoadSnapshot(HardwareSnapshot? snapshot)
    {
        _snapshot = snapshot;
        RebuildMetrics(snapshot);
        if (snapshot == null || snapshot.Readings.Count == 0)
        {
            _lblInfo.Text = "该崩溃事件无硬件回放数据（监控未运行或窗口无读数）";
        }
        else
        {
            string source = snapshot.CrashType == "连续采样" ? " · 来源：后台连续采样" : string.Empty;
            _lblInfo.Text = $"崩溃时刻 {snapshot.CrashTime:yyyy-MM-dd HH:mm:ss} · " +
                            $"窗口内 {snapshot.Readings.Count} 个采样点{source}";
        }
        _btnCsv.Enabled = _btnPng.Enabled = snapshot is { Readings.Count: > 0 };
        RenderFindings(snapshot);
        _chartPanel.Invalidate();
    }

    /// <summary>重建指标列表：固定指标 + 快照中出现的逐盘温度/寿命指标</summary>
    private void RebuildMetrics(HardwareSnapshot? snapshot)
    {
        var checkedLabels = new HashSet<string>();
        foreach (object? item in _clbMetrics.CheckedItems)
        {
            if (item?.ToString() is { } text)
                checkedLabels.Add(text);
        }

        _metrics.Clear();
        _metrics.AddRange(BaseMetrics);

        if (snapshot != null)
        {
            foreach (var drive in StorageLabel.Representatives(snapshot))
            {
                string key = StorageLabel.Key(drive);
                string label = StorageLabel.Short(drive);
                _metrics.Add(new MetricDef($"磁盘 {label} 温度", "℃",
                    r => StorageLabel.Find(r, key)?.Temperature));
                _metrics.Add(new MetricDef($"磁盘 {label} 寿命", "%",
                    r => StorageLabel.Find(r, key)?.LifePercent));
                _metrics.Add(new MetricDef($"磁盘 {label} 备用块", "%",
                    r => StorageLabel.Find(r, key)?.AvailableSparePercent));
            }
        }

        _updatingChecks = true;
        try
        {
            _clbMetrics.BeginUpdate();
            _clbMetrics.Items.Clear();
            foreach (var metric in _metrics)
                _clbMetrics.Items.Add($"{metric.Label} ({metric.Unit})");

            // 按显示文本恢复勾选（跨快照保持用户选择）
            for (int i = 0; i < _metrics.Count; i++)
            {
                string label = $"{_metrics[i].Label} ({_metrics[i].Unit})";
                if (checkedLabels.Contains(label))
                    _clbMetrics.SetItemChecked(i, true);
            }

            if (_clbMetrics.CheckedItems.Count == 0 && _metrics.Count >= 2)
            {
                _clbMetrics.SetItemChecked(0, true);
                _clbMetrics.SetItemChecked(1, true);
            }
        }
        finally
        {
            _clbMetrics.EndUpdate();
            _updatingChecks = false;
        }
    }

    private void RenderFindings(HardwareSnapshot? snapshot)
    {
        _rtbFindings.Clear();
        if (snapshot == null || snapshot.Readings.Count == 0)
        {
            AppendFinding("🩺 崩溃前硬件分析：无读数可分析。", Color.Gray, bold: false);
            return;
        }

        var anomalies = HardwareAnomalyAnalyzer.Analyze(snapshot);
        int preCount = HardwareAnomalyAnalyzer.GetPreCrashReadings(snapshot).Count;
        AppendFinding($"🩺 崩溃前硬件分析（{preCount} 个采样点）：",
            Color.FromArgb(0x1f, 0x29, 0x37), bold: true);

        if (anomalies.Count == 0)
        {
            AppendFinding("✅ 未发现明显硬件异常。", Color.FromArgb(0x04, 0x78, 0x57), bold: false);
            return;
        }

        foreach (var a in anomalies)
        {
            string icon = a.Severity switch
            {
                AnomalySeverity.Critical => "⛔",
                AnomalySeverity.Warning => "⚠",
                _ => "ℹ",
            };
            AppendFinding($"{icon} {a.Title}：{a.Detail}", AnomalyColor(a.Severity), bold: false);
        }
    }

    private void AppendFinding(string text, Color color, bool bold)
    {
        _rtbFindings.SelectionStart = _rtbFindings.TextLength;
        _rtbFindings.SelectionLength = 0;
        _rtbFindings.SelectionColor = color;
        _rtbFindings.SelectionFont = bold ? FindingsBoldFont : FindingsFont;
        _rtbFindings.AppendText(text + Environment.NewLine);
    }

    private static Color AnomalyColor(AnomalySeverity severity) => severity switch
    {
        AnomalySeverity.Critical => Color.FromArgb(0xdc, 0x26, 0x26),
        AnomalySeverity.Warning => Color.FromArgb(0xd9, 0x77, 0x06),
        _ => Color.FromArgb(0x6b, 0x72, 0x80),
    };

    private void OnMetricItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_updatingChecks || e.Index < 0 || e.Index >= _metrics.Count) return;
        if (e.NewValue != CheckState.Checked) return;

        // 只允许同单位叠加，勾选新单位时自动取消其他单位的指标
        string unit = _metrics[e.Index].Unit;
        _updatingChecks = true;
        try
        {
            for (int i = 0; i < _metrics.Count; i++)
            {
                if (i != e.Index && _metrics[i].Unit != unit && _clbMetrics.GetItemChecked(i))
                    _clbMetrics.SetItemChecked(i, false);
            }
        }
        finally
        {
            _updatingChecks = false;
        }
        _chartPanel.Invalidate();
    }

    private List<int> GetCheckedMetricIndices()
    {
        var list = new List<int>();
        for (int i = 0; i < _metrics.Count; i++)
            if (_clbMetrics.GetItemChecked(i)) list.Add(i);
        return list;
    }

    private void ExportCsv()
    {
        if (_snapshot == null || _snapshot.Readings.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            FileName = $"hardware_{_snapshot.CrashTime:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = "csv",
            Title = "导出硬件读数 CSV",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, HardwareCsvExporter.BuildCsv(_snapshot), new UTF8Encoding(true));
            MessageBox.Show(this, $"已导出：\n{dlg.FileName}", "导出成功",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportPng()
    {
        if (_snapshot == null || _snapshot.Readings.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            FileName = $"hardware_{_snapshot.CrashTime:yyyyMMdd_HHmmss}.png",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = "png",
            Title = "导出回放曲线 PNG",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int width = Math.Max(960, _chartPanel.ClientSize.Width);
        int height = Math.Max(520, _chartPanel.ClientSize.Height + 70);
        try
        {
            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var titleFont = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
                g.DrawString(
                    $"崩溃时刻 {_snapshot.CrashTime:yyyy-MM-dd HH:mm:ss} · {_snapshot.Readings.Count} 个采样点",
                    titleFont, Brushes.Black, 20, 14);
                RenderChart(g, new Rectangle(20, 52, width - 40, height - 72));
            }
            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            MessageBox.Show(this, $"已导出：\n{dlg.FileName}", "导出成功",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChartPanel_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Color.White);
        RenderChart(e.Graphics, _chartPanel.ClientRectangle);
    }

    private void RenderChart(Graphics g, Rectangle bounds)
    {
        if (_snapshot == null || _snapshot.Readings.Count == 0)
        {
            DrawMessage(g, bounds, "无回放数据");
            return;
        }

        var readings = _snapshot.Readings.OrderBy(r => r.Timestamp).ToList();
        if (readings.Count < 2)
        {
            DrawMessage(g, bounds, "采样点不足，无法绘制曲线");
            return;
        }

        var selected = GetCheckedMetricIndices();
        if (selected.Count == 0)
        {
            DrawMessage(g, bounds, "请勾选至少一个指标");
            return;
        }

        DateTime tStart = readings[0].Timestamp;
        DateTime tEnd = readings[^1].Timestamp;
        double timeSpan = (tEnd - tStart).TotalSeconds;
        if (timeSpan <= 0) return;

        int padL = 64, padR = 20, padT = 40, padB = 40;
        int w = bounds.Width - padL - padR;
        int h = bounds.Height - padT - padB;
        if (w <= 0 || h <= 0) return;

        var series = new List<(MetricDef Def, Color Color, List<double?> Values)>();
        for (int k = 0; k < selected.Count; k++)
        {
            var def = _metrics[selected[k]];
            series.Add((def, SeriesPalette[k % SeriesPalette.Length], readings.Select(def.Get).ToList()));
        }

        var allValues = series.SelectMany(s => s.Values)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
        if (allValues.Count == 0)
        {
            DrawMessage(g, bounds, "所选指标在该窗口内无数据");
            return;
        }

        double yMin = allValues.Min();
        double yMax = allValues.Max();
        double yPad = (yMax - yMin) * 0.1;
        if (yPad < 0.01) yPad = 1;
        yMin -= yPad;
        yMax += yPad;

        // 网格 + Y 轴刻度（同一单位）
        using var gridPen = new Pen(GridColor) { DashStyle = DashStyle.Dash };
        for (int i = 0; i <= 4; i++)
        {
            int y = padT + h * i / 4;
            g.DrawLine(gridPen, padL, y, padL + w, y);
            double val = yMax - (yMax - yMin) * i / 4;
            g.DrawString(val.ToString("F1"), AxisFont, Brushes.Gray, 2, y - 8);
        }
        g.DrawString($"单位：{series[0].Def.Unit}", AxisFont, Brushes.Gray, 2, 4);

        // 图例（顶部）
        float lx = padL;
        foreach (var s in series)
        {
            using var legendPen = new Pen(s.Color, 2);
            g.DrawLine(legendPen, lx, 12, lx + 16, 12);
            double? last = s.Values.LastOrDefault(v => v.HasValue);
            string text = last.HasValue
                ? $"{s.Def.Label} {last.Value.ToString("0.#")}{s.Def.Unit}"
                : $"{s.Def.Label} —";
            g.DrawString(text, LegendFont, Brushes.Black, lx + 20, 4);
            lx += 20 + g.MeasureString(text, LegendFont).Width + 14;
        }

        // 时间轴
        g.DrawString(tStart.ToString("HH:mm:ss"), AxisFont, Brushes.Gray, padL, padT + h + 5);
        g.DrawString(tEnd.ToString("HH:mm:ss"), AxisFont, Brushes.Gray, padL + w - 50, padT + h + 5);

        // 数据曲线（空值断开，不跨缺口连线）
        foreach (var s in series)
        {
            using var linePen = new Pen(s.Color, 1.8f);
            DrawSeries(g, linePen, readings, s.Values, tStart, timeSpan, padL, padT, w, h, yMin, yMax);
        }

        // 崩溃时刻标注
        double crashFrac = (_snapshot.CrashTime - tStart).TotalSeconds / timeSpan;
        if (crashFrac >= 0 && crashFrac <= 1)
        {
            int cx = padL + (int)(w * crashFrac);
            using var crashPen = new Pen(CrashLineColor, 2) { DashStyle = DashStyle.Dash };
            g.DrawLine(crashPen, cx, padT, cx, padT + h);
            using var crashBrush = new SolidBrush(CrashLineColor);
            g.DrawString("崩溃时刻", AxisFont, crashBrush, cx + 4, padT + 4);
        }
    }

    private static void DrawSeries(Graphics g, Pen pen, IReadOnlyList<HardwareReading> readings,
        IReadOnlyList<double?> values, DateTime tStart, double timeSpan,
        int padL, int padT, int w, int h, double yMin, double yMax)
    {
        PointF? prev = null;
        for (int i = 0; i < readings.Count; i++)
        {
            var v = values[i];
            if (!v.HasValue)
            {
                prev = null;
                continue;
            }
            float x = padL + (float)(w * ((readings[i].Timestamp - tStart).TotalSeconds / timeSpan));
            float y = padT + (float)(h * ((yMax - v.Value) / (yMax - yMin)));
            var pt = new PointF(x, y);
            if (prev.HasValue) g.DrawLine(pen, prev.Value, pt);
            prev = pt;
        }
    }

    private static void DrawMessage(Graphics g, Rectangle bounds, string text)
    {
        g.DrawString(text, MessageFont, Brushes.Gray, bounds, new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        });
    }
}
