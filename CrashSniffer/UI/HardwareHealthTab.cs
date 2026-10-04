using System;
using System.Drawing;
using System.Windows.Forms;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// "硬件健康"标签页：4 卡片实时展示 CPU/GPU/主板电压/SSD 传感器读数。
/// 数据来自 HardwareMonitorService 的环形缓冲，2s 刷新。
/// 未提权/传感器不可用时显示降级占位。
/// </summary>
public class HardwareHealthTab : UserControl
{
    private readonly Label _lblStatus = new()
    {
        Text = "🔴 未采集到硬件数据（需管理员权限）",
        AutoSize = true,
        ForeColor = Color.FromArgb(0xdc, 0x26, 0x26),
        Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
    };

    private readonly Label _lblCpuTitle, _lblCpuValue;
    private readonly Label _lblGpuTitle, _lblGpuValue;
    private readonly Label _lblVoltTitle, _lblVoltValue;
    private readonly Label _lblSsdTitle, _lblSsdValue;

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private HardwareMonitorService? _service;

    private static readonly Color CardBack = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(226, 232, 240);
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9, FontStyle.Bold);
    private static readonly Font ValueFont = new("Microsoft YaHei UI", 14, FontStyle.Bold);

    public HardwareHealthTab()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(248, 250, 252);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(_lblStatus, 0, 0);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _lblCpuTitle = MakeTitle("🖥️ CPU");
        _lblCpuValue = MakeValue("—");
        _lblGpuTitle = MakeTitle("🎮 GPU");
        _lblGpuValue = MakeValue("—");
        _lblVoltTitle = MakeTitle("⚡ 主板电压");
        _lblVoltValue = MakeValue("—");
        _lblSsdTitle = MakeTitle("💾 SSD");
        _lblSsdValue = MakeValue("—");

        grid.Controls.Add(MakeCard(_lblCpuTitle, _lblCpuValue), 0, 0);
        grid.Controls.Add(MakeCard(_lblGpuTitle, _lblGpuValue), 1, 0);
        grid.Controls.Add(MakeCard(_lblVoltTitle, _lblVoltValue), 0, 1);
        grid.Controls.Add(MakeCard(_lblSsdTitle, _lblSsdValue), 1, 1);

        root.Controls.Add(grid, 0, 1);
        Controls.Add(root);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshReadings();
        Load += (_, _) => _refreshTimer.Start();
        Disposed += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>绑定监控服务实例</summary>
    public void BindService(HardwareMonitorService service)
    {
        _service = service;
        RefreshReadings();
    }

    private static Label MakeTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(0x1f, 0x29, 0x37),
        Font = TitleFont,
    };

    private static Label MakeValue(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(0x6b, 0x72, 0x80),
        Font = ValueFont,
    };

    private static Panel MakeCard(Label title, Label value)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBack,
            Padding = new Padding(16),
            Margin = new Padding(6),
        };
        var border = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBorder,
            Padding = new Padding(1),
        };
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = CardBack };
        value.Location = new Point(0, 30);
        inner.Controls.Add(value);
        title.Location = new Point(0, 0);
        inner.Controls.Add(title);
        border.Controls.Add(inner);
        card.Controls.Add(border);
        return card;
    }

    private void RefreshReadings()
    {
        if (_service == null)
        {
            SetStatus(false, "🔴 监控服务未启动");
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
        UpdateCpu(r);
        UpdateGpu(r);
        UpdateVoltage(r);
        UpdateSsd(r);
    }

    private void UpdateCpu(HardwareReading r)
    {
        string temp = r.CpuTemperature.HasValue ? $"{r.CpuTemperature.Value:F1} ℃" : "—";
        string load = r.CpuLoad.HasValue ? $"{r.CpuLoad.Value:F0}%" : "—";
        string power = r.CpuPower.HasValue ? $"{r.CpuPower.Value:F1}W" : "—";
        string clock = r.CpuClock.HasValue ? $"{r.CpuClock.Value:F0}MHz" : "—";

        _lblCpuValue.Text = $"温度 {temp}\n负载 {load} · {power}\n{clock}";
        var level = HardwareThresholds.EvaluateCpuTemp(r.CpuTemperature);
        _lblCpuValue.ForeColor = HardwareThresholds.ColorFor(level);
    }

    private void UpdateGpu(HardwareReading r)
    {
        string temp = r.GpuTemperature.HasValue ? $"{r.GpuTemperature.Value:F1} ℃" : "—";
        string load = r.GpuLoad.HasValue ? $"{r.GpuLoad.Value:F0}%" : "—";
        string power = r.GpuPower.HasValue ? $"{r.GpuPower.Value:F1}W" : "—";

        _lblGpuValue.Text = $"温度 {temp}\n负载 {load} · {power}";
        var level = HardwareThresholds.EvaluateGpuTemp(r.GpuTemperature);
        _lblGpuValue.ForeColor = HardwareThresholds.ColorFor(level);
    }

    private void UpdateVoltage(HardwareReading r)
    {
        string v12 = r.Voltage12V.HasValue ? $"{r.Voltage12V.Value:F2}V" : "—";
        string v5 = r.Voltage5V.HasValue ? $"{r.Voltage5V.Value:F2}V" : "—";
        string v3 = r.Voltage3V.HasValue ? $"{r.Voltage3V.Value:F2}V" : "—";

        _lblVoltValue.Text = $"+12V {v12}\n+5V {v5} · +3.3V {v3}";
        var levels = new[]
        {
            HardwareThresholds.EvaluateVoltage12V(r.Voltage12V),
            HardwareThresholds.EvaluateVoltage5V(r.Voltage5V),
            HardwareThresholds.EvaluateVoltage3V(r.Voltage3V),
        };
        HardwareHealth worst = HardwareHealth.Unknown;
        foreach (var l in levels)
            if (l > worst) worst = l;
        _lblVoltValue.ForeColor = HardwareThresholds.ColorFor(worst);
    }

    private void UpdateSsd(HardwareReading r)
    {
        string temp = r.SsdTemperature.HasValue ? $"{r.SsdTemperature.Value:F1} ℃" : "—";
        string life = r.SsdLife.HasValue ? $"{r.SsdLife.Value:F0}%" : "—";
        _lblSsdValue.Text = $"温度 {temp}\n寿命 {life}";
        var level = HardwareThresholds.EvaluateSsdTemp(r.SsdTemperature);
        _lblSsdValue.ForeColor = HardwareThresholds.ColorFor(level);
    }

    private void ClearValues()
    {
        _lblCpuValue.Text = "—";
        _lblGpuValue.Text = "—";
        _lblVoltValue.Text = "—";
        _lblSsdValue.Text = "—";
        Color gray = Color.FromArgb(0x6b, 0x72, 0x80);
        _lblCpuValue.ForeColor = gray;
        _lblGpuValue.ForeColor = gray;
        _lblVoltValue.ForeColor = gray;
        _lblSsdValue.ForeColor = gray;
    }

    private void SetStatus(bool ok, string text)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = ok
            ? Color.FromArgb(0x04, 0x78, 0x57)
            : Color.FromArgb(0xdc, 0x26, 0x26);
    }
}
