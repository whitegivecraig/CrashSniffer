using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// CrashSniffer 主窗体：顶部时间范围工具栏 + 左列表 + 右详情 Tab + 底部状态栏
/// </summary>
public class MainForm : Form
{
    // —— 控件 ——
    private readonly ToolStrip _topToolStrip = new();
    private readonly ToolStripLabel _lblStart = new() { Text = "开始:", Margin = new Padding(6, 2, 2, 2) };
    private ToolStripDateTimePicker _dtpStart = null!;
    private readonly ToolStripLabel _lblEnd = new() { Text = " 至:", Margin = new Padding(6, 2, 2, 2) };
    private ToolStripDateTimePicker _dtpEnd = null!;
    private readonly ToolStripButton _btnToday = new("今天") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
    private readonly ToolStripButton _btn7d = new("近7天") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
    private readonly ToolStripButton _btn30d = new("近30天") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
    private readonly ToolStripButton _btnMonth = new("本月") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
    private readonly ToolStripButton _btnAll = new("全部") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
    private readonly ToolStripSeparator _sep1 = new();
    private readonly ToolStripButton _btnRefresh = new("🔄 刷新扫描") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
    private readonly ToolStripButton _btnExport = new("📦 导出报告包") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };

    private readonly SplitContainer _split = new() { Orientation = Orientation.Vertical, Dock = DockStyle.Fill, SplitterDistance = 460, FixedPanel = FixedPanel.Panel1 };
    private readonly TabControl _rootTabs = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9) };
    private readonly TabPage _tabEvents = new("崩溃事件") { BackColor = Color.FromArgb(248, 250, 252) };
    private readonly TabPage _tabHistory = new("历史趋势") { BackColor = Color.FromArgb(248, 250, 252) };
    private readonly TabPage _tabEnvHistory = new("环境变更") { BackColor = Color.FromArgb(248, 250, 252) };
    private readonly EnvironmentHistoryTab _envHistoryTab = new();
    private readonly HistoryTab _historyTab = new();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, ReadOnly = true, RowHeadersVisible = false, BackgroundColor = Color.FromArgb(245, 248, 252), GridColor = Color.FromArgb(220, 228, 238), AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(252, 254, 255) } };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9) };
    private readonly TabPage _tabBsod = new("BSOD / 停止代码") { BackColor = Color.White };
    private readonly TabPage _tabWhea = new("WHEA / GPU") { BackColor = Color.White };
    private readonly TabPage _tabSuggest = new("排障建议") { BackColor = Color.White };
    private readonly TabPage _tabRelated = new("关联事件") { BackColor = Color.White };
    private readonly TabPage _tabRaw = new("原始消息") { BackColor = Color.White };
    private readonly TabPage _tabChanges = new("事发前变更") { BackColor = Color.White };
    private readonly RichTextBox _tbChanges = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.White, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical };

    private readonly RichTextBox _tbBsod = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10), BackColor = Color.White, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _tbWhea = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10), BackColor = Color.White, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _tbSuggest = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.White, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly DataGridView _gridRelated = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, ReadOnly = true, RowHeadersVisible = false, BackgroundColor = Color.White, GridColor = Color.FromArgb(220, 228, 238) };
    private readonly RichTextBox _tbRaw = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10), BackColor = Color.White, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical };

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblStatus = new("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _lblCount = new("崩溃事件: 0") { BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched, Padding = new Padding(10, 3, 6, 3) };
    private readonly ToolStripStatusLabel _lblDump = new("Dumps: 0") { BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched, Padding = new Padding(10, 3, 6, 3) };
    private readonly ToolStripStatusLabel _lblLog = new("事件: 0") { BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched, Padding = new Padding(10, 3, 6, 3) };

    // —— 数据 ——
    private List<CrashEvent> _events = new();
    private int _dumpCount;
    private int _logCount;
    private int _liveCount;
    /// <summary>最近一次扫描的环境快照（WMI 后台采集），导出报告时附带</summary>
    private EnvironmentSnapshot? _envSnapshot;
    /// <summary>环境变更记录缓存（详情页「事发前变更」关联展示用）</summary>
    private List<EnvironmentChange> _envChanges = new();
    /// <summary>SetQuickRange 期间抑制 DateTimePicker 的 ValueChanged 触发重复扫描</summary>
    private bool _suppressRangeEvents;
    private readonly Color _bsodColor = Color.FromArgb(0x1d, 0x4e, 0xd9);
    private readonly Color _wheaColor = Color.FromArgb(0xc2, 0x41, 0x0c);
    private readonly Color _tdrColor = Color.FromArgb(0x6d, 0x28, 0xd9);
    private readonly Color _kp41Color = Color.FromArgb(0xa1, 0x62, 0x07);
    private readonly Color _s6008Color = Color.FromArgb(0x47, 0x55, 0x69);
    private readonly Color _liveColor = Color.FromArgb(0x0e, 0x74, 0x93);

    public MainForm()
    {
        Text = "CrashSniffer · Windows 系统崩溃信息抓取工具";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 640);
        Size = new Size(1440, 880);
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.FromArgb(248, 250, 252);
        Icon = IconFromFont();

        BuildUi();
        BindEvents();

        // 启动时加载环境变更缓存（供详情页关联展示）
        try { _envChanges = EnvironmentHistoryStore.LoadChanges(); } catch { }

        // 默认近30天
        SetQuickRange(TimePreset.Last30Days);
        // 启动后自动刷新一次
        _ = AutoFirstScan();
    }

    private void BuildUi()
    {
        // ToolStrip
        _dtpStart = new ToolStripDateTimePicker
        {
            Width = 160,
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm"
        };
        _dtpEnd = new ToolStripDateTimePicker
        {
            Width = 160,
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm"
        };

        _topToolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _topToolStrip.Stretch = true;
        _topToolStrip.Font = new Font("Microsoft YaHei UI", 9);
        _topToolStrip.Items.AddRange(new ToolStripItem[]
        {
            _lblStart, _dtpStart, _lblEnd, _dtpEnd,
            new ToolStripSeparator(),
            _btnToday, _btn7d, _btn30d, _btnMonth, _btnAll,
            _sep1, _btnRefresh, new ToolStripSeparator(), _btnExport,
        });
        _btnExport.Enabled = false;

        // 快速按钮颜色
        foreach (var b in new[] { _btnToday, _btn7d, _btn30d, _btnMonth, _btnAll })
            b.ForeColor = Color.FromArgb(0x1d, 0x4e, 0xd9);
        _btnRefresh.ForeColor = Color.FromArgb(0x04, 0x78, 0x57);
        _btnExport.ForeColor = Color.FromArgb(0x83, 0x18, 0xb4);

        // DataGrid 列
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColTime",
            HeaderText = "时间",
            Width = 145,
            DataPropertyName = "Time",
            DefaultCellStyle = new DataGridViewCellStyle { Format = "MM-dd HH:mm:ss", Font = new Font("Consolas", 9) }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColType",
            HeaderText = "类型",
            Width = 140
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColCode",
            HeaderText = "停止代码",
            Width = 150,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9) }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ColDriver",
            HeaderText = "嫌疑驱动",
            Width = 190,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Consolas", 9) }
        });

        // 详情 Tab
        _tabBsod.Controls.Add(_tbBsod);
        _tabWhea.Controls.Add(_tbWhea);
        _tabSuggest.Controls.Add(_tbSuggest);
        _tabRelated.Controls.Add(_gridRelated);
        _tabRaw.Controls.Add(_tbRaw);
        _tabChanges.Controls.Add(_tbChanges);
        _tabs.TabPages.AddRange(new[] { _tabBsod, _tabWhea, _tabSuggest, _tabChanges, _tabRelated, _tabRaw });

        // 关联事件列
        _gridRelated.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", Width = 95, DataPropertyName = "Time", DefaultCellStyle = new DataGridViewCellStyle { Format = "HH:mm:ss.fff", Font = new Font("Consolas", 9) } });
        _gridRelated.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "级别", Width = 70 });
        _gridRelated.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Provider", Width = 170 });
        _gridRelated.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", Width = 60 });
        _gridRelated.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "消息", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        // 组装容器：根 Tab = 崩溃事件页 + 历史趋势页
        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_tabs);
        _tabEvents.Controls.Add(_split);
        _tabHistory.Controls.Add(_historyTab);
        _tabEnvHistory.Controls.Add(_envHistoryTab);
        _rootTabs.TabPages.AddRange(new[] { _tabEvents, _tabHistory, _tabEnvHistory });

        Controls.Add(_rootTabs);
        Controls.Add(_topToolStrip);
        Controls.Add(_status);

        _status.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblCount, _lblDump, _lblLog });
    }

    private void BindEvents()
    {
        _btnToday.Click += (_, _) => { SetQuickRange(TimePreset.Today); RefreshScan(); };
        _btn7d.Click += (_, _) => { SetQuickRange(TimePreset.Last7Days); RefreshScan(); };
        _btn30d.Click += (_, _) => { SetQuickRange(TimePreset.Last30Days); RefreshScan(); };
        _btnMonth.Click += (_, _) => { SetQuickRange(TimePreset.ThisMonth); RefreshScan(); };
        _btnAll.Click += (_, _) => { SetQuickRange(TimePreset.All); RefreshScan(); };
        _btnRefresh.Click += (_, _) => RefreshScan();
        _btnExport.Click += (_, _) => DoExport();

        _dtpStart.ValueChanged += (_, _) => { if (!_suppressRangeEvents) RefreshScan(); };
        _dtpEnd.ValueChanged += (_, _) => { if (!_suppressRangeEvents) RefreshScan(); };

        _grid.SelectionChanged += (_, _) => UpdateDetail();
    }

    private enum TimePreset { Today, Last7Days, Last30Days, ThisMonth, All }

    private void SetQuickRange(TimePreset preset)
    {
        DateTime now = DateTime.Now;
        DateTime start, end = now;
        switch (preset)
        {
            case TimePreset.Today:
                start = now.Date;
                break;
            case TimePreset.Last7Days:
                start = now.AddDays(-7).Date;
                break;
            case TimePreset.Last30Days:
                start = now.AddDays(-30).Date;
                break;
            case TimePreset.ThisMonth:
                start = new DateTime(now.Year, now.Month, 1);
                break;
            case TimePreset.All:
            default:
                // DateTime.MinValue(0001年) 低于 DateTimePicker.MinDate(1753年) 会抛
                // ArgumentOutOfRangeException，这里用控件自身的边界值
                start = DateTimePicker.MinimumDateTime;
                end = DateTimePicker.MaximumDateTime;
                break;
        }
        _suppressRangeEvents = true;
        try
        {
            _dtpStart.Value = start;
            _dtpEnd.Value = end;
        }
        finally
        {
            _suppressRangeEvents = false;
        }
    }

    private async Task AutoFirstScan()
    {
        await Task.Delay(200);
        RefreshScan();
    }

    private async void RefreshScan()
    {
        DateTime start = _dtpStart.Value;
        DateTime end = _dtpEnd.Value;
        if (start > end)
        {
            MessageBox.Show(this, "开始时间不能晚于结束时间。", "时间范围错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, "正在扫描 minidump、系统事件日志和 LiveKernelReports…");
        _btnExport.Enabled = false;
        _envSnapshot = null;

        try
        {
            var task = Task.Run(() => CrashAggregator.CollectAndAggregate(start, end));
            await task;

            (_events, _dumpCount, _logCount, _liveCount) = task.Result;

            LoadGrid();
            _btnExport.Enabled = _events.Count > 0;
            _lblCount.Text = $"崩溃事件: {_events.Count}";
            _lblDump.Text = $"Dumps: {_dumpCount}";
            _lblLog.Text = $"事件: {_logCount}";
            SetBusy(false, $"扫描完成，共 {_events.Count} 条崩溃事件 (Minidump {_dumpCount} / 事件 {_logCount} / LiveKernel {_liveCount})");

            // 后台：写入历史存档 + 采集环境快照（均不阻塞 UI）
            _ = Task.Run(() =>
            {
                try { HistoryStore.AppendAndSave(_events); } catch { /* 历史失败不影响主流程 */ }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    var snap = await Task.Run(() => CrashSniffer.Collectors.EnvironmentCollector.Collect());
                    _envSnapshot = snap;
                    int newChanges = EnvironmentHistoryStore.RecordSnapshot(snap);
                    _envChanges = EnvironmentHistoryStore.LoadChanges();
                    if (InvokeRequired) BeginInvoke(() => _envHistoryTab.OnScanCompleted());
                    else _envHistoryTab.OnScanCompleted();
                    SetStatusText(newChanges > 0
                        ? $"环境快照采集完成：检测到 {newChanges} 项环境变更，见「环境变更」页"
                        : $"环境快照采集完成：{snap.Gpus.Count} 个 GPU · 内存诊断：{snap.MemoryDiag.Display}");
                }
                catch { /* 快照失败不影响主流程 */ }
            });

            // 历史趋势页同步刷新
            _historyTab.OnScanCompleted();
        }
        catch (Exception ex)
        {
            SetBusy(false, "扫描失败");
            MessageBox.Show(this, $"扫描过程出错：\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatusText(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => _lblStatus.Text = text); }
        else _lblStatus.Text = text;
    }

    private void LoadGrid()
    {
        _grid.Rows.Clear();
        foreach (var ev in _events)
        {
            int idx = _grid.Rows.Add();
            var row = _grid.Rows[idx];
            row.Cells[0].Value = ev.Time;
            row.Cells[1].Value = TypeLabel(ev.Type);
            row.DefaultCellStyle.ForeColor = ColorForType(ev.Type);
            row.DefaultCellStyle.SelectionForeColor = Color.White;
            row.DefaultCellStyle.SelectionBackColor = ColorForType(ev.Type);
            row.Cells[1].Style.ForeColor = ColorForType(ev.Type);
            row.Cells[2].Value = ev.BugCheckCode > 0 ? $"0x{ev.BugCheckCode:X} {ev.StopCode}" : "-";
            row.Cells[3].Value = string.IsNullOrWhiteSpace(ev.SuspectDriver) ? "-" : Truncate(StripBracket(ev.SuspectDriver), 22);
            row.Tag = ev;
        }
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
    }

    private void UpdateDetail()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not CrashEvent ev)
        {
            ClearDetail();
            return;
        }

        // BSOD / Stop code
        var b = _tbBsod;
        b.Clear();
        b.Font = new Font("Consolas", 10);
        AppendLine(b, $"■ 时间        {ev.Time:yyyy-MM-dd HH:mm:ss}", ColorForType(ev.Type), bold: true);
        AppendLine(b, $"■ 类型        {TypeLabel(ev.Type)}", Color.Black);
        AppendLine(b, $"■ 摘要        {ev.Summary}", Color.FromArgb(0x1f, 0x29, 0x37));
        if (ev.BugCheckCode != 0)
        {
            AppendLine(b, $"", Color.Black);
            AppendLine(b, $"停止代码     0x{ev.BugCheckCode:X8}", Color.FromArgb(0x7c, 0x2d, 0x14), bold: true);
            AppendLine(b, $"符号名       {ev.StopCode}", Color.Black);
            AppendLine(b, $"解释         {ev.StopCodeChinese}", Color.Black);
            AppendLine(b, "", Color.Black);
            AppendLine(b, "BugCheck 参数：", Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
            for (int i = 0; i < ev.BugCheckParams.Length; i++)
                AppendLine(b, $"  P{i}  =  0x{ev.BugCheckParams[i]:X16}", Color.Black);
        }
        if (!string.IsNullOrWhiteSpace(ev.DumpPath))
        {
            AppendLine(b, "", Color.Black);
            AppendLine(b, "Dump 文件：", Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
            AppendLine(b, $"  {ev.DumpPath}", Color.Black);
            if (ev.DumpSize > 0)
                AppendLine(b, $"  大小        {ev.DumpSize / 1024 / 1024} MB", Color.Black);
        }
        if (!string.IsNullOrWhiteSpace(ev.SuspectDriver))
        {
            AppendLine(b, "", Color.Black);
            AppendLine(b, "嫌疑驱动：", Color.FromArgb(0x9f, 0x12, 0x3a), bold: true);
            AppendLine(b, $"  {ev.SuspectDriver}", Color.FromArgb(0x7c, 0x2d, 0x14));
        }

        // WHEA / GPU
        var w = _tbWhea;
        w.Clear();
        bool hasWhea = false;
        if (!string.IsNullOrWhiteSpace(ev.WheaErrorType) || !string.IsNullOrWhiteSpace(ev.WheaLocation))
        {
            hasWhea = true;
            AppendLine(w, $"WHEA 错误类型：{ev.WheaErrorType}", Color.FromArgb(0xc2, 0x41, 0x0c), bold: true);
            AppendLine(w, $"出错位置：{ev.WheaLocation}", Color.Black);
            if (!string.IsNullOrWhiteSpace(ev.WheaHardwarePart))
                AppendLine(w, $"出错部位：→ {ev.WheaHardwarePart}", Color.FromArgb(0x9f, 0x12, 0x3a), bold: true);
        }
        if (!string.IsNullOrWhiteSpace(ev.GpuVendor))
        {
            AppendLine(w, $"", Color.Black);
            AppendLine(w, $"GPU 厂商：{ev.GpuVendor}", Color.FromArgb(0x6d, 0x28, 0xd9), bold: true);
        }
        if (!hasWhea && string.IsNullOrWhiteSpace(ev.GpuVendor))
        {
            AppendLine(w, "该崩溃事件无可显示的 WHEA / GPU 信息。", Color.Gray);
        }

        // 建议
        var s = _tbSuggest;
        s.Clear();
        if (ev.Troubleshooting.Count == 0)
        {
            AppendLine(s, "暂无针对性建议。\n建议前往 关联事件 或 原始消息 Tab 查看细节。", Color.Gray);
        }
        else
        {
            AppendLine(s, "💡 针对性排障建议", Color.FromArgb(0x1d, 0x4e, 0xd9), bold: true, 14);
            AppendLine(s, "─────────────────────────────", Color.Gray);
            for (int i = 0; i < ev.Troubleshooting.Count; i++)
            {
                AppendLine(s, $"{i + 1,2}.  {ev.Troubleshooting[i]}", Color.FromArgb(0x1f, 0x29, 0x37));
            }
        }

        // 关联事件 grid
        _gridRelated.Rows.Clear();
        foreach (var r in ev.RelatedEvents)
        {
            int i = _gridRelated.Rows.Add();
            var row = _gridRelated.Rows[i];
            row.Cells[0].Value = r.Time;
            row.Cells[1].Value = r.Level;
            row.Cells[2].Value = r.Provider;
            row.Cells[3].Value = r.EventId;
            row.Cells[4].Value = Truncate(r.Message, 400);
            if (r.Level == "Error" || r.Level == "Critical")
                row.DefaultCellStyle.ForeColor = Color.FromArgb(0x9f, 0x12, 0x3a);
            else if (r.Level == "Warning")
                row.DefaultCellStyle.ForeColor = Color.FromArgb(0xc2, 0x41, 0x0c);
        }

        // 事发前变更
        var ch = _tbChanges;
        ch.Clear();
        var before = _envChanges.Where(c => c.Time <= ev.Time).OrderByDescending(c => c.Time).ToList();
        if (before.Count == 0)
        {
            AppendLine(ch, _envChanges.Count == 0
                ? "尚无环境变更记录（从首次扫描起开始记录）。\n定期扫描后，崩溃前更换驱动 / 刷 BIOS / 改内存频率等变更会显示在这里。"
                : "该崩溃发生前未检测到环境变更。", Color.Gray);
        }
        else
        {
            AppendLine(ch, $"🕐 事发前环境变更（共 {before.Count} 项）", Color.FromArgb(0x1d, 0x4e, 0xd9), bold: true, 13);
            AppendLine(ch, "注：变更只能定位到两次扫描之间，精确时刻不可知。", Color.Gray);
            AppendLine(ch, "─────────────────────────────", Color.Gray);
            int show = Math.Min(10, before.Count);
            for (int i = 0; i < show; i++)
            {
                var c = before[i];
                AppendLine(ch, $"{EnvironmentChangeDetector.RelativeBefore(ev.Time, c.Time)} · {EnvironmentChangeDetector.CategoryLabel(c.Category)}",
                    Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
                AppendLine(ch, $"  {c.Item}:  {c.OldValue}  →  {c.NewValue}", Color.FromArgb(0x1f, 0x29, 0x37));
            }
            if (before.Count > show)
                AppendLine(ch, $"…另有 {before.Count - show} 条，见「环境变更」页", Color.Gray);
        }

        // 原始消息
        _tbRaw.Clear();
        if (string.IsNullOrWhiteSpace(ev.EventMessage))
        {
            _tbRaw.AppendText("(无原始事件消息)");
        }
        else
        {
            _tbRaw.AppendText(ev.EventMessage);
        }
    }

    private void ClearDetail()
    {
        foreach (var tb in new[] { _tbBsod, _tbWhea, _tbSuggest, _tbRaw, _tbChanges }) tb.Clear();
        _gridRelated.Rows.Clear();
    }

    private async void DoExport()
    {
        if (_events.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            FileName = $"CrashReport_{DateTime.Now:yyyyMMdd_HHmm}.zip",
            Filter = "Zip 压缩报告 (*.zip)|*.zip",
            DefaultExt = "zip",
            Title = "导出崩溃报告包",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SetBusy(true, $"正在导出报告包 → {dlg.FileName}");
        try
        {
            // 导出前若环境快照尚未生成（后台还在采集），现场补采一次
            EnvironmentSnapshot? env = _envSnapshot;
            if (env == null)
            {
                SetBusy(true, "正在采集环境快照…");
                env = await Task.Run(() => CrashSniffer.Collectors.EnvironmentCollector.Collect());
                _envSnapshot = env;
                SetBusy(true, $"正在导出报告包 → {dlg.FileName}");
            }
            var ranking = CrashAggregator.BuildSuspectRanking(_events);

            EnvironmentHistoryData envHistory;
            try { envHistory = EnvironmentHistoryStore.Load(); } catch { envHistory = new EnvironmentHistoryData(); }
            var result = ReportExporter.Export(dlg.FileName, _events, _dtpStart.Value, _dtpEnd.Value, env, ranking, envHistory);
            SetBusy(false, result.Success ? $"报告包导出成功：{dlg.FileName}" : $"导出失败：{result.ErrorMessage}");

            if (!result.Success)
            {
                MessageBox.Show(this, $"导出失败：\n{result.ErrorMessage}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string msg = $"报告包已生成：\n{dlg.FileName}\n\n包含文件：\n- {result.FilesIncluded.Count} 个文件 (HTML + JSON + Dumps)";
            if (result.SkippedDumps.Count > 0)
                msg += $"\n跳过 {result.SkippedDumps.Count} 个大文件 (超过500MB的dump未包含)\n";
            var res = MessageBox.Show(this, msg + "\n\n是否立即打开所在文件夹？",
                "导出成功", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (res == DialogResult.Yes)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\""); }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            SetBusy(false, "导出失败");
            MessageBox.Show(this, "导出异常：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // —— 通用工具 ——

    /// <summary>SelectionFont 缓存：RichTextBox 每行赋 Font 不释放会泄漏 GDI 句柄，统一缓存复用</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string family, float size, bool bold), Font> _fontCache = new();

    internal static Font GetCachedFont(Font template, float size, bool bold)
    {
        var key = (template.FontFamily.Name, size, bold);
        return _fontCache.GetOrAdd(key, k => new Font(k.family, k.size, bold ? FontStyle.Bold : FontStyle.Regular));
    }

    private void SetBusy(bool busy, string statusText)
    {
        _btnRefresh.Enabled = !busy;
        _btnExport.Enabled = !busy && _events.Count > 0;
        foreach (ToolStripItem it in _topToolStrip.Items)
            if (it is ToolStripButton) it.Enabled = busy ? false : true;
        _btnRefresh.Enabled = !busy;
        _lblStatus.Text = statusText;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private static string TypeLabel(CrashType t) => t switch
    {
        CrashType.BSOD => "🟦 BSOD 蓝屏",
        CrashType.WHEA_Error => "🔥 WHEA 硬件错误",
        CrashType.GPU_TDR => "🎮 GPU TDR",
        CrashType.KernelPower_41 => "⚡ Power-41",
        CrashType.Shutdown_6008 => "🔌 关机6008",
        CrashType.LiveKernel => "🧩 LiveKernel挂死",
        _ => t.ToString(),
    };

    private Color ColorForType(CrashType t) => t switch
    {
        CrashType.BSOD => _bsodColor,
        CrashType.WHEA_Error => _wheaColor,
        CrashType.GPU_TDR => _tdrColor,
        CrashType.KernelPower_41 => _kp41Color,
        CrashType.LiveKernel => _liveColor,
        _ => _s6008Color,
    };

    private static void AppendLine(RichTextBox tb, string text, Color? color = null, bool bold = false, int fontSize = 0)
    {
        int start = tb.TextLength;
        tb.AppendText(text + "\n");
        tb.Select(start, tb.TextLength - start);
        if (color.HasValue) tb.SelectionColor = color.Value;
        tb.SelectionFont = GetCachedFont(tb.Font, fontSize > 0 ? fontSize : tb.Font.Size, bold);
        tb.SelectionStart = tb.TextLength;
        tb.SelectionLength = 0;
    }

    private static string StripBracket(string s)
    {
        int i = s.IndexOf('[');
        return i > 0 ? s.Substring(0, i).Trim() : s;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }

    private static Icon? IconFromFont()
    {
        try
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(new SolidBrush(Color.FromArgb(0x1d, 0x4e, 0xd9)), 0, 0, 64, 64);
                g.DrawString("🪦", new Font("Segoe UI Emoji", 28), Brushes.White, new RectangleF(0, 6, 64, 52),
                    new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// ToolStrip 中嵌入 DateTimePicker 控件
/// </summary>
public class ToolStripDateTimePicker : ToolStripControlHost
{
    public DateTimePicker Dtp => (DateTimePicker)Control;
    public ToolStripDateTimePicker() : base(new DateTimePicker()) { }
    public DateTime Value { get => Dtp.Value; set => Dtp.Value = value; }
    public event EventHandler ValueChanged
    {
        add { Dtp.ValueChanged += value; }
        remove { Dtp.ValueChanged -= value; }
    }
    public DateTimePickerFormat Format
    {
        get => Dtp.Format;
        set => Dtp.Format = value;
    }
    public string CustomFormat
    {
        get => Dtp.CustomFormat;
        set => Dtp.CustomFormat = value;
    }
}
