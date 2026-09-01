using System;
using System.Drawing;
using System.Windows.Forms;
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.UI;

/// <summary>
/// "环境变更"标签页：展示历次扫描之间检测到的环境变更
/// （BIOS / 驱动 / Windows 更新 / 内存频率 / 硬件清单），数据来自 EnvironmentHistoryStore
/// </summary>
public class EnvironmentHistoryTab : UserControl
{
    private readonly Button _btnRefresh = new() { Text = "🔄 刷新", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
    private readonly Label _lblStats = new() { Text = "尚无环境变更记录（从首次扫描起开始记录）", AutoSize = true, ForeColor = Color.FromArgb(0x47, 0x55, 0x69), Font = new Font("Microsoft YaHei UI", 9) };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BackgroundColor = Color.White, GridColor = Color.FromArgb(220, 228, 238), Font = new Font("Microsoft YaHei UI", 9) };

    public EnvironmentHistoryTab()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(248, 250, 252);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _btnRefresh.ForeColor = Color.FromArgb(0x04, 0x78, 0x57);
        top.Controls.Add(_btnRefresh);
        top.Controls.Add(new Label { Text = "  ", AutoSize = true });
        top.Controls.Add(_lblStats);
        root.Controls.Add(top, 0, 0);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "检测时间", Width = 150, DefaultCellStyle = new DataGridViewCellStyle { Format = "yyyy-MM-dd HH:mm" } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类别", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "项目", Width = 230 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "旧值", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新值", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        root.Controls.Add(_grid, 0, 1);

        Controls.Add(root);

        _btnRefresh.Click += (_, _) => LoadChanges();
        Load += (_, _) => LoadChanges();
    }

    /// <summary>外部通知：扫描完成，环境历史可能更新</summary>
    public void OnScanCompleted() => LoadChanges();

    /// <summary>重新读取环境历史存档并刷新表格</summary>
    public void LoadChanges()
    {
        var data = EnvironmentHistoryStore.Load();
        var changes = data.Changes;
        changes.Sort((a, b) => b.Time.CompareTo(a.Time));

        if (changes.Count == 0)
        {
            _lblStats.Text = data.Snapshots.Count == 0
                ? "尚无环境变更记录（从首次扫描起开始记录）"
                : $"已有 {data.Snapshots.Count} 份环境快照 · 暂未检测到任何变更 · 存档: {EnvironmentHistoryStore.FilePath}";
        }
        else
        {
            _lblStats.Text = $"共 {changes.Count} 条变更记录 · 存档: {EnvironmentHistoryStore.FilePath}";
        }

        _grid.Rows.Clear();
        foreach (var c in changes)
        {
            int i = _grid.Rows.Add();
            var row = _grid.Rows[i];
            row.Cells[0].Value = c.Time;
            row.Cells[1].Value = EnvironmentChangeDetector.CategoryLabel(c.Category);
            row.Cells[2].Value = c.Item;
            row.Cells[3].Value = c.OldValue;
            row.Cells[4].Value = c.NewValue;
        }
    }
}
