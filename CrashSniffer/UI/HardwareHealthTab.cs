using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// "硬件健康"标签页：左侧类别导航（CPU / GPU / 内存 / 电压 / 磁盘 / 主板），
/// 选中后在右侧查看该类别的详细读数；数据来自 HardwareMonitorService，2s 刷新。
/// 未提权/传感器不可用时显示降级占位。
/// </summary>
public class HardwareHealthTab : UserControl
{
    private enum HealthCategory
    {
        Cpu = 0,
        Gpu = 1,
        Memory = 2,
        Voltage = 3,
        Disk = 4,
        Motherboard = 5,
    }

    private static readonly string[] CategoryNames = { "CPU", "GPU", "内存", "电压", "磁盘", "主板" };
    private static readonly string[] CategoryIcons = { "🖥️", "🎮", "🧠", "⚡", "💾", "🌡️" };

    private readonly Label _lblStatus = new()
    {
        Text = "🔴 未采集到硬件数据（需管理员权限）",
        AutoSize = true,
        ForeColor = Color.FromArgb(0xdc, 0x26, 0x26),
        Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
    };

    private readonly ListBox _nav = new()
    {
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 46,
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        IntegralHeight = false,
        Font = new Font("Microsoft YaHei UI", 9),
    };

    private readonly Label _detailTitle = new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold),
        ForeColor = Color.FromArgb(0x1f, 0x29, 0x37),
    };

    private readonly Label _detailHint = new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9),
        ForeColor = Color.FromArgb(0x6b, 0x72, 0x80),
        Margin = new Padding(0, 4, 0, 10),
    };

    private readonly DataGridView _detailGrid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AllowUserToResizeColumns = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Color.White,
        GridColor = Color.FromArgb(226, 232, 240),
        BorderStyle = BorderStyle.None,
        Font = new Font("Microsoft YaHei UI", 10),
        RowTemplate = { Height = 34 },
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
    };

    private readonly ListView _drivesList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        Font = new Font("Microsoft YaHei UI", 9),
        UseCompatibleStateImageBehavior = false,
        ShowItemToolTips = true,
        Visible = false,
    };

    private readonly Dictionary<string, ListViewItem> _driveItems = new();
    private readonly HardwareHealth[] _navLevels = new HardwareHealth[CategoryNames.Length];
    private readonly string[] _navSummaries = new string[CategoryNames.Length];

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private HardwareMonitorService? _service;
    private HealthCategory _category = HealthCategory.Cpu;

    private readonly CheckBox _chkSampler = new()
    {
        Text = "开机后台采样（重启后仍可回放崩溃前读数）",
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9),
        ForeColor = Color.FromArgb(0x1f, 0x29, 0x37),
    };

    private readonly Label _lblSamplerInfo = new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9),
        ForeColor = Color.FromArgb(0x6b, 0x72, 0x80),
        Margin = new Padding(16, 6, 0, 0),
    };

    private bool _samplerEnabled;
    private bool _updatingSampler;

    private static readonly Color CardBack = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(226, 232, 240);
    private static readonly Color TextDark = Color.FromArgb(0x1f, 0x29, 0x37);
    private static readonly Color TextGray = Color.FromArgb(0x6b, 0x72, 0x80);
    private static readonly Font NavNameFont = new("Microsoft YaHei UI", 10, FontStyle.Bold);
    private static readonly Font NavSummaryFont = new("Microsoft YaHei UI", 8);

    public HardwareHealthTab()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(248, 250, 252);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(_lblStatus, 0, 0);

        var samplerRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 8),
        };
        samplerRow.Controls.Add(_chkSampler);
        samplerRow.Controls.Add(_lblSamplerInfo);
        root.Controls.Add(samplerRow, 0, 1);

        // —— 左：类别导航 ——
        var navCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Padding = new Padding(1),
            Margin = new Padding(0, 0, 10, 0),
        };
        var navInner = new Panel { Dock = DockStyle.Fill, BackColor = CardBack, Padding = new Padding(0) };
        var navHeader = new Label
        {
            Text = "监控类别",
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
            ForeColor = TextGray,
        };
        _nav.Items.AddRange(CategoryNames);
        _nav.SelectedIndex = 0;
        _nav.DrawItem += Nav_DrawItem;
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (_nav.SelectedIndex < 0) return;
            _category = (HealthCategory)_nav.SelectedIndex;
            RebuildDetail();
            var reading = _service?.LatestReading;
            if (reading != null) UpdateDetail(reading);
        };
        navInner.Controls.Add(_nav);
        navInner.Controls.Add(navHeader);
        navCard.Controls.Add(navInner);

        // —— 右：类别详情 ——
        var detailCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Padding = new Padding(16),
        };
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailLayout.Controls.Add(_detailTitle, 0, 0);
        detailLayout.Controls.Add(_detailHint, 0, 1);

        var nameColumn = new DataGridViewTextBoxColumn
        {
            HeaderText = "指标",
            Width = 170,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var valueColumn = new DataGridViewTextBoxColumn
        {
            HeaderText = "当前值",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var stateColumn = new DataGridViewTextBoxColumn
        {
            HeaderText = "状态",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter },
        };
        _detailGrid.Columns.AddRange(nameColumn, valueColumn, stateColumn);
        _detailGrid.EnableHeadersVisualStyles = false;
        _detailGrid.ColumnHeadersHeight = 32;
        _detailGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(241, 245, 249),
            ForeColor = TextGray,
            Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
            SelectionBackColor = Color.FromArgb(241, 245, 249),
            SelectionForeColor = TextGray,
        };
        _detailGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = TextDark,
            SelectionBackColor = Color.FromArgb(232, 240, 255),
            SelectionForeColor = TextDark,
            Padding = new Padding(6, 0, 6, 0),
        };
        _detailGrid.RowsDefaultCellStyle = _detailGrid.DefaultCellStyle;

        _drivesList.Columns.Add("磁盘", 190);
        _drivesList.Columns.Add("盘符", 80);
        _drivesList.Columns.Add("温度", 70);
        _drivesList.Columns.Add("寿命", 70);
        _drivesList.Columns.Add("备用块", 70);
        _drivesList.Columns.Add("已用", 70);
        _drivesList.Columns.Add("读速", 85);
        _drivesList.Columns.Add("写速", 85);
        _drivesList.Columns.Add("累计写入", 95);

        var detailHost = new Panel { Dock = DockStyle.Fill };
        detailHost.Controls.Add(_detailGrid);
        detailHost.Controls.Add(_drivesList);
        detailLayout.Controls.Add(detailHost, 0, 2);
        detailCard.Controls.Add(detailLayout);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(navCard, 0, 0);
        content.Controls.Add(detailCard, 1, 0);
        root.Controls.Add(content, 0, 2);

        Controls.Add(root);
        RebuildDetail();

        _chkSampler.CheckedChanged += OnSamplerToggled;
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshReadings();
            UpdateSamplerInfoText();
        };
        Load += (_, _) =>
        {
            _refreshTimer.Start();
            RefreshSamplerStateAsync();
        };
        Disposed += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>绑定监控服务实例</summary>
    public void BindService(HardwareMonitorService service)
    {
        _service = service;
        RefreshReadings();
    }

    /// <summary>查询计划任务注册状态并同步勾选框（schtasks 放后台线程，避免卡 UI）</summary>
    private async void RefreshSamplerStateAsync()
    {
        bool enabled = await Task.Run(SamplerAutoStart.IsEnabled);
        _samplerEnabled = enabled;
        _updatingSampler = true;
        _chkSampler.Checked = enabled;
        _updatingSampler = false;
        UpdateSamplerInfoText();
    }

    private void UpdateSamplerInfoText()
    {
        DateTime? latest = ContinuousSampleStore.GetLatestSampleTime();
        string latestText = latest.HasValue
            ? $"最近写入 {latest.Value:MM-dd HH:mm:ss}"
            : "尚无采样数据";
        _lblSamplerInfo.Text = _samplerEnabled
            ? $"✅ 已启用 · {latestText} · 保留约 {ContinuousSampleStore.MaxFiles} 小时"
            : $"未启用（重启后无法回放崩溃前读数）· {latestText}";
    }

    private async void OnSamplerToggled(object? sender, EventArgs e)
    {
        if (_updatingSampler) return;

        bool wanted = _chkSampler.Checked;
        _chkSampler.Enabled = false;
        _lblSamplerInfo.Text = wanted ? "正在注册开机采样任务…" : "正在注销开机采样任务…";

        string error = string.Empty;
        bool ok = await Task.Run(() => wanted
            ? SamplerAutoStart.Enable(Application.ExecutablePath, out error)
            : SamplerAutoStart.Disable(out error));

        _chkSampler.Enabled = true;
        if (!ok)
        {
            MessageBox.Show(FindForm(),
                (wanted ? "启用开机采样失败：" : "关闭开机采样失败：") + error,
                "后台连续采样", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            _updatingSampler = true;
            _chkSampler.Checked = !wanted;
            _updatingSampler = false;
            UpdateSamplerInfoText();
            return;
        }

        _samplerEnabled = wanted;
        UpdateSamplerInfoText();
    }

    private void RefreshReadings()
    {
        if (_service == null)
        {
            SetStatus(false, "🔴 监控服务未启动");
            ClearValues();
            return;
        }

        var r = _service.LatestReading;
        if (r == null)
        {
            SetStatus(false, "🔴 未采集到硬件数据（需管理员权限或传感器不可用）");
            ClearValues();
            return;
        }

        SetStatus(true, $"✅ 实时监控中 · 采样间隔 2s · 缓冲 {_service.GetBuffer().Length}/300 点");
        UpdateNav(r);
        UpdateDetail(r);
    }

    /// <summary>左侧导航的摘要与状态色</summary>
    private void UpdateNav(HardwareReading r)
    {
        var storages = r.Storages ?? new List<StorageReading>();
        var diskTemps = storages.Where(s => s.Temperature.HasValue).Select(s => s.Temperature!.Value).ToList();
        var diskLives = storages.Where(s => s.LifePercent.HasValue).Select(s => s.LifePercent!.Value).ToList();

        _navLevels[(int)HealthCategory.Cpu] = HardwareThresholds.EvaluateCpuTemp(r.CpuTemperature);
        _navSummaries[(int)HealthCategory.Cpu] = $"{Temp(r.CpuTemperature)} · {Pct(r.CpuLoad)}";

        _navLevels[(int)HealthCategory.Gpu] = Worst(
            HardwareThresholds.EvaluateGpuTemp(r.GpuTemperature),
            HardwareThresholds.EvaluateGpuHotspot(r.GpuHotspot),
            HardwareThresholds.EvaluateGpuMemoryTemp(r.GpuMemoryTemperature));
        _navSummaries[(int)HealthCategory.Gpu] = $"核心 {Temp(r.GpuTemperature)} · 显存 {Temp(r.GpuMemoryTemperature)}";

        var memLevel = Worst(
            HardwareThresholds.EvaluateMemoryLoad(r.MemoryLoad),
            HardwareThresholds.EvaluateMemoryLoad(r.VirtualMemoryLoad));
        _navLevels[(int)HealthCategory.Memory] = memLevel;
        _navSummaries[(int)HealthCategory.Memory] = $"占用 {Pct(r.MemoryLoad)} · 虚拟 {Pct(r.VirtualMemoryLoad)}";

        var voltLevel = Worst(
            HardwareThresholds.EvaluateVoltage12V(r.Voltage12V),
            HardwareThresholds.EvaluateVoltage5V(r.Voltage5V),
            HardwareThresholds.EvaluateVoltage3V(r.Voltage3V),
            HardwareThresholds.EvaluateVoltage3V(r.Voltage3VStandby),
            HardwareThresholds.EvaluateCmosBattery(r.CmosBatteryVoltage));
        _navLevels[(int)HealthCategory.Voltage] = voltLevel;
        _navSummaries[(int)HealthCategory.Voltage] = voltLevel == HardwareHealth.Unknown
            ? "—"
            : HardwareThresholds.LabelFor(voltLevel);

        var diskLevel = Worst(
            HardwareThresholds.EvaluateSsdTemp(diskTemps.Count > 0 ? diskTemps.Max() : null),
            HardwareThresholds.EvaluateSsdLife(diskLives.Count > 0 ? diskLives.Min() : null),
            WorstSpare(storages));
        _navLevels[(int)HealthCategory.Disk] = diskLevel;
        _navSummaries[(int)HealthCategory.Disk] = storages.Count == 0
            ? "未检测到"
            : diskTemps.Count > 0
                ? $"{storages.Count} 块 · 最高 {diskTemps.Max():F0} ℃"
                : $"{storages.Count} 块磁盘";

        _navLevels[(int)HealthCategory.Motherboard] = HardwareThresholds.EvaluateMoboTemp(r.MoboMaxTemperature);
        _navSummaries[(int)HealthCategory.Motherboard] = r.Fans.Count > 0
            ? $"{Temp(r.MoboMaxTemperature)} · {r.Fans.Count} 个风扇"
            : $"{Temp(r.MoboMaxTemperature)} · 风扇 {Rpm(r.CpuFanRpm)}";

        _nav.Invalidate();
    }

    /// <summary>右侧详情：磁盘类别显示逐盘列表，其余显示指标表格</summary>
    private void UpdateDetail(HardwareReading r)
    {
        if (_category == HealthCategory.Disk)
        {
            RefreshDrives(r);
            UpdateDiskHint(r);
            return;
        }

        var cells = BuildCells(_category, r);
        var names = RowNames(_category, r);
        if (_detailGrid.Rows.Count != names.Length)
        {
            _detailGrid.Rows.Clear();
            foreach (string name in names)
                _detailGrid.Rows.Add(name, "—", "—");
        }

        for (int i = 0; i < _detailGrid.Rows.Count && i < cells.Count; i++)
        {
            var cell = cells[i];
            _detailGrid.Rows[i].Cells[0].Value = names[i];
            var valueCell = _detailGrid.Rows[i].Cells[1];
            valueCell.Value = cell.Value;
            valueCell.Style.ForeColor = cell.Colorize
                ? HardwareThresholds.ColorFor(cell.Level)
                : TextDark;

            var stateCell = _detailGrid.Rows[i].Cells[2];
            stateCell.Value = cell.State;
            stateCell.Style.ForeColor = cell.Colorize
                ? HardwareThresholds.ColorFor(cell.Level)
                : TextGray;
        }
        _detailGrid.ClearSelection();
    }

    private void RebuildDetail()
    {
        string icon = CategoryIcons[(int)_category];
        _detailTitle.Text = $"{icon} {CategoryNames[(int)_category]}";
        _detailHint.Text = _category == HealthCategory.Disk
            ? "全部物理磁盘的实时状态，逐行按温度 / 寿命 / 备用块着色"
            : "实时读数 · 每 2 秒刷新 · 按厂商安全阈值着色";

        _detailGrid.Rows.Clear();
        if (_category != HealthCategory.Disk)
        {
            foreach (string name in RowNames(_category, _service?.LatestReading))
                _detailGrid.Rows.Add(name, "—", "—");
        }

        _detailGrid.Visible = _category != HealthCategory.Disk;
        _drivesList.Visible = _category == HealthCategory.Disk;
    }

    private void UpdateDiskHint(HardwareReading r)
    {
        var storages = r.Storages ?? new List<StorageReading>();
        if (storages.Count == 0)
        {
            _detailHint.Text = "未检测到磁盘（需管理员权限）";
            return;
        }

        var temps = storages.Where(s => s.Temperature.HasValue).Select(s => s.Temperature!.Value).ToList();
        var lives = storages.Where(s => s.LifePercent.HasValue).Select(s => s.LifePercent!.Value).ToList();
        var spares = storages.Where(s => s.AvailableSparePercent.HasValue).Select(s => s.AvailableSparePercent!.Value).ToList();
        string tempText = temps.Count > 0 ? $"最高温 {temps.Max():F0} ℃" : "温度 —";
        string lifeText = lives.Count > 0 ? $"最低寿命 {lives.Min():F0}%" : "寿命 —";
        string spareText = spares.Count > 0 ? $"最低备用块 {spares.Min():F0}%" : "备用块 —";
        _detailHint.Text = $"{storages.Count} 块磁盘 · {tempText} · {lifeText} · {spareText}";
    }

    private static string[] RowNames(HealthCategory category, HardwareReading? r) => category switch
    {
        HealthCategory.Cpu => CpuRowNames(r),
        HealthCategory.Gpu => new[]
        {
            "核心温度", "热点温度", "显存温度", "显存占用",
            "GPU 负载", "显存负载", "核心频率", "显存频率", "功耗",
            "风扇转速", "风扇转速百分比", "核心电压",
        },
        HealthCategory.Memory => new[]
        {
            "物理内存占用", "物理内存已用 / 总量",
            "虚拟内存占用", "虚拟内存已用 / 总量",
        },
        HealthCategory.Voltage => new[]
        {
            "+12V", "+5V", "+3.3V", "Vcore",
            "CPU SoC", "GPU 核心", "+3V Standby", "CMOS 电池",
        },
        HealthCategory.Motherboard => MotherboardRowNames(r),
        _ => Array.Empty<string>(),
    };

    private static string[] CpuRowNames(HardwareReading? r)
    {
        var names = new List<string> { "温度", "负载", "功耗", "平均频率", "最高核心负载" };
        if (r != null)
            foreach (var core in r.CpuCores)
                names.Add($"核心 {core.Index}");
        return names.ToArray();
    }

    private static string[] MotherboardRowNames(HardwareReading? r)
    {
        var names = new List<string> { "传感器最高温度" };
        if (r != null)
            foreach (var fan in r.Fans)
                names.Add(FanLabel(fan.Name));
        return names.ToArray();
    }

    private static string FanLabel(string name)
    {
        int index = HardwareMonitorService.ParseTrailingIndex(name);
        return index > 0 ? $"风扇 {index}" : name;
    }

    private readonly record struct MetricCell(string Value, string State, HardwareHealth Level, bool Colorize);

    private static List<MetricCell> BuildCells(HealthCategory category, HardwareReading r)
    {
        switch (category)
        {
            case HealthCategory.Cpu:
            {
                var cells = new List<MetricCell>
                {
                    StateCell(Temp(r.CpuTemperature), HardwareThresholds.EvaluateCpuTemp(r.CpuTemperature)),
                    PlainCell(Pct(r.CpuLoad)),
                    PlainCell(Watts(r.CpuPower)),
                    PlainCell(Mhz(r.CpuClock)),
                    PlainCell(Pct(r.CpuMaxCoreLoad)),
                };
                cells.AddRange(r.CpuCores.Select(core => PlainCell(CoreText(core))));
                return cells;
            }
            case HealthCategory.Gpu:
            {
                string vram = r.GpuMemoryUsedMb.HasValue && r.GpuMemoryTotalMb is > 0
                    ? $"{r.GpuMemoryUsedMb.Value / 1024.0:F1} / {r.GpuMemoryTotalMb.Value / 1024.0:F1} GB" +
                      $"（{r.GpuMemoryUsedMb.Value / r.GpuMemoryTotalMb.Value * 100.0:F1}%）"
                    : "—";
                return new()
                {
                    StateCell(Temp(r.GpuTemperature), HardwareThresholds.EvaluateGpuTemp(r.GpuTemperature)),
                    StateCell(Temp(r.GpuHotspot), HardwareThresholds.EvaluateGpuHotspot(r.GpuHotspot)),
                    StateCell(Temp(r.GpuMemoryTemperature), HardwareThresholds.EvaluateGpuMemoryTemp(r.GpuMemoryTemperature)),
                    PlainCell(vram),
                    PlainCell(Pct(r.GpuLoad)),
                    PlainCell(Pct(r.GpuMemoryLoad)),
                    PlainCell(Mhz(r.GpuClock)),
                    PlainCell(Mhz(r.GpuMemoryClock)),
                    PlainCell(Watts(r.GpuPower)),
                    PlainCell(Rpm(r.GpuFanRpm)),
                    PlainCell(Pct(r.GpuFanPercent)),
                    PlainCell(Volts(r.GpuVoltage, 3)),
                };
            }
            case HealthCategory.Memory:
                return new()
                {
                    StateCell(Pct(r.MemoryLoad), HardwareThresholds.EvaluateMemoryLoad(r.MemoryLoad)),
                    PlainCell(GbPair(r.MemoryUsedGb, r.MemoryTotalGb)),
                    StateCell(Pct(r.VirtualMemoryLoad), HardwareThresholds.EvaluateMemoryLoad(r.VirtualMemoryLoad)),
                    PlainCell(GbPair(r.VirtualMemoryUsedGb, r.VirtualMemoryTotalGb)),
                };
            case HealthCategory.Voltage:
                return new()
                {
                    StateCell(VoltageText(r.Voltage12V, HardwareThresholds.V12Nominal),
                        HardwareThresholds.EvaluateVoltage12V(r.Voltage12V)),
                    StateCell(VoltageText(r.Voltage5V, HardwareThresholds.V5Nominal),
                        HardwareThresholds.EvaluateVoltage5V(r.Voltage5V)),
                    StateCell(VoltageText(r.Voltage3V, HardwareThresholds.V3Nominal),
                        HardwareThresholds.EvaluateVoltage3V(r.Voltage3V)),
                    PlainCell(Volts(r.CpuVoltage, 2)),
                    PlainCell(Volts(r.CpuSocVoltage, 3)),
                    PlainCell(Volts(r.GpuVoltage, 3)),
                    StateCell(VoltageText(r.Voltage3VStandby, HardwareThresholds.V3Nominal),
                        HardwareThresholds.EvaluateVoltage3V(r.Voltage3VStandby)),
                    StateCell(CmosText(r.CmosBatteryVoltage),
                        HardwareThresholds.EvaluateCmosBattery(r.CmosBatteryVoltage)),
                };
            case HealthCategory.Motherboard:
            {
                var cells = new List<MetricCell>
                {
                    StateCell(Temp(r.MoboMaxTemperature), HardwareThresholds.EvaluateMoboTemp(r.MoboMaxTemperature)),
                };
                cells.AddRange(r.Fans.Select(fan => PlainCell(FanText(fan))));
                return cells;
            }
            default:
                return new();
        }
    }

    private static MetricCell StateCell(string value, HardwareHealth level)
        => new(value,
            level == HardwareHealth.Unknown ? "—" : HardwareThresholds.LabelFor(level),
            level,
            Colorize: true);

    private static MetricCell PlainCell(string value)
        => new(value, "—", HardwareHealth.Unknown, Colorize: false);

    private static string Temp(double? v) => v.HasValue ? $"{v.Value:F1} ℃" : "—";
    private static string Pct(double? v) => v.HasValue ? $"{v.Value:F0}%" : "—";
    private static string Watts(double? v) => v.HasValue ? $"{v.Value:F1} W" : "—";
    private static string Mhz(double? v) => v.HasValue ? $"{v.Value:F0} MHz" : "—";
    private static string Rpm(double? v) => v.HasValue ? $"{v.Value:F0} RPM" : "—";

    private static string Volts(double? v, int decimals)
        => v.HasValue ? v.Value.ToString($"F{decimals}") + " V" : "—";

    private static string GbPair(double? used, double? total)
    {
        if (used.HasValue && total.HasValue)
            return $"{used.Value:F1} / {total.Value:F1} GB";
        return used.HasValue ? $"{used.Value:F1} GB" : "—";
    }

    private static string CoreText(CpuCoreReading core)
    {
        var parts = new List<string>();
        if (core.Load.HasValue) parts.Add($"负载 {core.Load.Value:F0}%");
        if (core.Clock.HasValue) parts.Add($"{core.Clock.Value:F0} MHz");
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private static string FanText(FanReading fan)
    {
        var parts = new List<string>();
        if (fan.Rpm.HasValue) parts.Add($"{fan.Rpm.Value:F0} RPM");
        if (fan.Percent.HasValue) parts.Add($"{fan.Percent.Value:F0}%");
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private static HardwareHealth WorstSpare(List<StorageReading> storages)
    {
        var worst = HardwareHealth.Unknown;
        foreach (var storage in storages)
        {
            var level = HardwareThresholds.EvaluateAvailableSpare(
                storage.AvailableSparePercent, storage.SpareThresholdPercent);
            if (level > worst) worst = level;
        }
        return worst;
    }

    private static string VoltageText(double? v, double nominal)
    {
        if (!v.HasValue) return "—";
        double deviation = (v.Value - nominal) / nominal * 100.0;
        return $"{v.Value:F2} V（额定 {nominal:0.##} V，偏离 {deviation:+0.0;-0.0;0.0}%）";
    }

    private static string CmosText(double? v)
        => v.HasValue
            ? $"{v.Value:F2} V（CR2032，低电压告警线 {HardwareThresholds.CmosBatteryWarn:0.0} V）"
            : "—";

    private static HardwareHealth Worst(params HardwareHealth[] levels)
    {
        var worst = HardwareHealth.Unknown;
        foreach (var level in levels)
            if (level > worst) worst = level;
        return worst;
    }

    private void Nav_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= CategoryNames.Length) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? Color.FromArgb(232, 240, 255) : CardBack))
            e.Graphics.FillRectangle(background, e.Bounds);

        if (selected)
        {
            using var accent = new SolidBrush(Color.FromArgb(0x1e, 0x3a, 0x8a));
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top + 6, 3, e.Bounds.Height - 12);
        }

        using (var dot = new SolidBrush(HardwareThresholds.ColorFor(_navLevels[e.Index])))
            e.Graphics.FillEllipse(dot, e.Bounds.Left + 12, e.Bounds.Top + 19, 8, 8);

        using (var nameBrush = new SolidBrush(TextDark))
            e.Graphics.DrawString(CategoryNames[e.Index], NavNameFont, nameBrush,
                e.Bounds.Left + 28, e.Bounds.Top + 5);

        using (var summaryBrush = new SolidBrush(TextGray))
            e.Graphics.DrawString(_navSummaries[e.Index] ?? "—", NavSummaryFont, summaryBrush,
                e.Bounds.Left + 28, e.Bounds.Top + 26);
    }

    private void RefreshDrives(HardwareReading r)
    {
        var seen = new HashSet<string>();
        var ordered = StorageLabel.OrderForDisplay(r.Storages).ToList();
        _drivesList.BeginUpdate();
        try
        {
            foreach (var storage in ordered)
            {
                string key = StorageLabel.Key(storage);
                seen.Add(key);

                if (!_driveItems.TryGetValue(key, out var item))
                {
                    item = new ListViewItem();
                    for (int i = 1; i < _drivesList.Columns.Count; i++) item.SubItems.Add(string.Empty);
                    _drivesList.Items.Add(item);
                    _driveItems[key] = item;
                }

                item.Text = string.IsNullOrWhiteSpace(storage.Name) ? "—" : storage.Name;
                SetCell(item, 1, StorageLabel.Short(storage));
                SetCell(item, 2, storage.Temperature.HasValue ? $"{storage.Temperature.Value:F0}℃" : "—");
                SetCell(item, 3, storage.LifePercent.HasValue ? $"{storage.LifePercent.Value:F0}%" : "—");
                SetCell(item, 4, storage.AvailableSparePercent.HasValue
                    ? $"{storage.AvailableSparePercent.Value:F0}%"
                    : "—");
                SetCell(item, 5, storage.UsedSpacePercent.HasValue ? $"{storage.UsedSpacePercent.Value:F1}%" : "—");
                SetCell(item, 6, FormatRate(storage.ReadRateMBps));
                SetCell(item, 7, FormatRate(storage.WriteRateMBps));
                SetCell(item, 8, storage.TotalWrittenGb.HasValue
                    ? $"{storage.TotalWrittenGb.Value / 1024.0:F2} TB"
                    : "—");

                item.ToolTipText = storage.AvailableSparePercent.HasValue && storage.SpareThresholdPercent.HasValue
                    ? $"{StorageLabel.Short(storage)} · 备用块 {storage.AvailableSparePercent.Value:F0}%" +
                      $"（厂商阈值 {storage.SpareThresholdPercent.Value:F0}%）"
                    : string.Empty;

                item.ForeColor = HardwareThresholds.ColorFor(Worst(
                    HardwareThresholds.EvaluateSsdTemp(storage.Temperature),
                    HardwareThresholds.EvaluateSsdLife(storage.LifePercent),
                    HardwareThresholds.EvaluateAvailableSpare(
                        storage.AvailableSparePercent, storage.SpareThresholdPercent)));
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                var item = _driveItems[StorageLabel.Key(ordered[i])];
                if (_drivesList.Items.IndexOf(item) == i) continue;
                _drivesList.Items.Remove(item);
                _drivesList.Items.Insert(i, item);
            }

            foreach (string key in _driveItems.Keys.ToList())
            {
                if (seen.Contains(key)) continue;
                _drivesList.Items.Remove(_driveItems[key]);
                _driveItems.Remove(key);
            }
        }
        finally
        {
            _drivesList.EndUpdate();
        }
    }

    private static void SetCell(ListViewItem item, int index, string text)
    {
        if (index < item.SubItems.Count)
            item.SubItems[index].Text = text;
    }

    private static string FormatRate(double? mbps)
    {
        if (!mbps.HasValue) return "—";
        return mbps.Value < 0.05 ? "0.0 MB/s" : $"{mbps.Value:F1} MB/s";
    }

    private void ClearValues()
    {
        Array.Fill(_navLevels, HardwareHealth.Unknown);
        Array.Fill(_navSummaries, "—");
        _nav.Invalidate();

        foreach (DataGridViewRow row in _detailGrid.Rows)
        {
            row.Cells[1].Value = "—";
            row.Cells[2].Value = "—";
            row.Cells[1].Style.ForeColor = TextGray;
            row.Cells[2].Style.ForeColor = TextGray;
        }

        _drivesList.Items.Clear();
        _driveItems.Clear();
    }

    private void SetStatus(bool ok, string text)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = ok
            ? Color.FromArgb(0x04, 0x78, 0x57)
            : Color.FromArgb(0xdc, 0x26, 0x26);
    }
}
