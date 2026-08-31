using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// "历史趋势"标签页：
/// 1. 每日崩溃次数柱状图（最近30天）
/// 2. 停止代码分布 / 嫌疑驱动分布
/// 3. 两个时间段对比（换驱动/改设置前后 diff）
/// 数据来自 HistoryStore 的 JSON 存档
/// </summary>
public class HistoryTab : UserControl
{
    private readonly Button _btnRefresh = new() { Text = "🔄 刷新历史", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
    private readonly Label _lblStats = new() { Text = "暂无历史数据（每次扫描会自动记录）", AutoSize = true, ForeColor = Color.FromArgb(0x47, 0x55, 0x69), Font = new Font("Microsoft YaHei UI", 9) };
    private readonly Label _lblChart = new() { Text = "最近 30 天每日崩溃次数", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
    private readonly Panel _chartPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };

    private readonly DataGridView _gridCodes = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, GridColor = Color.FromArgb(220, 228, 238), Font = new Font("Microsoft YaHei UI", 9) };
    private readonly DataGridView _gridDrivers = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, GridColor = Color.FromArgb(220, 228, 238), Font = new Font("Microsoft YaHei UI", 9) };

    private readonly DateTimePicker _dtpAStart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110 };
    private readonly DateTimePicker _dtpAEnd = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110 };
    private readonly DateTimePicker _dtpBStart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110 };
    private readonly DateTimePicker _dtpBEnd = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110 };
    private readonly Button _btnDiff = new() { Text = "📊 对比两个时段", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
    private readonly RichTextBox _tbDiff = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9.5f), BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical };

    /// <summary>最近 30 天每日崩溃数（chartPanel 绘制数据）</summary>
    private List<(DateTime day, int count)> _daily = new();

    public HistoryTab()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(248, 250, 252);
        BuildUi();
        SetDefaultDiffRanges();
        _btnRefresh.Click += (_, _) => LoadHistory();
        _btnDiff.Click += (_, _) => RunDiff();
        _chartPanel.Paint += (_, _) => DrawChart();
        _chartPanel.Resize += (_, _) => _chartPanel.Invalidate();
        Load += (_, _) => LoadHistory();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 43));

        // 第 0 行：刷新 + 摘要
        var topPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        topPanel.Controls.Add(_btnRefresh);
        _btnRefresh.ForeColor = Color.FromArgb(0x04, 0x78, 0x57);
        topPanel.Controls.Add(new Label { Text = "  ", AutoSize = true });
        topPanel.Controls.Add(_lblStats);
        root.Controls.Add(topPanel, 0, 0);
        root.SetColumnSpan(topPanel, 2);

        // 第 1 行：每日趋势图（标题 + 图表放在同一容器内）
        var chartHost = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 0, 0, 8) };
        chartHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        chartHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chartHost.Controls.Add(new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Controls = { _lblChart } }, 0, 0);
        chartHost.Controls.Add(_chartPanel, 0, 1);
        root.Controls.Add(chartHost, 0, 1);
        root.SetColumnSpan(chartHost, 2);

        // 第 2 行：两个分布表
        var gridCodesHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        gridCodesHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gridCodesHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gridCodesHost.Controls.Add(new Label { Text = "停止代码分布", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) }, 0, 0);
        gridCodesHost.Controls.Add(_gridCodes, 0, 1);

        var gridDriversHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        gridDriversHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gridDriversHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gridDriversHost.Controls.Add(new Label { Text = "嫌疑驱动分布", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) }, 0, 0);
        gridDriversHost.Controls.Add(_gridDrivers, 0, 1);

        root.Controls.Add(gridCodesHost, 0, 2);
        root.Controls.Add(gridDriversHost, 1, 2);

        _gridCodes.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "停止代码", Width = 200 });
        _gridCodes.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "次数", Width = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _gridDrivers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "驱动", Width = 200 });
        _gridDrivers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "次数", Width = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        // 第 3 行：对比区
        var diffHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        diffHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        diffHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var diffBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        diffBar.Controls.Add(new Label { Text = "对比  时段A:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        diffBar.Controls.Add(_dtpAStart);
        diffBar.Controls.Add(new Label { Text = "→", AutoSize = true, Padding = new Padding(2, 6, 2, 0) });
        diffBar.Controls.Add(_dtpAEnd);
        diffBar.Controls.Add(new Label { Text = "   时段B:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        diffBar.Controls.Add(_dtpBStart);
        diffBar.Controls.Add(new Label { Text = "→", AutoSize = true, Padding = new Padding(2, 6, 2, 0) });
        diffBar.Controls.Add(_dtpBEnd);
        _btnDiff.ForeColor = Color.FromArgb(0x83, 0x18, 0xb4);
        diffBar.Controls.Add(new Label { Text = "  ", AutoSize = true });
        diffBar.Controls.Add(_btnDiff);
        diffBar.Controls.Add(new Label
        {
            Text = "  (如：换驱动前 A vs 换驱动后 B)",
            AutoSize = true,
            ForeColor = Color.FromArgb(0x47, 0x55, 0x69),
            Padding = new Padding(4, 6, 0, 0),
        });

        diffHost.Controls.Add(diffBar, 0, 0);
        diffHost.Controls.Add(_tbDiff, 0, 1);
        root.Controls.Add(diffHost, 0, 3);
        root.SetColumnSpan(diffHost, 2);

        Controls.Add(root);
    }

    /// <summary>外部通知：扫描完成，历史可能更新</summary>
    public void OnScanCompleted()
    {
        LoadHistory();
    }

    /// <summary>重新读取历史存档并刷新所有展示</summary>
    public void LoadHistory()
    {
        var records = HistoryStore.Load();

        _lblStats.Text = records.Count == 0
            ? "暂无历史数据（每次扫描会自动记录）"
            : $"历史共 {records.Count} 条记录 · 存档: {HistoryStore.FilePath}";

        // 每日趋势（最近30天）
        DateTime today = DateTime.Today;
        var byDay = records
            .Where(r => r.Time >= today.AddDays(-29))
            .GroupBy(r => r.Time.Date)
            .ToDictionary(g => g.Key, g => g.Count());
        _daily = Enumerable.Range(0, 30)
            .Select(i => (today.AddDays(-29 + i), byDay.TryGetValue(today.AddDays(-29 + i), out int c) ? c : 0))
            .ToList();
        _chartPanel.Invalidate();

        // 停止代码分布
        _gridCodes.Rows.Clear();
        foreach (var g in records.Where(r => r.BugCheckCode > 0)
                     .GroupBy(r => $"0x{r.BugCheckCode:X} {r.StopCode}")
                     .OrderByDescending(g => g.Count()))
        {
            _gridCodes.Rows.Add(g.Key, g.Count());
        }

        // 嫌疑驱动分布
        _gridDrivers.Rows.Clear();
        foreach (var g in records.Where(r => !string.IsNullOrWhiteSpace(r.SuspectDriver))
                     .GroupBy(r => r.SuspectDriver, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()))
        {
            _gridDrivers.Rows.Add(g.Key, g.Count());
        }
    }

    private void SetDefaultDiffRanges()
    {
        DateTime now = DateTime.Now;
        _dtpAStart.Value = now.AddDays(-60).Date;
        _dtpAEnd.Value = now.AddDays(-30).Date;
        _dtpBStart.Value = now.AddDays(-30).Date;
        _dtpBEnd.Value = now.Date;
    }

    // —— 柱状图 ——

    private void DrawChart()
    {
        var g = _chartPanel.CreateGraphics();
        try
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _chartPanel.ClientRectangle;
            g.Clear(Color.White);

            int n = _daily.Count;
            if (n == 0) return;
            int max = Math.Max(1, _daily.Max(d => d.count));

            var plot = new RectangleF(rect.X + 30, rect.Y + 8, rect.Width - 40, rect.Height - 42);
            if (plot.Width <= 10 || plot.Height <= 10) return;

            // 轴
            using var axisPen = new Pen(Color.FromArgb(203, 213, 225));
            g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);

            // Y 轴刻度
            using var tickFont = new Font("Consolas", 7.5f);
            using var tickBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
            for (int v = 0; v <= max; v += Math.Max(1, max / 4))
            {
                float y = plot.Bottom - (float)v / max * plot.Height;
                g.DrawString(v.ToString(), tickFont, tickBrush, rect.Left + 4, y - 7);
            }

            float barW = plot.Width / n;
            float barDrawW = Math.Max(2, barW - 2);

            bool todayDrawn = false;
            using var dateFont = new Font("Consolas", 7f);
            for (int i = 0; i < n; i++)
            {
                var (day, count) = _daily[i];
                float h = count == 0 ? 0 : Math.Max(2f, (float)count / max * plot.Height);
                if (count > 0)
                {
                    float x = plot.Left + i * barW + 1;
                    bool isToday = day == DateTime.Today;
                    using var barBrush = new SolidBrush(isToday
                        ? Color.FromArgb(0x1d, 0x4e, 0xd9)
                        : Color.FromArgb(0x7c, 0xa8, 0xe8));
                    g.FillRectangle(barBrush, x, plot.Bottom - h, barDrawW, h);
                }

                // X 轴日期：每 5 天 + 最后一天（今天）
                if (i % 5 == 0 || (i == n - 1 && !todayDrawn))
                {
                    float x = plot.Left + i * barW;
                    g.DrawString(day.ToString("MM-dd"), dateFont, tickBrush, x, plot.Bottom + 4);
                    todayDrawn |= i == n - 1;
                }
            }
        }
        finally
        {
            g.Dispose();
        }
    }

    // —— 报告对比 ——

    private void RunDiff()
    {
        DateTime aStart = _dtpAStart.Value.Date;
        DateTime aEnd = _dtpAEnd.Value.Date.AddDays(1); // 含结束当天
        DateTime bStart = _dtpBStart.Value.Date;
        DateTime bEnd = _dtpBEnd.Value.Date.AddDays(1);
        if (aStart >= aEnd || bStart >= bEnd)
        {
            MessageBox.Show(this, "时段起止时间不合法（结束日期需晚于开始日期）。", "对比错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var records = HistoryStore.Load();
        var listA = records.Where(r => r.Time >= aStart && r.Time < aEnd).ToList();
        var listB = records.Where(r => r.Time >= bStart && r.Time < bEnd).ToList();

        _tbDiff.Clear();
        AppendLine(_tbDiff, $"时段A：{aStart:yyyy-MM-dd} ~ {_dtpAEnd.Value:yyyy-MM-dd}（{listA.Count} 次崩溃）", Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
        AppendLine(_tbDiff, $"时段B：{bStart:yyyy-MM-dd} ~ {_dtpBEnd.Value:yyyy-MM-dd}（{listB.Count} 次崩溃）", Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
        AppendLine(_tbDiff, "", Color.Black);

        if (listA.Count == 0 && listB.Count == 0)
        {
            AppendLine(_tbDiff, "两个时段都没有崩溃记录。", Color.Gray);
            return;
        }

        // 总体结论
        if (listA.Count == 0)
            AppendLine(_tbDiff, $"✅→❌ 时段A无崩溃，时段B出现 {listB.Count} 次：情况恶化", Color.FromArgb(0x9f, 0x12, 0x3a), bold: true);
        else if (listB.Count == 0)
            AppendLine(_tbDiff, $"✅ 时段B无崩溃（时段A有 {listA.Count} 次）：问题消失", Color.FromArgb(0x04, 0x78, 0x57), bold: true);
        else if (listB.Count < listA.Count)
            AppendLine(_tbDiff, $"📉 崩溃频率下降 {listA.Count} → {listB.Count}：在好转", Color.FromArgb(0x04, 0x78, 0x57), bold: true);
        else if (listB.Count > listA.Count)
            AppendLine(_tbDiff, $"📈 崩溃频率上升 {listA.Count} → {listB.Count}：在恶化", Color.FromArgb(0x9f, 0x12, 0x3a), bold: true);
        else
            AppendLine(_tbDiff, $"➖ 崩溃频率持平（{listA.Count} 次）", Color.FromArgb(0xa1, 0x62, 0x07), bold: true);

        AppendLine(_tbDiff, "", Color.Black);

        // 停止代码对比
        AppendLine(_tbDiff, "── 停止代码变化 ──", Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
        DiffDimension(_tbDiff,
            listA.Where(r => r.BugCheckCode > 0).Select(r => $"0x{r.BugCheckCode:X} {r.StopCode}"),
            listB.Where(r => r.BugCheckCode > 0).Select(r => $"0x{r.BugCheckCode:X} {r.StopCode}"));

        AppendLine(_tbDiff, "", Color.Black);

        // 嫌疑驱动对比
        AppendLine(_tbDiff, "── 嫌疑驱动变化 ──", Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
        DiffDimension(_tbDiff,
            listA.Where(r => !string.IsNullOrWhiteSpace(r.SuspectDriver)).Select(r => r.SuspectDriver),
            listB.Where(r => !string.IsNullOrWhiteSpace(r.SuspectDriver)).Select(r => r.SuspectDriver));
    }

    /// <summary>
    /// 对某个维度（停止代码或驱动）做 A/B 分布 diff 输出
    /// </summary>
    private static void DiffDimension(RichTextBox tb, IEnumerable<string> a, IEnumerable<string> b)
    {
        var countA = a.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var countB = b.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        bool any = false;

        // B 中新增
        foreach (var kv in countB.Where(kv => !countA.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value))
        {
            AppendLine(tb, $"  ❌ 新增   {kv.Key} × {kv.Value}（A 时段没有，恶化信号）", Color.FromArgb(0x9f, 0x12, 0x3a));
            any = true;
        }
        // A 中消失
        foreach (var kv in countA.Where(kv => !countB.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value))
        {
            AppendLine(tb, $"  ✅ 消失   {kv.Key} × {kv.Value}（B 时段未再出现，好转信号）", Color.FromArgb(0x04, 0x78, 0x57));
            any = true;
        }
        // 两边都有：次数变化
        foreach (var kv in countB.Where(kv => countA.TryGetValue(kv.Key, out int ca) && kv.Value != ca)
                     .OrderByDescending(kv => Math.Abs(kv.Value - countA[kv.Key])))
        {
            int ca = countA[kv.Key];
            bool worse = kv.Value > ca;
            string arrow = worse ? "↑ 恶化" : "↓ 好转";
            AppendLine(tb, $"  {(worse ? "⚠" : "✅")} 变化   {kv.Key}  {ca} → {kv.Value}  {arrow}",
                worse ? Color.FromArgb(0x9f, 0x12, 0x3a) : Color.FromArgb(0x04, 0x78, 0x57));
            any = true;
        }

        if (!any)
            AppendLine(tb, "  （两时段分布一致，无变化）", Color.Gray);
    }

    private static void AppendLine(RichTextBox tb, string text, Color color, bool bold = false)
    {
        int start = tb.TextLength;
        tb.AppendText(text + "\n");
        tb.Select(start, tb.TextLength - start);
        tb.SelectionColor = color;
        tb.SelectionFont = MainForm.GetCachedFont(tb.Font, tb.Font.Size, bold);
        tb.SelectionStart = tb.TextLength;
        tb.SelectionLength = 0;
    }
}
