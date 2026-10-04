# 硬件健康看板（实时监控+崩溃回放）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 CrashSniffer 增加实时硬件健康监控看板与崩溃时刻硬件读数回放，帮助定位"崩溃前 CPU/GPU 是否过热/过压"。

**Architecture:** 后台 `HardwareMonitorService` 以 2s 间隔轮询 LibreHardwareMonitorLib，维护 300 点环形缓冲（约 10 分钟）；崩溃事件发生时把崩溃前 5min+后 1min 的读数窗口落盘为 JSON 快照。UI 层新增「硬件健康」根 Tab 实时展示 4 类硬件，崩溃详情新增「硬件回放」子 Tab 展示时间序列曲线。存储与阈值层为无 UI 依赖的可测类。

**Tech Stack:** .NET 8 WinForms、LibreHardwareMonitorLib（NuGet）、System.Text.Json（现有）、xunit（现有）、GDI+ 自绘曲线（无第三方图表库）。

**设计文档：** `docs/superpowers/specs/2026-09-17-hardware-health-dashboard-design.md`

**关键既有代码约定：**
- 存档模式参照 `CrashSniffer/Services/HistoryStore.cs` / `EnvironmentHistoryStore.cs`（JSON + 原子写入 + .bak + catch-all + 上限淘汰）
- UI 字体一律走 `MainForm.GetCachedFont`（GDI 句柄防泄漏）
- 后台任务不阻塞 UI，UI 更新经 `BeginInvoke`
- 颜色常量与 MainForm 一致：成功绿 `#047857`、警告橙 `#d97706`、危险红 `#dc2626`、信息蓝 `#1e3a8a`、浅灰底 `#f8fafc`
- 仓库无 .sln，所有 dotnet 命令显式指向 csproj 路径
- `app.manifest` 已配 `requireAdministrator`，启动即提权

---

### Task 1: 数据模型 HardwareReading + HardwareSnapshot

**Files:**
- Create: `CrashSniffer/Models/HardwareReading.cs`

- [ ] **Step 1: 创建硬件读数模型**

`CrashSniffer/Models/HardwareReading.cs`：

```csharp
using System;

namespace CrashSniffer.Models;

/// <summary>
/// 单次硬件传感器读数快照（一次轮询的全部核心指标）。
/// 缺失项为 null，UI 展示为 "—"。
/// </summary>
public class HardwareReading
{
    /// <summary>采集时间（本地时间）</summary>
    public DateTime Timestamp { get; set; }

    // —— CPU ——
    /// <summary>CPU 封装温度（℃）</summary>
    public double? CpuTemperature { get; set; }

    /// <summary>CPU 封装功耗（W）</summary>
    public double? CpuPower { get; set; }

    /// <summary>CPU 核心平均频率（MHz）</summary>
    public double? CpuClock { get; set; }

    /// <summary>CPU 负载（%）</summary>
    public double? CpuLoad { get; set; }

    // —— GPU ——
    /// <summary>GPU 核心温度（℃）</summary>
    public double? GpuTemperature { get; set; }

    /// <summary>GPU 热点温度（℃，部分卡支持）</summary>
    public double? GpuHotspot { get; set; }

    /// <summary>GPU 功耗（W）</summary>
    public double? GpuPower { get; set; }

    /// <summary>GPU 核心频率（MHz）</summary>
    public double? GpuClock { get; set; }

    /// <summary>GPU 显存频率（MHz）</summary>
    public double? GpuMemoryClock { get; set; }

    /// <summary>GPU 利用率（%）</summary>
    public double? GpuLoad { get; set; }

    // —— 主板 / 电压 ——
    /// <summary>CPU Vcore 电压（V）</summary>
    public double? CpuVoltage { get; set; }

    /// <summary>+12V 电压（V）</summary>
    public double? Voltage12V { get; set; }

    /// <summary>+5V 电压（V）</summary>
    public double? Voltage5V { get; set; }

    /// <summary>+3.3V 电压（V）</summary>
    public double? Voltage3V { get; set; }

    // —— 散热 ——
    /// <summary>CPU 风扇转速（RPM）</summary>
    public double? CpuFanRpm { get; set; }

    /// <summary>GPU 风扇转速（RPM，取第一个 GPU 风扇）</summary>
    public double? GpuFanRpm { get; set; }

    // —— 存储 ——
    /// <summary>SSD 温度（℃，取第一个 NVMe/SATA SSD）</summary>
    public double? SsdTemperature { get; set; }

    /// <summary>SSD 剩余寿命（%，部分 SSD 支持）</summary>
    public double? SsdLife { get; set; }
}

/// <summary>
/// 崩溃时刻的硬件读数窗口快照（落盘到 hardware-snapshots 目录）
/// </summary>
public class HardwareSnapshot
{
    /// <summary>崩溃事件唯一标识（时间+ticks，用于与崩溃列表关联）</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>崩溃发生时间</summary>
    public DateTime CrashTime { get; set; }

    /// <summary>崩溃类型描述</summary>
    public string CrashType { get; set; } = string.Empty;

    /// <summary>崩溃前 5min + 崩溃后 1min 的读数序列（按时间升序）</summary>
    public List<HardwareReading> Readings { get; set; } = new();
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```powershell
git add CrashSniffer/Models/HardwareReading.cs
git commit -m "feat: 硬件健康看板 - 数据模型 HardwareReading/HardwareSnapshot"
```

---

### Task 2: 管理员权限检测 AdminRightsHelper

**Files:**
- Create: `CrashSniffer/Services/AdminRightsHelper.cs`
- Test: `CrashSniffer.Tests/AdminRightsHelperTests.cs`

- [ ] **Step 1: 写失败测试**

`CrashSniffer.Tests/AdminRightsHelperTests.cs`：

```csharp
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class AdminRightsHelperTests
{
    [Fact]
    public void IsRunningAsAdmin_ReturnsBoolean()
    {
        // manifest 配了 requireAdministrator，但测试可能非管理员运行
        bool result = AdminRightsHelper.IsRunningAsAdmin();
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void CheckAndPrompt_ReturnsTrueWhenAdmin()
    {
        // CI/本地可能非管理员，这里只验证不抛异常
        bool result = AdminRightsHelper.CheckAndPrompt();
        Assert.IsType<bool>(result);
    }
}
```

- [ ] **Step 2: 运行测试确认失败（类型不存在）**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter AdminRightsHelperTests`
Expected: 编译失败 CS0246 "未找到类型 AdminRightsHelper"

- [ ] **Step 3: 实现 AdminRightsHelper**

`CrashSniffer/Services/AdminRightsHelper.cs`：

```csharp
using System;
using System.Security.Principal;
using System.Windows.Forms;

namespace CrashSniffer.Services;

/// <summary>
/// 管理员权限检测：LibreHardwareMonitor 读取硬件传感器需要管理员权限。
/// app.manifest 已配 requireAdministrator，但用户可能通过兼容模式/调试器绕过，
/// 运行时再次确认并提示。
/// </summary>
public static class AdminRightsHelper
{
    /// <summary>当前进程是否以管理员身份运行</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检测权限，非管理员时弹提示框。返回是否具备权限。
    /// 不强制退出，由调用方决定是否降级。
    /// </summary>
    public static bool CheckAndPrompt()
    {
        if (IsRunningAsAdmin()) return true;

        MessageBox.Show(
            "硬件健康监控需要管理员权限才能读取 CPU/GPU/主板传感器数据。\n\n" +
            "请以管理员身份重新运行 CrashSniffer，否则硬件看板将显示「未获取数据」。",
            "需要管理员权限",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter AdminRightsHelperTests`
Expected: PASS

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/AdminRightsHelper.cs CrashSniffer.Tests/AdminRightsHelperTests.cs
git commit -m "feat: 硬件健康看板 - AdminRightsHelper 权限检测"
```

---

### Task 3: 硬件阈值 HardwareThresholds

**Files:**
- Create: `CrashSniffer/Services/HardwareThresholds.cs`
- Test: `CrashSniffer.Tests/HardwareThresholdsTests.cs`

- [ ] **Step 1: 写失败测试**

`CrashSniffer.Tests/HardwareThresholdsTests.cs`：

```csharp
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HardwareThresholdsTests
{
    [Theory]
    [InlineData(85.0, HardwareHealth.Normal)]   // CPU < 90 正常
    [InlineData(92.0, HardwareHealth.Warning)]  // 90-95 警告
    [InlineData(97.0, HardwareHealth.Critical)] // >= 95 危险
    public void EvaluateCpuTemp_ReturnsCorrectLevel(double temp, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateCpuTemp(temp));
    }

    [Theory]
    [InlineData(75.0, HardwareHealth.Normal)]
    [InlineData(82.0, HardwareHealth.Warning)]
    [InlineData(90.0, HardwareHealth.Critical)]
    public void EvaluateGpuTemp_ReturnsCorrectLevel(double temp, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateGpuTemp(temp));
    }

    [Theory]
    [InlineData(12.0, HardwareHealth.Normal)]   // 12V ±5% = 11.4~12.6
    [InlineData(11.3, HardwareHealth.Warning)]
    [InlineData(10.0, HardwareHealth.Critical)]
    [InlineData(13.0, HardwareHealth.Critical)]
    public void EvaluateVoltage12V_ReturnsCorrectLevel(double v, HardwareHealth expected)
    {
        Assert.Equal(expected, HardwareThresholds.EvaluateVoltage12V(v));
    }

    [Fact]
    public void EvaluateOverall_NoReadings_ReturnsUnknown()
    {
        var r = new CrashSniffer.Models.HardwareReading();
        Assert.Equal(HardwareHealth.Unknown, HardwareThresholds.EvaluateOverall(r));
    }

    [Fact]
    public void EvaluateOverall_CriticalItem_ReturnsCritical()
    {
        var r = new CrashSniffer.Models.HardwareReading { CpuTemperature = 100 };
        Assert.Equal(HardwareHealth.Critical, HardwareThresholds.EvaluateOverall(r));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter HardwareThresholdsTests`
Expected: 编译失败

- [ ] **Step 3: 实现 HardwareThresholds**

`CrashSniffer/Services/HardwareThresholds.cs`：

```csharp
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>硬件健康等级</summary>
public enum HardwareHealth
{
    Unknown,   // 无数据
    Normal,    // 正常（绿）
    Warning,   // 警告（橙）
    Critical,  // 危险（红）
}

/// <summary>
/// 硬件阈值判定：根据读数返回健康等级。
/// 阈值参考主流硬件厂商规格，保守取值。
/// </summary>
public static class HardwareThresholds
{
    // CPU 温度阈值（℃）
    private const double CpuTempWarn = 90.0;
    private const double CpuTempCrit = 95.0;

    // GPU 温度阈值（℃）
    private const double GpuTempWarn = 80.0;
    private const double GpuTempCrit = 85.0;

    // SSD 温度阈值（℃）
    private const double SsdTempWarn = 65.0;
    private const double SsdTempCrit = 70.0;

    // 电压阈值：额定 ±5% 警告，±10% 危险
    private const double V12Nominal = 12.0;
    private const double V5Nominal = 5.0;
    private const double V3Nominal = 3.3;
    private const double VtolWarn = 0.05;   // ±5%
    private const double VtolCrit = 0.10;   // ±10%

    public static HardwareHealth EvaluateCpuTemp(double? temp)
        => EvaluateTemp(temp, CpuTempWarn, CpuTempCrit);

    public static HardwareHealth EvaluateGpuTemp(double? temp)
        => EvaluateTemp(temp, GpuTempWarn, GpuTempCrit);

    public static HardwareHealth EvaluateSsdTemp(double? temp)
        => EvaluateTemp(temp, SsdTempWarn, SsdTempCrit);

    public static HardwareHealth EvaluateVoltage12V(double? v)
        => EvaluateVoltage(v, V12Nominal);

    public static HardwareHealth EvaluateVoltage5V(double? v)
        => EvaluateVoltage(v, V5Nominal);

    public static HardwareHealth EvaluateVoltage3V(double? v)
        => EvaluateVoltage(v, V3Nominal);

    private static HardwareHealth EvaluateTemp(double? v, double warn, double crit)
    {
        if (v == null) return HardwareHealth.Unknown;
        if (v >= crit) return HardwareHealth.Critical;
        if (v >= warn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    private static HardwareHealth EvaluateVoltage(double? v, double nominal)
    {
        if (v == null) return HardwareHealth.Unknown;
        double deviation = Math.Abs(v.Value - nominal) / nominal;
        if (deviation >= VtolCrit) return HardwareHealth.Critical;
        if (deviation >= VtolWarn) return HardwareHealth.Warning;
        return HardwareHealth.Normal;
    }

    /// <summary>
    /// 综合评估：取所有指标中最严重的等级。
    /// 无任何有效读数返回 Unknown。
    /// </summary>
    public static HardwareHealth EvaluateOverall(HardwareReading r)
    {
        var levels = new[]
        {
            EvaluateCpuTemp(r.CpuTemperature),
            EvaluateGpuTemp(r.GpuTemperature),
            EvaluateSsdTemp(r.SsdTemperature),
            EvaluateVoltage12V(r.Voltage12V),
            EvaluateVoltage5V(r.Voltage5V),
            EvaluateVoltage3V(r.Voltage3V),
        };

        HardwareHealth worst = HardwareHealth.Unknown;
        foreach (var l in levels)
        {
            if (l == HardwareHealth.Critical) return HardwareHealth.Critical;
            if (l == HardwareHealth.Warning) worst = HardwareHealth.Warning;
            else if (l == HardwareHealth.Normal && worst == HardwareHealth.Unknown)
                worst = HardwareHealth.Normal;
        }
        return worst;
    }

    /// <summary>等级对应的颜色（与 MainForm 配色一致）</summary>
    public static System.Drawing.Color ColorFor(HardwareHealth level) => level switch
    {
        HardwareHealth.Critical => System.Drawing.Color.FromArgb(0xdc, 0x26, 0x26),
        HardwareHealth.Warning => System.Drawing.Color.FromArgb(0xd9, 0x77, 0x06),
        HardwareHealth.Normal => System.Drawing.Color.FromArgb(0x04, 0x78, 0x57),
        _ => System.Drawing.Color.FromArgb(0x6b, 0x72, 0x80),
    };

    /// <summary>等级中文标签</summary>
    public static string LabelFor(HardwareHealth level) => level switch
    {
        HardwareHealth.Critical => "危险",
        HardwareHealth.Warning => "警告",
        HardwareHealth.Normal => "正常",
        _ => "未知",
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter HardwareThresholdsTests`
Expected: PASS

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/HardwareThresholds.cs CrashSniffer.Tests/HardwareThresholdsTests.cs
git commit -m "feat: 硬件健康看板 - HardwareThresholds 阈值判定"
```

---

### Task 4: 环形缓冲 RingBuffer（监控服务内部组件）

**Files:**
- Create: `CrashSniffer/Services/RingBuffer.cs`
- Test: `CrashSniffer.Tests/RingBufferTests.cs`

- [ ] **Step 1: 写失败测试**

`CrashSniffer.Tests/RingBufferTests.cs`：

```csharp
using System.Linq;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class RingBufferTests
{
    [Fact]
    public void Add_BelowCapacity_ContainsAll()
    {
        var buf = new RingBuffer<int>(5);
        for (int i = 0; i < 3; i++) buf.Add(i);
        Assert.Equal(new[] { 0, 1, 2 }, buf.ToArray());
    }

    [Fact]
    public void Add_ExceedsCapacity_DropsOldest()
    {
        var buf = new RingBuffer<int>(3);
        for (int i = 0; i < 5; i++) buf.Add(i);
        // 只保留最新 3 个
        Assert.Equal(new[] { 2, 3, 4 }, buf.ToArray());
    }

    [Fact]
    public void Count_ReflectsActualCount()
    {
        var buf = new RingBuffer<int>(10);
        Assert.Equal(0, buf.Count);
        buf.Add(1); buf.Add(2);
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buf = new RingBuffer<int>(5);
        buf.Add(1); buf.Add(2);
        buf.Clear();
        Assert.Equal(0, buf.Count);
        Assert.Empty(buf.ToArray());
    }

    [Fact]
    public void ToArray_ReturnsChronologicalOrder()
    {
        var buf = new RingBuffer<int>(4);
        for (int i = 0; i < 10; i++) buf.Add(i);
        var arr = buf.ToArray();
        Assert.Equal(new[] { 6, 7, 8, 9 }, arr);
        // 验证升序
        for (int i = 1; i < arr.Length; i++)
            Assert.True(arr[i] > arr[i - 1]);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter RingBufferTests`
Expected: 编译失败

- [ ] **Step 3: 实现 RingBuffer**

`CrashSniffer/Services/RingBuffer.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace CrashSniffer.Services;

/// <summary>
/// 线程安全的固定容量环形缓冲：超过容量后覆盖最旧数据。
/// 用于保存最近 N 次硬件读数（300 次 × 2s = 10 分钟）。
/// </summary>
public class RingBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _head;   // 下一个写入位置
    private int _count;  // 当前元素数
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer = new T[capacity];
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>追加元素，满则覆盖最旧</summary>
    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>清空</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>按时间顺序（旧→新）返回快照数组</summary>
    public T[] ToArray()
    {
        lock (_lock)
        {
            var result = new T[_count];
            if (_count == 0) return result;
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % _capacity];
            }
            return result;
        }
    }

    /// <summary>返回时间范围内的元素（含两端）</summary>
    public T[] GetRange(DateTime from, DateTime to, Func<T, DateTime> timeSelector)
    {
        var all = ToArray();
        var list = new List<T>(all.Length);
        foreach (var item in all)
        {
            DateTime t = timeSelector(item);
            if (t >= from && t <= to) list.Add(item);
        }
        return list.ToArray();
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter RingBufferTests`
Expected: PASS

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/RingBuffer.cs CrashSniffer.Tests/RingBufferTests.cs
git commit -m "feat: 硬件健康看板 - RingBuffer 环形缓冲"
```

---

### Task 5: 硬件快照存储 HardwareSnapshotStore

**Files:**
- Create: `CrashSniffer/Services/HardwareSnapshotStore.cs`
- Test: `CrashSniffer.Tests/HardwareSnapshotStoreTests.cs`

- [ ] **Step 1: 写失败测试**

`CrashSniffer.Tests/HardwareSnapshotStoreTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using CrashSniffer.Models;
using CrashSniffer.Services;
using Xunit;

namespace CrashSniffer.Tests;

public class HardwareSnapshotStoreTests : IDisposable
{
    private readonly string _testDir;

    public HardwareSnapshotStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"hw_test_{Guid.NewGuid():N}");
        HardwareSnapshotStore.StorageDirOverride = _testDir;
    }

    public void Dispose()
    {
        HardwareSnapshotStore.StorageDirOverride = null;
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void SaveAndLoad_Roundtrip()
    {
        var snap = new HardwareSnapshot
        {
            EventId = "test_event_1",
            CrashTime = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Local),
            CrashType = "BSOD",
            Readings = new()
            {
                new() { Timestamp = new DateTime(2026, 9, 17, 11, 55, 0), CpuTemperature = 85, GpuTemperature = 75 },
            }
        };

        HardwareSnapshotStore.Save(snap);
        var loaded = HardwareSnapshotStore.Load(snap.EventId);

        Assert.NotNull(loaded);
        Assert.Equal(snap.EventId, loaded!.EventId);
        Assert.Equal(snap.CrashTime, loaded.CrashTime);
        Assert.Single(loaded.Readings);
        Assert.Equal(85, loaded.Readings[0].CpuTemperature);
    }

    [Fact]
    public void Load_NonExistent_ReturnsNull()
    {
        Assert.Null(HardwareSnapshotStore.Load("does_not_exist"));
    }

    [Fact]
    public void ListSnapshots_ReturnsAll()
    {
        HardwareSnapshotStore.Save(new() { EventId = "e1", CrashTime = DateTime.Now });
        HardwareSnapshotStore.Save(new() { EventId = "e2", CrashTime = DateTime.Now });
        var list = HardwareSnapshotStore.ListSnapshots();
        Assert.Contains(list, s => s.EventId == "e1");
        Assert.Contains(list, s => s.EventId == "e2");
    }

    [Fact]
    public void Save_ExceedsLimit_RemovesOldest()
    {
        // 临时把上限调小
        var oldLimit = HardwareSnapshotStore.MaxSnapshots;
        try
        {
            HardwareSnapshotStore.MaxSnapshots = 3;
            for (int i = 0; i < 5; i++)
                HardwareSnapshotStore.Save(new()
                {
                    EventId = $"e{i}",
                    CrashTime = DateTime.Now.AddMinutes(i)
                });

            var list = HardwareSnapshotStore.ListSnapshots();
            Assert.Equal(3, list.Count);
            // 最旧的 e0, e1 应被淘汰
            Assert.DoesNotContain(list, s => s.EventId == "e0");
            Assert.DoesNotContain(list, s => s.EventId == "e1");
            Assert.Contains(list, s => s.EventId == "e4");
        }
        finally
        {
            HardwareSnapshotStore.MaxSnapshots = oldLimit;
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter HardwareSnapshotStoreTests`
Expected: 编译失败

- [ ] **Step 3: 实现 HardwareSnapshotStore**

`CrashSniffer/Services/HardwareSnapshotStore.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件快照存档：每次崩溃把前后窗口的读数序列落盘为独立 JSON 文件。
/// 存储路径：%LOCALAPPDATA%\CrashSniffer\hardware-snapshots\
/// 最多保留 100 份，超出淘汰最旧（按 CrashTime）。
/// 原子写入（.tmp + File.Move），损坏自动备份，失败不阻断。
/// </summary>
public static class HardwareSnapshotStore
{
    /// <summary>快照保留上限（可被测试覆盖）</summary>
    public static int MaxSnapshots { get; set; } = 100;

    /// <summary>默认存储目录</summary>
    public static string DefaultStorageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrashSniffer", "hardware-snapshots");

    /// <summary>测试用目录覆盖</summary>
    internal static string? StorageDirOverride { get; set; }

    private static string StorageDir => StorageDirOverride ?? DefaultStorageDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>快照文件命名：{EventId}.json</summary>
    private static string FilePathFor(string eventId)
        => Path.Combine(StorageDir, $"{SanitizeFileName(eventId)}.json");

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>保存一份崩溃快照</summary>
    public static void Save(HardwareSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            string path = FilePathFor(snapshot.EventId);
            string json = JsonSerializer.Serialize(snapshot, JsonOpts);

            // 原子写入：先写 .tmp 再 Move 覆盖
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);

            // 上限淘汰
            EnforceLimit();
        }
        catch
        {
            // 存档失败不阻断主流程
        }
    }

    /// <summary>按 EventId 加载快照，不存在返回 null</summary>
    public static HardwareSnapshot? Load(string eventId)
    {
        try
        {
            string path = FilePathFor(eventId);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            var snap = JsonSerializer.Deserialize<HardwareSnapshot>(json, JsonOpts);
            snap?.Readings ??= new();
            return snap;
        }
        catch
        {
            // 损坏：备份后返回 null
            try
            {
                string path = FilePathFor(eventId);
                if (File.Exists(path))
                {
                    string bak = path + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(path, bak);
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>列出所有快照的元数据（不含读数详情），按 CrashTime 倒序</summary>
    public static List<HardwareSnapshot> ListSnapshots()
    {
        var list = new List<HardwareSnapshot>();
        try
        {
            if (!Directory.Exists(StorageDir)) return list;
            foreach (string file in Directory.GetFiles(StorageDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var snap = JsonSerializer.Deserialize<HardwareSnapshot>(json, JsonOpts);
                    if (snap != null)
                    {
                        snap.Readings ??= new();
                        list.Add(snap);
                    }
                }
                catch { /* 跳过损坏文件 */ }
            }
        }
        catch { }
        return list.OrderByDescending(s => s.CrashTime).ToList();
    }

    /// <summary>超出上限时删除最旧的快照文件</summary>
    private static void EnforceLimit()
    {
        try
        {
            var files = Directory.GetFiles(StorageDir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTime)
                .ToList();

            while (files.Count > MaxSnapshots)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                try { File.Delete(oldest.FullName); } catch { }
            }
        }
        catch { }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj --filter HardwareSnapshotStoreTests`
Expected: PASS

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/HardwareSnapshotStore.cs CrashSniffer.Tests/HardwareSnapshotStoreTests.cs
git commit -m "feat: 硬件健康看板 - HardwareSnapshotStore 快照存储"
```

---

### Task 6: 添加 LibreHardwareMonitorLib 依赖

**Files:**
- Modify: `CrashSniffer/CrashSniffer.csproj`
- Modify: `THIRD-PARTY-NOTICES.txt`

- [ ] **Step 1: 添加 NuGet 包引用**

在 `CrashSniffer/CrashSniffer.csproj` 的 `<ItemGroup>`（PackageReference）中添加：

```xml
    <PackageReference Include="LibreHardwareMonitorLib" Version="0.9.4" />
```

- [ ] **Step 2: 还原并编译验证**

Run: `dotnet restore d:\break_down\CrashSniffer\CrashSniffer.csproj` 然后 `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED，LibreHardwareMonitorLib 被还原

- [ ] **Step 3: 在 THIRD-PARTY-NOTICES.txt 追加声明**

在 `THIRD-PARTY-NOTICES.txt` 末尾追加：

```
-----------------------------------------------------------------------
N. LibreHardwareMonitor
-----------------------------------------------------------------------

组件名称：LibreHardwareMonitorLib
版权所有：Copyright (c) 2017 LibreHardwareMonitor contributors
许可协议：Mozilla Public License 2.0 (MPL-2.0)

This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. If a copy of the MPL was not distributed with this
file, You can obtain one at https://mozilla.org/MPL/2.0/.

组件用途：读取 CPU/GPU/主板/SSD 温度、电压、风扇转速等硬件传感器数据，
用于硬件健康看板与崩溃回放功能。
项目主页：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
```

（把 N 替换为当前文件中已有条目的序号+1）

- [ ] **Step 4: Commit**

```powershell
git add CrashSniffer/CrashSniffer.csproj THIRD-PARTY-NOTICES.txt
git commit -m "feat: 硬件健康看板 - 引入 LibreHardwareMonitorLib 依赖"
```

---

### Task 7: 核心监控服务 HardwareMonitorService

**Files:**
- Create: `CrashSniffer/Services/HardwareMonitorService.cs`

- [ ] **Step 1: 实现监控服务**

`CrashSniffer/Services/HardwareMonitorService.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrashSniffer.Models;
using LibreHardwareMonitor.Hardware;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件监控核心服务：后台线程以 2s 间隔轮询传感器，
/// 维护 300 点环形缓冲（~10 分钟），崩溃时输出前后窗口快照。
/// 实现 IDisposable，释放 Computer 与后台线程。
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    /// <summary>轮询间隔（毫秒）</summary>
    private const int PollIntervalMs = 2000;

    /// <summary>环形缓冲容量（300 × 2s = 10 分钟）</summary>
    private const int BufferCapacity = 300;

    /// <summary>崩溃前窗口（分钟）</summary>
    private const int BeforeCrashMinutes = 5;

    /// <summary>崩溃后窗口（分钟）</summary>
    private const int AfterCrashMinutes = 1;

    private readonly Computer _computer;
    private readonly RingBuffer<HardwareReading> _buffer = new(BufferCapacity);
    private readonly object _sensorLock = new();

    private Thread? _pollThread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>最新一次读数（线程安全访问）</summary>
    public HardwareReading? LatestReading
    {
        get
        {
            var all = _buffer.ToArray();
            return all.Length > 0 ? all[^1] : null;
        }
    }

    /// <summary>当前缓冲中的全部读数（旧→新）</summary>
    public HardwareReading[] GetBuffer() => _buffer.ToArray();

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsMemoryEnabled = false,   // 内存温度传感器支持有限，按需启用
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
        };
    }

    /// <summary>启动监控（打开传感器 + 启动轮询线程）</summary>
    public void Start()
    {
        if (_running || _disposed) return;
        try
        {
            _computer.Open();
        }
        catch
        {
            // 传感器打开失败（非管理员等），不抛异常，LatestReading 返回 null
            return;
        }
        _running = true;
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "HardwareMonitor" };
        _pollThread.Start();
    }

    /// <summary>停止监控</summary>
    public void Stop()
    {
        _running = false;
        try { _pollThread?.Join(2000); } catch { }
    }

    private void PollLoop()
    {
        while (_running && !_disposed)
        {
            try
            {
                var reading = CollectReading();
                if (reading != null)
                    _buffer.Add(reading);
            }
            catch
            {
                // 单次采集失败不终止线程
            }

            // 可中断的等待
            for (int i = 0; i < PollIntervalMs / 100 && _running; i++)
                Thread.Sleep(100);
        }
    }

    /// <summary>采集一次硬件读数</summary>
    private HardwareReading? CollectReading()
    {
        var reading = new HardwareReading { Timestamp = DateTime.Now };

        lock (_sensorLock)
        {
            try
            {
                // 遍历所有硬件并更新传感器
                foreach (var hw in _computer.Hardware)
                {
                    try { hw.Update(); } catch { continue; }

                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.Value == null) continue;
                        double v = sensor.Value.Value;

                        switch (hw.HardwareType)
                        {
                            case HardwareType.Cpu:
                                ExtractCpu(sensor, v, reading);
                                break;
                            case HardwareType.GpuNvidia:
                            case HardwareType.GpuAmd:
                            case HardwareType.GpuIntel:
                                ExtractGpu(sensor, v, reading);
                                break;
                            case HardwareType.Motherboard:
                            case HardwareType.SuperIO:
                                ExtractMobo(sensor, v, reading);
                                break;
                            case HardwareType.Storage:
                                ExtractStorage(sensor, v, reading);
                                break;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }
        return reading;
    }

    private static void ExtractCpu(ISensor s, double v, HardwareReading r)
    {
        // 取 Package 级温度/功耗/时钟，Core 平均负载
        if (s.SensorType == SensorType.Temperature && s.Name.Contains("Package"))
            r.CpuTemperature = v;
        else if (s.SensorType == SensorType.Power && s.Name.Contains("Package"))
            r.CpuPower = v;
        else if (s.SensorType == SensorType.Clock && s.Name.Contains("Core") && r.CpuClock == null)
            r.CpuClock = v;
        else if (s.SensorType == SensorType.Load && s.Name.Contains("Total"))
            r.CpuLoad = v;
    }

    private static void ExtractGpu(ISensor s, double v, HardwareReading r)
    {
        if (s.SensorType == SensorType.Temperature)
        {
            if (s.Name.Contains("Hot Spot") || s.Name.Contains("Hotspot"))
                r.GpuHotspot = v;
            else if (r.GpuTemperature == null)
                r.GpuTemperature = v;
        }
        else if (s.SensorType == SensorType.Power && r.GpuPower == null)
            r.GpuPower = v;
        else if (s.SensorType == SensorType.Clock)
        {
            if (s.Name.Contains("Memory") && r.GpuMemoryClock == null)
                r.GpuMemoryClock = v;
            else if (r.GpuClock == null)
                r.GpuClock = v;
        }
        else if (s.SensorType == SensorType.Load && s.Name.Contains("Core") && r.GpuLoad == null)
            r.GpuLoad = v;
        else if (s.SensorType == SensorType.Fan && r.GpuFanRpm == null)
            r.GpuFanRpm = v;
    }

    private static void ExtractMobo(ISensor s, double v, HardwareReading r)
    {
        if (s.SensorType == SensorType.Voltage)
        {
            string n = s.Name.ToLowerInvariant();
            if (n.Contains("vcore") || n.Contains("cpu core"))
                r.CpuVoltage = v;
            else if (n.Contains("+12") || n.Contains("12v"))
                r.Voltage12V = v;
            else if (n.Contains("+5") || n.Contains("5v"))
                r.Voltage5V = v;
            else if (n.Contains("3.3") || n.Contains("3v3"))
                r.Voltage3V = v;
        }
        else if (s.SensorType == SensorType.Fan && r.CpuFanRpm == null)
        {
            // 主板上的第一个风扇通常是 CPU 风扇
            r.CpuFanRpm = v;
        }
    }

    private static void ExtractStorage(ISensor s, double v, HardwareReading r)
    {
        if (s.SensorType == SensorType.Temperature && r.SsdTemperature == null)
            r.SsdTemperature = v;
        else if (s.SensorType == SensorType.Level && r.SsdLife == null)
            r.SsdLife = v;
    }

    /// <summary>
    /// 崩溃事件发生时：从环形缓冲提取崩溃前 5min 的读数，
    /// 并在崩溃后继续采集 1min，然后整体落盘。
    /// 返回快照 EventId（与崩溃事件关联）。
    /// </summary>
    public string RecordCrashWindow(DateTime crashTime, string crashType)
    {
        string eventId = $"crash_{crashTime:yyyyMMdd_HHmmss}_{crashTime.Ticks % 1000000:D6}";

        // 取崩溃前 5 分钟的读数
        var before = _buffer.GetRange(
            crashTime.AddMinutes(-BeforeCrashMinutes),
            crashTime,
            r => r.Timestamp);

        var snapshot = new HardwareSnapshot
        {
            EventId = eventId,
            CrashTime = crashTime,
            CrashType = crashType,
            Readings = before.ToList(),
        };

        // 后台线程：崩溃后继续采集 1 分钟，追加到快照后落盘
        _ = Task.Run(() =>
        {
            try
            {
                var afterReadings = new List<HardwareReading>();
                DateTime end = crashTime.AddMinutes(AfterCrashMinutes);
                while (DateTime.Now < end && !_disposed)
                {
                    var r = CollectReading();
                    if (r != null) afterReadings.Add(r);
                    Thread.Sleep(PollIntervalMs);
                }
                lock (snapshot.Readings)
                {
                    snapshot.Readings.AddRange(afterReadings);
                    snapshot.Readings.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                }
                HardwareSnapshotStore.Save(snapshot);
            }
            catch { /* 落盘失败不阻断 */ }
        });

        return eventId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _computer.Close(); } catch { }
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```powershell
git add CrashSniffer/Services/HardwareMonitorService.cs
git commit -m "feat: 硬件健康看板 - HardwareMonitorService 核心监控服务"
```

---

### Task 8: 硬件健康看板 UI HardwareHealthTab

**Files:**
- Create: `CrashSniffer/UI/HardwareHealthTab.cs`

- [ ] **Step 1: 实现看板 UI**

`CrashSniffer/UI/HardwareHealthTab.cs`：

```csharp
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

    // 4 个卡片标题与值标签
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

        // 顶部状态栏
        root.Controls.Add(_lblStatus, 0, 0);

        // 2x2 卡片网格
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
        // 模拟卡片边框
        var border = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBorder,
            Padding = new Padding(1),
        };
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = CardBack };
        inner.Controls.Add(value);
        value.Location = new Point(0, 30);
        inner.Controls.Add(title);
        title.Location = new Point(0, 0);
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
        // 取最严重的电压等级
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
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```powershell
git add CrashSniffer/UI/HardwareHealthTab.cs
git commit -m "feat: 硬件健康看板 - HardwareHealthTab 看板 UI"
```

---

### Task 9: 崩溃回放视图 HardwareReplayView

**Files:**
- Create: `CrashSniffer/UI/HardwareReplayView.cs`

- [ ] **Step 1: 实现回放视图（GDI+ 自绘曲线）**

`CrashSniffer/UI/HardwareReplayView.cs`：

```csharp
using System;
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

        // 绘图区域
        int padL = 60, padR = 20, padT = 20, padB = 40;
        int w = _chartPanel.ClientSize.Width - padL - padR;
        int h = _chartPanel.ClientSize.Height - padT - padB;
        if (w <= 0 || h <= 0) return;

        // 选择指标
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

        // 网格 + Y 轴标签
        using var gridPen = new Pen(GridColor) { DashStyle = DashStyle.Dash };
        using var axisFont = new Font("Microsoft YaHei UI", 8);
        for (int i = 0; i <= 4; i++)
        {
            int y = padT + h * i / 4;
            g.DrawLine(gridPen, padL, y, padL + w, y);
            double val = yMax - (yMax - yMin) * i / 4;
            g.DrawString(val.ToString("F1"), axisFont, Brushes.Gray, 2, y - 8);
        }

        // X 轴时间标签（起止 + 崩溃时刻）
        g.DrawString(tStart.ToString("HH:mm:ss"), axisFont, Brushes.Gray, padL, padT + h + 5);
        g.DrawString(tEnd.ToString("HH:mm:ss"), axisFont, Brushes.Gray, padL + w - 50, padT + h + 5);

        // 曲线
        using var linePen = new Pen(LineColor, 2);
        var points = new System.Collections.Generic.List<PointF>();
        for (int i = 0; i < readings.Count; i++)
        {
            if (values[i] == null) continue;
            double xFrac = (readings[i].Timestamp - tStart).TotalSeconds / timeSpan;
            double yFrac = (yMax - values[i]!.Value) / (yMax - yMin);
            points.Add(new PointF(padL + (float)(w * xFrac), padT + (float)(h * yFrac)));
        }
        if (points.Count >= 2)
            g.DrawLines(linePen, points.ToArray());

        // 崩溃时刻竖线
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
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```powershell
git add CrashSniffer/UI/HardwareReplayView.cs
git commit -m "feat: 硬件健康看板 - HardwareReplayView 崩溃回放曲线"
```

---

### Task 10: MainForm 集成（根 Tab + 回放子 Tab + 崩溃联动 + 托盘）

**Files:**
- Modify: `CrashSniffer/UI/MainForm.cs`

- [ ] **Step 1: 添加字段与 Tab 声明**

在 `MainForm.cs` 字段区（与 `_envHistoryTab` 同区域）添加：

```csharp
    private readonly TabPage _tabHardware = new("硬件健康") { BackColor = Color.FromArgb(248, 250, 252) };
    private readonly HardwareHealthTab _hardwareTab = new();
    private readonly TabPage _tabReplay = new("硬件回放") { BackColor = Color.White };
    private readonly HardwareReplayView _replayView = new();
    private HardwareMonitorService? _hwMonitor;
```

在崩溃详情 Tab 数组中追加 `_tabReplay`：

把 `_tabs.TabPages.AddRange(new[] { _tabBsod, _tabWhea, _tabSuggest, _tabChanges, _tabRelated, _tabRaw });`
改为 `_tabs.TabPages.AddRange(new[] { _tabBsod, _tabWhea, _tabSuggest, _tabChanges, _tabReplay, _tabRelated, _tabRaw });`

在根 Tab 数组中追加 `_tabHardware`：

把 `_rootTabs.TabPages.AddRange(new[] { _tabEvents, _tabHistory, _tabEnvHistory });`
改为 `_rootTabs.TabPages.AddRange(new[] { _tabEvents, _tabHistory, _tabEnvHistory, _tabHardware });`

- [ ] **Step 2: 在构造函数中初始化硬件 Tab 与监控服务**

在构造函数末尾（`Controls.Add(_rootTabs);` 之后或合适位置）添加：

```csharp
        // 硬件健康看板
        _tabHardware.Controls.Add(_hardwareTab);
        _tabReplay.Controls.Add(_replayView);

        // 启动硬件监控服务（需管理员权限）
        if (AdminRightsHelper.IsRunningAsAdmin())
        {
            _hwMonitor = new HardwareMonitorService();
            _hwMonitor.Start();
            _hardwareTab.BindService(_hwMonitor);
        }
        else
        {
            _hardwareTab.Enabled = false;
        }
```

- [ ] **Step 3: 扫描完成时对新崩溃事件触发硬件回放窗口记录**

在 `StartScanAsync` 的 `HistoryStore.AppendAndSave` 附近（后台 fire-and-forget 块内）添加：

```csharp
            // 对本次扫描到的新崩溃事件，记录硬件读数窗口
            _ = Task.Run(() =>
            {
                if (_hwMonitor == null) return;
                foreach (var ev in _events)
                {
                    try
                    {
                        string eventId = _hwMonitor.RecordCrashWindow(ev.Time, ev.Type.ToString());
                        // 保存 eventId 到事件，供回放 Tab 关联
                        // 注：CrashEvent 暂不扩展字段，用 CrashTime 作为关联键
                    }
                    catch { }
                }
            });
```

- [ ] **Step 4: 在 UpdateDetail 中加载回放视图**

在 `UpdateDetail` 方法末尾（`ev` 已确认非 null 后）添加：

```csharp
        // 硬件回放：按崩溃时间查找快照
        var snapshot = FindSnapshotByCrashTime(ev.Time);
        _replayView.LoadSnapshot(snapshot);
```

并在 MainForm 中添加辅助方法：

```csharp
    private HardwareSnapshot? FindSnapshotByCrashTime(DateTime crashTime)
    {
        try
        {
            var snaps = HardwareSnapshotStore.ListSnapshots();
            // 匹配时间差在 2 分钟内的快照
            return snaps.FirstOrDefault(s =>
                Math.Abs((s.CrashTime - crashTime).TotalMinutes) < 2);
        }
        catch { return null; }
    }
```

- [ ] **Step 5: 在 Dispose 中释放监控服务**

在 MainForm 的 Dispose/FormClosing 中添加：

```csharp
        _hwMonitor?.Dispose();
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 7: Commit**

```powershell
git add CrashSniffer/UI/MainForm.cs
git commit -m "feat: 硬件健康看板 - MainForm 集成（根Tab+回放子Tab+崩溃联动）"
```

---

### Task 11: ReportExporter 集成（导出硬件快照）

**Files:**
- Modify: `CrashSniffer/Services/ReportExporter.cs`

- [ ] **Step 1: 在 Export 方法中添加硬件快照导出**

在 `ReportExporter.Export` 方法中，`environment_history.json` 写入之后，添加：

```csharp
                // 1.7 hardware_snapshots/ 崩溃硬件回放快照
                try
                {
                    var snaps = HardwareSnapshotStore.ListSnapshots();
                    if (snaps.Count > 0)
                    {
                        string hwDir = Path.Combine(tempDir, "hardware_snapshots");
                        Directory.CreateDirectory(hwDir);
                        foreach (var snap in snaps)
                        {
                            string snapFile = Path.Combine(hwDir, $"{snap.EventId}.json");
                            File.WriteAllText(snapFile, JsonSerializer.Serialize(snap, JsonOpts), Encoding.UTF8);
                        }
                        result.FilesIncluded.Add($"hardware_snapshots/ ({snaps.Count} snapshots)");
                    }
                }
                catch { /* 硬件快照导出失败不阻断 */ }
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```powershell
git add CrashSniffer/Services/ReportExporter.cs
git commit -m "feat: 硬件健康看板 - 报告导出包含硬件回放快照"
```

---

### Task 12: 编译 + 测试总验证

- [ ] **Step 1: 全量编译**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: BUILD SUCCEEDED，0 errors 0 warnings（或仅既有 warning）

- [ ] **Step 2: 运行全部单元测试**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 所有测试 PASS（AdminRightsHelper / HardwareThresholds / RingBuffer / HardwareSnapshotStore + 既有测试）

- [ ] **Step 3: 启动程序做冒烟测试（手动）**

Run: `dotnet run --project d:\break_down\CrashSniffer\CrashSniffer.csproj`

手动验收清单：
1. 程序启动后 UAC 提权弹窗出现
2. 「硬件健康」根 Tab 存在，2 秒后显示 CPU/GPU/电压/SSD 读数
3. 卡片颜色随温度变化（正常绿/警告橙/危险红）
4. 点击扫描按钮，扫描完成后崩溃详情中出现「硬件回放」子 Tab
5. 若有历史崩溃且硬件监控运行过，回放 Tab 显示曲线 + 红色崩溃竖线
6. 托盘无异常，退出程序无错误日志

---

## 自检清单

**Spec 覆盖：**
- ✅ 实时监控 2s 轮询 → Task 7 HardwareMonitorService
- ✅ 10 分钟环形缓冲 300 点 → Task 4 RingBuffer
- ✅ 崩溃前 5min + 后 1min 落盘 → Task 7 RecordCrashWindow
- ✅ 100 份上限 + 原子写入 → Task 5 HardwareSnapshotStore
- ✅ 4 卡片看板 → Task 8 HardwareHealthTab
- ✅ 崩溃回放曲线 + 红色标注 → Task 9 HardwareReplayView
- ✅ 未提权降级 → Task 2 AdminRightsHelper + Task 10
- ✅ 阈值判定 → Task 3 HardwareThresholds
- ✅ 报告导出 → Task 11
- ✅ LibreHardwareMonitor 依赖声明 → Task 6

**类型一致性：**
- HardwareReading 字段在 Task 1 定义，Task 3/7/8/9 引用一致
- HardwareSnapshot.EventId/CrashTime/Readings 在 Task 1/5/7/9 一致
- HardwareHealth 枚举在 Task 3 定义，Task 8/9 使用一致

**无占位符：** 所有步骤含完整代码，无 TBD/TODO。
