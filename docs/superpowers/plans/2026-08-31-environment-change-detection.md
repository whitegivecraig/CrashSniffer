# 环境变更检测（「崩溃前什么变了」）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每次扫描持久化环境快照，自动检测两次扫描之间的环境变更（BIOS/驱动/Windows 更新/内存频率/硬件），并与崩溃事件关联展示在 UI 与导出报告中。

**Architecture:** 纯逻辑层（检测器 + JSON 存档，参照 HistoryStore 既有模式，原子写入/损坏备份/失败不阻断）+ UI 层（新增「环境变更」页签 + 详情页「事发前变更」卡片）+ 报告导出扩展。检测器与存档为无 UI 依赖的静态类，可单元测试。

**Tech Stack:** .NET 8 WinForms（现有）、System.Text.Json（现有）、xunit（新增，仅测试用）。

**设计文档：** `docs/superpowers/specs/2026-08-31-environment-change-detection-design.md`

**关键既有代码约定：**
- 存档模式参照 `CrashSniffer/Services/HistoryStore.cs`（JSON + 原子写入 + .bak 备份 + catch-all）
- UI 字体一律走 `MainForm.GetCachedFont`（GDI 句柄防泄漏）
- 后台任务不阻塞 UI，UI 更新经 `BeginInvoke`
- 仓库无 .sln，所有 dotnet 命令显式指向 csproj 路径

---

### Task 1: 测试项目骨架

**Files:**
- Create: `CrashSniffer.Tests/CrashSniffer.Tests.csproj`
- Create: `CrashSniffer.Tests/EnvironmentChangeDetectorTests.cs`（本任务先放空壳）

- [ ] **Step 1: 创建测试项目文件**

`CrashSniffer.Tests/CrashSniffer.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
    <PackageReference Include="xunit" Version="2.7.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\CrashSniffer\CrashSniffer.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 创建测试类空壳（编译通过即可）**

`CrashSniffer.Tests/EnvironmentChangeDetectorTests.cs`：

```csharp
using CrashSniffer.Services;

namespace CrashSniffer.Tests;

public class EnvironmentChangeDetectorTests
{
    [Fact]
    public void Placeholder()
    {
        Assert.True(typeof(EnvironmentChangeDetector) != null);
    }
}
```

- [ ] **Step 3: 运行验证（预期编译失败：EnvironmentChangeDetector 不存在）**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 编译错误 `CS0103` 或 "未找到类型 EnvironmentChangeDetector"

- [ ] **Step 4: Commit**

```powershell
git add CrashSniffer.Tests
git commit -m "test: 添加 CrashSniffer.Tests 测试项目骨架"
```

---

### Task 2: 数据模型

**Files:**
- Create: `CrashSniffer/Models/EnvironmentChange.cs`
- Modify: `CrashSniffer/Models/EnvironmentSnapshot.cs`（get-only 集合属性补 setter，保证 JSON 反序列化可靠）

- [ ] **Step 1: 写失败测试（模型字段不存在则编译失败）**

在 `CrashSniffer.Tests/EnvironmentChangeDetectorTests.cs` 中替换为（后续任务在此基础上追加，本任务的测试是文件底座）：

```csharp
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.Tests;

public class EnvironmentChangeDetectorTests
{
    /// <summary>两份完全相同的快照 → 无变更</summary>
    [Fact]
    public void Detect_IdenticalSnapshots_ReturnsEmpty()
    {
        var changes = EnvironmentChangeDetector.Detect(BaseSnapshot(), BaseSnapshot());
        Assert.Empty(changes);
    }

    /// <summary>构造一份"完整"的基准快照，所有类别均有数据</summary>
    internal static EnvironmentSnapshot BaseSnapshot() => new()
    {
        CpuName = "AMD Ryzen 7 5700X",
        Motherboard = "B550M Mortar",
        BiosVersion = "1.2.0",
        BiosDate = "2024-01-01",
        OsName = "Windows 11 Pro",
        OsDisplayVersion = "23H2",
        OsBuild = "22621.1",
        MemorySummary = "32 GB DDR4",
        XmpNote = "XMP 已开启",
        Gpus =
        {
            new GpuInfo { Name = "AMD Radeon RX 7800 XT", DriverVersion = "31.0.24010", DriverDate = "2024-11-20", AdapterRamMb = 16384 },
        },
        // 注意：两根内存条 PartNumber 必须不同——硬件增删检测按 PartNumber 去重，
        // 相同料号无法体现"移除一根"
        MemorySticks =
        {
            new MemoryStickInfo { Slot = "DIMM2", CapacityGb = 16, Speed = 3600, ConfiguredSpeed = 3600, Manufacturer = "Kingston", PartNumber = "KF436C16RB1" },
            new MemoryStickInfo { Slot = "DIMM4", CapacityGb = 16, Speed = 3600, ConfiguredSpeed = 3600, Manufacturer = "Kingston", PartNumber = "KF436C16RB2" },
        },
        PlatformDrivers =
        {
            new PlatformDriverInfo { FileName = "amdgpio2.sys", Description = "AMD GPIO", Version = "2.2.0.130" },
        },
    };
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 编译错误（`EnvironmentChangeDetector` 未定义）

- [ ] **Step 3: 创建 `CrashSniffer/Models/EnvironmentChange.cs`**

```csharp
namespace CrashSniffer.Models;

/// <summary>
/// 环境变更类别
/// </summary>
public enum ChangeCategory
{
    /// <summary>BIOS 版本/日期刷新</summary>
    Bios = 0,

    /// <summary>Windows 版本/Build 更新</summary>
    WindowsUpdate = 1,

    /// <summary>显卡驱动版本变化</summary>
    GpuDriver = 2,

    /// <summary>内存实际运行频率变化（XMP/EXPO 开关推断）</summary>
    MemorySpeed = 3,

    /// <summary>芯片组/平台驱动版本变化</summary>
    PlatformDriver = 4,

    /// <summary>硬件清单增删（CPU/主板/GPU/内存条）</summary>
    Hardware = 5,

    /// <summary>Windows 内存诊断结果变化</summary>
    MemoryDiag = 6,
}

/// <summary>
/// 单条环境变更记录：两次扫描之间检测到的差异
/// </summary>
public class EnvironmentChange
{
    /// <summary>变更被检测到的扫描时间（即新快照的 CollectedAt）</summary>
    public DateTime Time { get; set; }

    public ChangeCategory Category { get; set; }

    /// <summary>变更对象，如 "显卡 AMD Radeon RX 7800 XT"、"BIOS"</summary>
    public string Item { get; set; } = string.Empty;

    public string OldValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;
}

/// <summary>
/// 环境历史存档中的快照条目
/// </summary>
public class SnapshotRecord
{
    public DateTime CollectedAt { get; set; }

    /// <summary>快照内容指纹，用于判断环境是否变化（见 EnvironmentChangeDetector.FingerprintOf）</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public EnvironmentSnapshot Snapshot { get; set; } = new();
}
```

- [ ] **Step 4: 给 EnvironmentSnapshot 的 get-only 集合属性补 setter**

`CrashSniffer/Models/EnvironmentSnapshot.cs` 中把以下属性从 `{ get; }` 改为 `{ get; set; }`（初始值保留 `= new()`）。原因：`EnvironmentHistoryStore` 需要把快照序列化后**反序列化**回来做对比，System.Text.Json 对无 setter 的集合属性反序列化不可靠。

改动的 5 处（L73、L74、L82、L84、L87）：

```csharp
    public List<GpuInfo> Gpus { get; set; } = new();
    public List<MemoryStickInfo> MemorySticks { get; set; } = new();
    public List<PlatformDriverInfo> PlatformDrivers { get; set; } = new();
    public MemoryDiagResult MemoryDiag { get; set; } = new();
    public List<string> CollectErrors { get; set; } = new();
```

- [ ] **Step 5: 验证主工程编译**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: 编译成功（测试工程因 Detector 未建仍会失败，只验证主工程）

- [ ] **Step 6: Commit**

```powershell
git add CrashSniffer/Models d:\break_down\CrashSniffer.Tests
git commit -m "feat: 环境变更数据模型（ChangeCategory/EnvironmentChange/SnapshotRecord）"
```

---

### Task 3: 指纹计算 FingerprintOf

**Files:**
- Create: `CrashSniffer/Services/EnvironmentChangeDetector.cs`

- [ ] **Step 1: 写失败测试**

追加到 `EnvironmentChangeDetectorTests`（Task 2 的类内）：

```csharp
    /// <summary>相同内容 → 相同指纹</summary>
    [Fact]
    public void FingerprintOf_IdenticalContent_SameFingerprint()
    {
        Assert.Equal(
            EnvironmentChangeDetector.FingerprintOf(BaseSnapshot()),
            EnvironmentChangeDetector.FingerprintOf(BaseSnapshot()));
    }

    /// <summary>驱动版本变化 → 指纹不同</summary>
    [Fact]
    public void FingerprintOf_DriverVersionChanged_DifferentFingerprint()
    {
        var a = BaseSnapshot();
        var b = BaseSnapshot();
        b.Gpus[0].DriverVersion = "32.0.23013";
        Assert.NotEqual(
            EnvironmentChangeDetector.FingerprintOf(a),
            EnvironmentChangeDetector.FingerprintOf(b));
    }

    /// <summary>一侧 GPU 列表为空（模拟 WMI 采集失败）→ 空侧不参与指纹，不误判</summary>
    [Fact]
    public void FingerprintOf_EmptyGpuSection_Ignored()
    {
        var a = BaseSnapshot();
        var b = BaseSnapshot();
        b.Gpus.Clear();
        b.CollectErrors.Add("GPU 采集失败");
        // 一侧为空时按"无该类别数据"处理：与去掉 GPU 段的指纹比较
        var c = BaseSnapshot();
        c.Gpus.Clear();
        Assert.Equal(
            EnvironmentChangeDetector.FingerprintOf(b),
            EnvironmentChangeDetector.FingerprintOf(c));
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 编译错误（FingerprintOf / EnvironmentChangeDetector 不存在）

- [ ] **Step 3: 实现 EnvironmentChangeDetector（含 FingerprintOf）**

`CrashSniffer/Services/EnvironmentChangeDetector.cs`（Detect 方法在 Task 4 追加，本任务先放占位返回空列表）：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 环境快照内容指纹计算与差异检测：
/// 对比两次扫描的快照，生成「崩溃前什么变了」的变更记录。
/// 规则：某类别数据在任一侧为空（WMI 采集失败）时，该类别不参与指纹与对比，
/// 避免"采集失败"被误判为"环境变更"
/// </summary>
public static class EnvironmentChangeDetector
{
    /// <summary>
    /// 计算快照内容指纹（SHA-256 前 8 字节的十六进制，16 字符）。
    /// 参与指纹的字段：BIOS 版本/日期、OS Build/显示版本、CPU、主板、
    /// 各 GPU 名称+驱动版本+驱动日期、各内存槽位+实际频率+PartNumber、
    /// 各平台驱动文件名+版本、内存诊断结果
    /// </summary>
    public static string FingerprintOf(EnvironmentSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.Append("BIOS=").Append(snap.BiosVersion).Append('/').Append(snap.BiosDate).Append(';');
        sb.Append("OS=").Append(snap.OsBuild).Append('/').Append(snap.OsDisplayVersion).Append(';');
        sb.Append("CPU=").Append(snap.CpuName).Append(';');
        sb.Append("MB=").Append(snap.Motherboard).Append(';');
        foreach (var gpu in snap.Gpus)
            sb.Append("GPU=").Append(gpu.Name).Append('/').Append(gpu.DriverVersion).Append('/')
              .Append(gpu.DriverDate).Append(';');
        foreach (var m in snap.MemorySticks)
            sb.Append("MEM=").Append(m.Slot).Append('/').Append(m.ConfiguredSpeed).Append('/')
              .Append(m.PartNumber).Append(';');
        foreach (var d in snap.PlatformDrivers)
            sb.Append("PDRV=").Append(d.FileName).Append('/').Append(d.Version).Append(';');
        sb.Append("DIAG=").Append(snap.MemoryDiag.Found ? (snap.MemoryDiag.ErrorsDetected ? "ERR" : "OK") : "-").Append(';');

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// 对比新旧快照，返回变更列表（Time 由调用方填充检测时间）
    /// </summary>
    public static List<EnvironmentChange> Detect(EnvironmentSnapshot oldSnap, EnvironmentSnapshot newSnap)
    {
        // Task 4 实现
        return new List<EnvironmentChange>();
    }
}
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 全部 PASS（Detect_IdenticalSnapshots 也因占位实现而通过）

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/EnvironmentChangeDetector.cs CrashSniffer.Tests/EnvironmentChangeDetectorTests.cs
git commit -m "feat: 环境快照内容指纹计算 FingerprintOf"
```

---

### Task 4: 差异检测 Detect

**Files:**
- Modify: `CrashSniffer/Services/EnvironmentChangeDetector.cs`（替换 Detect 占位实现，追加硬件清单对比）

- [ ] **Step 1: 写失败测试**

追加到 `EnvironmentChangeDetectorTests`：

```csharp
    /// <summary>BIOS 刷新 → 1 条 Bios 变更</summary>
    [Fact]
    public void Detect_BiosChanged_ReturnsBiosChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.BiosVersion = "2.1.0";
        neu.BiosDate = "2025-06-01";

        var changes = EnvironmentChangeDetector.Detect(old, neu);

        var c = Assert.Single(changes);
        Assert.Equal(ChangeCategory.Bios, c.Category);
        Assert.Contains("1.2.0", c.OldValue);
        Assert.Contains("2.1.0", c.NewValue);
    }

    /// <summary>Windows Build 变化 → WindowsUpdate 变更</summary>
    [Fact]
    public void Detect_OsBuildChanged_ReturnsWindowsUpdateChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.OsBuild = "22621.4";

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.WindowsUpdate, c.Category);
    }

    /// <summary>显卡驱动升级 → GpuDriver 变更，Item 含显卡名</summary>
    [Fact]
    public void Detect_GpuDriverChanged_ReturnsGpuDriverChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.Gpus[0].DriverVersion = "32.0.23013";
        neu.Gpus[0].DriverDate = "2025-01-15";

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.GpuDriver, c.Category);
        Assert.Contains("RX 7800 XT", c.Item);
        Assert.Contains("31.0.24010", c.OldValue);
        Assert.Contains("32.0.23013", c.NewValue);
    }

    /// <summary>内存实际频率 3600 → 2133（关 XMP）→ MemorySpeed 变更换带提示</summary>
    [Fact]
    public void Detect_MemorySpeedDown_HintsXmpOff()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.MemorySticks[0].ConfiguredSpeed = 2133;

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.MemorySpeed, c.Category);
        Assert.Contains("DIMM2", c.Item);
        Assert.Contains("2133", c.NewValue);
        Assert.Contains("XMP", c.NewValue); // 关闭提示
    }

    /// <summary>芯片组驱动版本变化 → PlatformDriver 变更</summary>
    [Fact]
    public void Detect_PlatformDriverChanged_ReturnsPlatformDriverChange()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.PlatformDrivers[0].Version = "3.1.0.55";

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.PlatformDriver, c.Category);
        Assert.Contains("amdgpio2.sys", c.Item);
    }

    /// <summary>新增显卡 + 移除内存条 → 2 条 Hardware 变更</summary>
    [Fact]
    public void Detect_HardwareAddedAndRemoved_ReturnsHardwareChanges()
    {
        var old = BaseSnapshot();
        var neu = BaseSnapshot();
        neu.Gpus.Add(new GpuInfo { Name = "Intel Arc A310", DriverVersion = "1.0.1", DriverDate = "2024-05-01" });
        neu.MemorySticks.RemoveAt(1);

        var changes = EnvironmentChangeDetector.Detect(old, neu);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeCategory.Hardware, c.Category));
        Assert.Contains(changes, c => c.Item == "新增显卡" && c.NewValue == "Intel Arc A310");
        Assert.Contains(changes, c => c.Item == "移除内存条" && c.OldValue.Contains("KF436C16RB1"));
    }

    /// <summary>内存诊断从无错误 → 有错误 → MemoryDiag 变更</summary>
    [Fact]
    public void Detect_MemoryDiagDeteriorated_ReturnsMemoryDiagChange()
    {
        var old = BaseSnapshot();
        old.MemoryDiag.Found = true;
        old.MemoryDiag.ErrorsDetected = false;
        var neu = BaseSnapshot();
        neu.MemoryDiag.Found = true;
        neu.MemoryDiag.ErrorsDetected = true;

        var c = Assert.Single(EnvironmentChangeDetector.Detect(old, neu));
        Assert.Equal(ChangeCategory.MemoryDiag, c.Category);
    }

    /// <summary>旧快照 GPU 采集失败（空列表）→ 不产生显卡相关变更（采集失败≠变更）</summary>
    [Fact]
    public void Detect_OldGpuCollectFailed_NoGpuChanges()
    {
        var old = BaseSnapshot();
        old.Gpus.Clear();
        old.CollectErrors.Add("GPU WMI 查询失败");
        var neu = BaseSnapshot();
        neu.Gpus[0].DriverVersion = "32.0.23013";

        var changes = EnvironmentChangeDetector.Detect(old, neu);
        Assert.DoesNotContain(changes, c => c.Category == ChangeCategory.GpuDriver);
        Assert.DoesNotContain(changes, c => c.Category == ChangeCategory.Hardware && c.Item.Contains("显卡"));
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: FAIL（Detect 返回空列表，各断言 `Assert.Single` 失败）

- [ ] **Step 3: 实现 Detect**

替换 `EnvironmentChangeDetector.cs` 中 Detect 占位实现，并在类末尾追加 `DetectHardwareChanges`：

```csharp
    /// <summary>
    /// 对比新旧快照，返回变更列表（Time 由调用方填充检测时间）。
    /// 只有双方都具备该类别数据时才对比，单侧缺失（采集失败）不产生变更
    /// </summary>
    public static List<EnvironmentChange> Detect(EnvironmentSnapshot oldSnap, EnvironmentSnapshot newSnap)
    {
        var changes = new List<EnvironmentChange>();

        // BIOS
        if (!string.IsNullOrEmpty(oldSnap.BiosVersion) && !string.IsNullOrEmpty(newSnap.BiosVersion)
            && (oldSnap.BiosVersion != newSnap.BiosVersion || oldSnap.BiosDate != newSnap.BiosDate))
        {
            changes.Add(new EnvironmentChange
            {
                Category = ChangeCategory.Bios,
                Item = "BIOS",
                OldValue = $"{oldSnap.BiosVersion}（{oldSnap.BiosDate}）",
                NewValue = $"{newSnap.BiosVersion}（{newSnap.BiosDate}）",
            });
        }

        // Windows 更新（Build / 显示版本）
        if (!string.IsNullOrEmpty(oldSnap.OsBuild) && !string.IsNullOrEmpty(newSnap.OsBuild)
            && (oldSnap.OsBuild != newSnap.OsBuild || oldSnap.OsDisplayVersion != newSnap.OsDisplayVersion))
        {
            changes.Add(new EnvironmentChange
            {
                Category = ChangeCategory.WindowsUpdate,
                Item = "Windows 版本",
                OldValue = $"{oldSnap.OsDisplayVersion} Build {oldSnap.OsBuild}",
                NewValue = $"{newSnap.OsDisplayVersion} Build {newSnap.OsBuild}",
            });
        }

        // 显卡驱动（按显卡名称配对）
        foreach (var newGpu in newSnap.Gpus)
        {
            var oldGpu = oldSnap.Gpus.FirstOrDefault(g => g.Name == newGpu.Name);
            if (oldGpu == null) continue; // 旧侧无此显卡（新装或清单差异），由硬件清单对比处理
            if (oldGpu.DriverVersion != newGpu.DriverVersion || oldGpu.DriverDate != newGpu.DriverDate)
            {
                changes.Add(new EnvironmentChange
                {
                    Category = ChangeCategory.GpuDriver,
                    Item = $"显卡 {newGpu.Name}",
                    OldValue = $"驱动 {oldGpu.DriverVersion}（{oldGpu.DriverDate}）",
                    NewValue = $"驱动 {newGpu.DriverVersion}（{newGpu.DriverDate}）",
                });
            }
        }

        // 内存实际运行频率（XMP/EXPO 开关推断）
        foreach (var newMem in newSnap.MemorySticks)
        {
            var oldMem = oldSnap.MemorySticks.FirstOrDefault(m => m.Slot == newMem.Slot);
            if (oldMem == null) continue;
            if (oldMem.ConfiguredSpeed != newMem.ConfiguredSpeed)
            {
                string xmpHint = newMem.ConfiguredSpeed > oldMem.ConfiguredSpeed
                    ? "（疑似开启 XMP/EXPO）"
                    : "（疑似关闭 XMP/EXPO）";
                changes.Add(new EnvironmentChange
                {
                    Category = ChangeCategory.MemorySpeed,
                    Item = $"内存 {newMem.Slot}",
                    OldValue = $"{oldMem.ConfiguredSpeed} MT/s",
                    NewValue = $"{newMem.ConfiguredSpeed} MT/s {xmpHint}",
                });
            }
        }

        // 芯片组/平台驱动（按文件名配对）
        foreach (var newDrv in newSnap.PlatformDrivers)
        {
            var oldDrv = oldSnap.PlatformDrivers.FirstOrDefault(d => d.FileName == newDrv.FileName);
            if (oldDrv == null) continue;
            if (oldDrv.Version != newDrv.Version)
            {
                changes.Add(new EnvironmentChange
                {
                    Category = ChangeCategory.PlatformDriver,
                    Item = $"芯片组驱动 {newDrv.FileName}",
                    OldValue = oldDrv.Version,
                    NewValue = newDrv.Version,
                });
            }
        }

        // 硬件清单增删
        changes.AddRange(DetectHardwareChanges(oldSnap, newSnap));

        // 内存诊断结果（双方都有记录才对比）
        if (oldSnap.MemoryDiag.Found && newSnap.MemoryDiag.Found
            && oldSnap.MemoryDiag.ErrorsDetected != newSnap.MemoryDiag.ErrorsDetected)
        {
            changes.Add(new EnvironmentChange
            {
                Category = ChangeCategory.MemoryDiag,
                Item = "Windows 内存诊断",
                OldValue = oldSnap.MemoryDiag.ErrorsDetected ? "检测到硬件错误" : "未发现错误",
                NewValue = newSnap.MemoryDiag.ErrorsDetected ? "检测到硬件错误" : "未发现错误",
            });
        }

        return changes;
    }

    /// <summary>
    /// 硬件清单增删检测：CPU / 主板 / GPU / 内存条（PartNumber）。
    /// 双方该类别都有数据才对比，避免采集失败误判为"移除硬件"
    /// </summary>
    private static IEnumerable<EnvironmentChange> DetectHardwareChanges(EnvironmentSnapshot oldSnap, EnvironmentSnapshot newSnap)
    {
        if (!string.IsNullOrEmpty(oldSnap.CpuName) && !string.IsNullOrEmpty(newSnap.CpuName)
            && oldSnap.CpuName != newSnap.CpuName)
        {
            yield return new EnvironmentChange
            { Category = ChangeCategory.Hardware, Item = "CPU", OldValue = oldSnap.CpuName, NewValue = newSnap.CpuName };
        }

        if (!string.IsNullOrEmpty(oldSnap.Motherboard) && !string.IsNullOrEmpty(newSnap.Motherboard)
            && oldSnap.Motherboard != newSnap.Motherboard)
        {
            yield return new EnvironmentChange
            { Category = ChangeCategory.Hardware, Item = "主板", OldValue = oldSnap.Motherboard, NewValue = newSnap.Motherboard };
        }

        if (oldSnap.Gpus.Count > 0 && newSnap.Gpus.Count > 0)
        {
            var oldNames = oldSnap.Gpus.Select(g => g.Name).ToHashSet();
            var newNames = newSnap.Gpus.Select(g => g.Name).ToHashSet();
            foreach (var removed in oldNames.Where(n => !newNames.Contains(n)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "移除显卡", OldValue = removed, NewValue = "-" };
            foreach (var added in newNames.Where(n => !oldNames.Contains(n)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "新增显卡", OldValue = "-", NewValue = added };
        }

        if (oldSnap.MemorySticks.Count > 0 && newSnap.MemorySticks.Count > 0)
        {
            var oldParts = oldSnap.MemorySticks.Select(m => m.PartNumber).Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
            var newParts = newSnap.MemorySticks.Select(m => m.PartNumber).Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
            foreach (var removed in oldParts.Where(p => !newParts.Contains(p)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "移除内存条", OldValue = removed, NewValue = "-" };
            foreach (var added in newParts.Where(p => !oldParts.Contains(p)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "新增内存条", OldValue = "-", NewValue = added };
        }
    }
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/EnvironmentChangeDetector.cs CrashSniffer.Tests/EnvironmentChangeDetectorTests.cs
git commit -m "feat: 环境快照差异检测 Detect（BIOS/OS/GPU驱动/内存频率/芯片组/硬件/内存诊断）"
```

---

### Task 5: 环境历史存档 EnvironmentHistoryStore

**Files:**
- Create: `CrashSniffer/Services/EnvironmentHistoryStore.cs`
- Test: `CrashSniffer.Tests/EnvironmentHistoryStoreTests.cs`

- [ ] **Step 1: 写失败测试**

`CrashSniffer.Tests/EnvironmentHistoryStoreTests.cs`：

```csharp
using CrashSniffer.Models;
using CrashSniffer.Services;

namespace CrashSniffer.Tests;

public class EnvironmentHistoryStoreTests : IDisposable
{
    private readonly string _tmpFile;

    public EnvironmentHistoryStoreTests()
    {
        _tmpFile = Path.Combine(Path.GetTempPath(), $"envhist_{Guid.NewGuid():N}.json");
        EnvironmentHistoryStore.FilePathOverrideForTests = _tmpFile;
    }

    public void Dispose()
    {
        EnvironmentHistoryStore.FilePathOverrideForTests = null;
        try { if (File.Exists(_tmpFile)) File.Delete(_tmpFile); } catch { }
        try { if (File.Exists(_tmpFile + ".bak")) File.Delete(_tmpFile + ".bak"); } catch { }
        try { if (File.Exists(_tmpFile + ".tmp")) File.Delete(_tmpFile + ".tmp"); } catch { }
    }

    /// <summary>冷启动：首份快照作为基线，不产生变更</summary>
    [Fact]
    public void RecordSnapshot_ColdStart_StoresBaselineWithoutChanges()
    {
        int added = EnvironmentHistoryStore.RecordSnapshot(EnvironmentChangeDetectorTests.BaseSnapshot());

        Assert.Equal(0, added);
        var data = EnvironmentHistoryStore.Load();
        Assert.Single(data.Snapshots);
        Assert.Empty(data.Changes);
    }

    /// <summary>环境未变（指纹相同）→ 不入库不产生记录</summary>
    [Fact]
    public void RecordSnapshot_UnchangedEnvironment_Skips()
    {
        EnvironmentHistoryStore.RecordSnapshot(EnvironmentChangeDetectorTests.BaseSnapshot());

        var snap2 = EnvironmentChangeDetectorTests.BaseSnapshot();
        snap2.CollectedAt = DateTime.Now.AddDays(1);
        int added = EnvironmentHistoryStore.RecordSnapshot(snap2);

        Assert.Equal(0, added);
        Assert.Single(EnvironmentHistoryStore.Load().Snapshots);
    }

    /// <summary>驱动升级 → 快照入库 + 1 条变更，Time 取新快照时间</summary>
    [Fact]
    public void RecordSnapshot_GpuDriverChange_RecordsChangeWithTime()
    {
        EnvironmentHistoryStore.RecordSnapshot(EnvironmentChangeDetectorTests.BaseSnapshot());

        var snap2 = EnvironmentChangeDetectorTests.BaseSnapshot();
        snap2.CollectedAt = new DateTime(2026, 8, 20, 12, 0, 0);
        snap2.Gpus[0].DriverVersion = "32.0.23013";
        int added = EnvironmentHistoryStore.RecordSnapshot(snap2);

        Assert.Equal(1, added);
        var change = EnvironmentHistoryStore.LoadChanges().Single();
        Assert.Equal(ChangeCategory.GpuDriver, change.Category);
        Assert.Equal(snap2.CollectedAt, change.Time);
    }

    /// <summary>快照超过 100 份 → 淘汰最旧</summary>
    [Fact]
    public void RecordSnapshot_OverSnapshotCap_TrimsOldest()
    {
        for (int i = 0; i < 105; i++)
        {
            var s = EnvironmentChangeDetectorTests.BaseSnapshot();
            s.BiosVersion = $"1.{i}.0"; // 每份都不同，确保入库
            s.CollectedAt = DateTime.Now.AddDays(-300 + i);
            EnvironmentHistoryStore.RecordSnapshot(s);
        }

        var data = EnvironmentHistoryStore.Load();
        Assert.Equal(100, data.Snapshots.Count);
        // 最旧被淘汰：首份 1.0.0 不在了，保留的是 1.5.0 ~ 1.104.0
        Assert.DoesNotContain(data.Snapshots, s => s.Snapshot.BiosVersion == "1.0.0");
        Assert.Contains(data.Snapshots, s => s.Snapshot.BiosVersion == "1.5.0");
    }

    /// <summary>存档损坏 → 备份 .bak 并返回空数据</summary>
    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tmpFile)!);
        File.WriteAllText(_tmpFile, "{{{ not json");

        var data = EnvironmentHistoryStore.Load();

        Assert.Empty(data.Snapshots);
        Assert.Empty(data.Changes);
        Assert.True(File.Exists(_tmpFile + ".bak"));
    }
}
```

- [ ] **Step 2: 运行验证失败**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 编译错误（EnvironmentHistoryStore 不存在）

- [ ] **Step 3: 实现 EnvironmentHistoryStore**

`CrashSniffer/Services/EnvironmentHistoryStore.cs`：

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
/// 环境历史存档数据（快照 + 变更记录）
/// </summary>
public class EnvironmentHistoryData
{
    public List<SnapshotRecord> Snapshots { get; set; } = new();
    public List<EnvironmentChange> Changes { get; set; } = new();
}

/// <summary>
/// 环境快照历史存档：每次扫描后记录快照，与最近一份做指纹对比，
/// 相同则跳过，不同则入库并生成变更记录。
/// JSON 存档 %ProgramData%\CrashSniffer\environment_history.json，
/// 原子写入、损坏自动备份重建、失败不阻断扫描（参照 HistoryStore 模式）
/// </summary>
public static class EnvironmentHistoryStore
{
    /// <summary>快照保留上限（超出淘汰最旧，防文件无限增长）</summary>
    private const int MaxSnapshots = 100;

    /// <summary>变更记录保留上限（超出淘汰最旧）</summary>
    private const int MaxChanges = 500;

    public static string StorageDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CrashSniffer");

    public static string FilePath { get; } = Path.Combine(StorageDir, "environment_history.json");

    /// <summary>测试用路径覆盖（避免测试写 %ProgramData%）</summary>
    internal static string? FilePathOverrideForTests { get; set; }

    private static string ActualFilePath => FilePathOverrideForTests ?? FilePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 读取存档；文件损坏时备份为 .bak 并返回空数据
    /// </summary>
    public static EnvironmentHistoryData Load()
    {
        try
        {
            if (!File.Exists(ActualFilePath)) return new EnvironmentHistoryData();
            string json = File.ReadAllText(ActualFilePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<EnvironmentHistoryData>(json, JsonOpts);
            if (data == null) return new EnvironmentHistoryData();
            data.Snapshots ??= new();
            data.Changes ??= new();
            return data;
        }
        catch
        {
            // 损坏：备份原文件后重建，不阻断主流程
            try
            {
                if (File.Exists(ActualFilePath))
                {
                    string bak = ActualFilePath + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(ActualFilePath, bak);
                }
            }
            catch { }
            return new EnvironmentHistoryData();
        }
    }

    /// <summary>
    /// 读取全部变更记录（按时间倒序）
    /// </summary>
    public static List<EnvironmentChange> LoadChanges()
        => Load().Changes.OrderByDescending(c => c.Time).ToList();

    /// <summary>
    /// 记录一次扫描的环境快照：与最近快照做指纹对比，相同则跳过，
    /// 不同则入库并生成变更记录（Time = 新快照采集时间）。返回本次新增变更数
    /// </summary>
    public static int RecordSnapshot(EnvironmentSnapshot snap)
    {
        try
        {
            var data = Load();
            string fp = EnvironmentChangeDetector.FingerprintOf(snap);

            var last = data.Snapshots.OrderByDescending(s => s.CollectedAt).FirstOrDefault();
            if (last != null && last.Fingerprint == fp)
                return 0; // 环境未变，不重复入库

            int added = 0;
            if (last != null)
            {
                foreach (var c in EnvironmentChangeDetector.Detect(last.Snapshot, snap))
                {
                    c.Time = snap.CollectedAt;
                    data.Changes.Add(c);
                    added++;
                }
            }

            data.Snapshots.Add(new SnapshotRecord
            {
                CollectedAt = snap.CollectedAt,
                Fingerprint = fp,
                Snapshot = snap,
            });

            // 上限淘汰
            if (data.Snapshots.Count > MaxSnapshots)
                data.Snapshots = data.Snapshots
                    .OrderByDescending(s => s.CollectedAt).Take(MaxSnapshots).ToList();
            if (data.Changes.Count > MaxChanges)
                data.Changes = data.Changes
                    .OrderByDescending(c => c.Time).Take(MaxChanges).ToList();

            Save(data);
            return added;
        }
        catch
        {
            // 环境历史失败不影响主流程
            return 0;
        }
    }

    /// <summary>原子写入：先写临时文件再替换，避免写一半留下损坏文件</summary>
    private static void Save(EnvironmentHistoryData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ActualFilePath)!);
        string json = JsonSerializer.Serialize(data, JsonOpts);
        string tmp = ActualFilePath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (File.Exists(ActualFilePath)) File.Delete(ActualFilePath);
        File.Move(tmp, ActualFilePath);
    }
}
```

- [ ] **Step 4: 运行验证通过**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```powershell
git add CrashSniffer/Services/EnvironmentHistoryStore.cs CrashSniffer.Tests/EnvironmentHistoryStoreTests.cs
git commit -m "feat: 环境历史存档（快照入库/指纹跳过/变更记录/上限淘汰/原子写入）"
```

---

### Task 6: 「环境变更」页签 EnvironmentHistoryTab

**Files:**
- Create: `CrashSniffer/UI/EnvironmentHistoryTab.cs`

本任务是 WinForms UI，无单元测试（无 UI 测试基础设施），验证方式为编译 + 人工检查。

- [ ] **Step 1: 创建控件**

`CrashSniffer/UI/EnvironmentHistoryTab.cs`：

```csharp
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
            row.Cells[1].Value = CategoryLabel(c.Category);
            row.Cells[2].Value = c.Item;
            row.Cells[3].Value = c.OldValue;
            row.Cells[4].Value = c.NewValue;
        }
    }

    /// <summary>变更类别的中文显示名（详情页复用）</summary>
    internal static string CategoryLabel(ChangeCategory c) => c switch
    {
        ChangeCategory.Bios => "BIOS",
        ChangeCategory.WindowsUpdate => "Windows 更新",
        ChangeCategory.GpuDriver => "显卡驱动",
        ChangeCategory.MemorySpeed => "内存频率",
        ChangeCategory.PlatformDriver => "芯片组驱动",
        ChangeCategory.Hardware => "硬件变更",
        ChangeCategory.MemoryDiag => "内存诊断",
        _ => c.ToString(),
    };
}
```

- [ ] **Step 2: 验证编译**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: 编译成功零警告

- [ ] **Step 3: Commit**

```powershell
git add CrashSniffer/UI/EnvironmentHistoryTab.cs
git commit -m "feat: 环境变更页签控件（时间线表格 + 扫描完成刷新）"
```

---

### Task 7: MainForm 集成（页签 / 扫描流程 / 详情卡片 / 导出传参）

**Files:**
- Modify: `CrashSniffer/UI/MainForm.cs`（多处小改）

- [ ] **Step 1: 新增控件与数据字段**

在 MainForm 字段区（`_tabRaw` 声明附近，L44 之后）追加：

```csharp
    private readonly TabPage _tabChanges = new("事发前变更") { BackColor = Color.White };
    private readonly RichTextBox _tbChanges = new() { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.White, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical };
```

在 `_tabHistory` 声明后（L36 之后）追加：

```csharp
    private readonly TabPage _tabEnvHistory = new("环境变更") { BackColor = Color.FromArgb(248, 250, 252) };
    private readonly EnvironmentHistoryTab _envHistoryTab = new();
```

在 `_envSnapshot` 字段（L64）后追加：

```csharp
    /// <summary>环境变更记录缓存（详情页「事发前变更」关联展示用）</summary>
    private List<EnvironmentChange> _envChanges = new();
```

- [ ] **Step 2: BuildUi 装配页签**

`BuildUi()` 中，将 `_tabs.TabPages.AddRange(new[] { _tabBsod, _tabWhea, _tabSuggest, _tabRelated, _tabRaw });`（L163）改为：

```csharp
        _tabChanges.Controls.Add(_tbChanges);
        _tabs.TabPages.AddRange(new[] { _tabBsod, _tabWhea, _tabSuggest, _tabChanges, _tabRelated, _tabRaw });
```

将 `_rootTabs.TabPages.AddRange(new[] { _tabEvents, _tabHistory });`（L177）改为：

```csharp
        _tabEnvHistory.Controls.Add(_envHistoryTab);
        _rootTabs.TabPages.AddRange(new[] { _tabEvents, _tabHistory, _tabEnvHistory });
```

- [ ] **Step 3: 构造函数加载变更缓存**

`MainForm()` 构造函数中 `BuildUi(); BindEvents();`（L84-85）之后追加：

```csharp
        // 启动时加载环境变更缓存（供详情页关联展示）
        try { _envChanges = EnvironmentHistoryStore.LoadChanges(); } catch { }
```

- [ ] **Step 4: 扫描流程接入快照入库**

`RefreshScan()` 中的环境快照后台任务块（L281-290）整体替换为：

```csharp
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
```

- [ ] **Step 5: UpdateDetail 渲染「事发前变更」卡片**

`UpdateDetail()` 中，在「原始消息」区块（`_tbRaw.Clear();` L426 之前）插入：

```csharp
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
                AppendLine(ch, $"{RelativeBefore(ev.Time, c.Time)} · {EnvironmentHistoryTab.CategoryLabel(c.Category)}",
                    Color.FromArgb(0x1e, 0x3a, 0x8a), bold: true);
                AppendLine(ch, $"  {c.Item}:  {c.OldValue}  →  {c.NewValue}", Color.FromArgb(0x1f, 0x29, 0x37));
            }
            if (before.Count > show)
                AppendLine(ch, $"…另有 {before.Count - show} 条，见「环境变更」页", Color.Gray);
        }
```

- [ ] **Step 6: ClearDetail 清空新文本框**

`ClearDetail()` 中 `foreach (var tb in new[] { _tbBsod, _tbWhea, _tbSuggest, _tbRaw }) tb.Clear();`（L439）改为：

```csharp
        foreach (var tb in new[] { _tbBsod, _tbWhea, _tbSuggest, _tbRaw, _tbChanges }) tb.Clear();
```

- [ ] **Step 7: 追加相对时间辅助方法**

在 MainForm「通用工具」区（`GetCachedFont` 附近）追加：

```csharp
    /// <summary>变更时间相对崩溃时间的表述，如 "崩溃前 3 天"</summary>
    private static string RelativeBefore(DateTime crash, DateTime change)
    {
        var span = crash - change;
        if (span <= TimeSpan.Zero) return "崩溃同时";
        if (span.TotalDays >= 1) return $"崩溃前 {(int)span.TotalDays} 天";
        return $"崩溃前 {Math.Max(1, (int)span.TotalHours)} 小时";
    }
```

- [ ] **Step 8: DoExport 传入环境历史**

`DoExport()` 中 `var result = ReportExporter.Export(...)`（L470）一行改为（ReportExporter 的扩展签名在 Task 8 实现）：

```csharp
            EnvironmentHistoryData envHistory;
            try { envHistory = EnvironmentHistoryStore.Load(); } catch { envHistory = new EnvironmentHistoryData(); }
            var result = ReportExporter.Export(dlg.FileName, _events, _dtpStart.Value, _dtpEnd.Value, env, ranking, envHistory);
```

- [ ] **Step 9: 验证编译（需 Task 8 的签名先就位，或与 Task 8 合并后统一编译）**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: 编译成功零警告

- [ ] **Step 10: Commit（若与 Task 8 合并验证，则合并提交）**

```powershell
git add CrashSniffer/UI/MainForm.cs
git commit -m "feat: MainForm 集成环境变更（页签/扫描入库/详情卡片/导出传参）"
```

---

### Task 8: 报告导出扩展 ReportExporter

**Files:**
- Modify: `CrashSniffer/Services/ReportExporter.cs`

- [ ] **Step 1: Export 签名与 environment_history.json**

`Export` 方法签名（L32-33）改为：

```csharp
    public static ExportResult Export(string zipPath, List<CrashEvent> events, DateTime startTime, DateTime endTime,
        EnvironmentSnapshot? env = null, List<SuspectRankEntry>? suspectRanking = null,
        EnvironmentHistoryData? envHistory = null)
```

在 environment.json 写入块（L50-56）之后追加：

```csharp
                // 1.6 environment_history.json (环境变更历史)
                if (envHistory != null)
                {
                    string envHistPath = Path.Combine(tempDir, "environment_history.json");
                    File.WriteAllText(envHistPath, JsonSerializer.Serialize(envHistory, JsonOpts), Encoding.UTF8);
                    result.FilesIncluded.Add("environment_history.json");
                }
```

- [ ] **Step 2: BuildHtml 签名与逐事件变更卡片**

`BuildHtml` 签名（L124-125）改为：

```csharp
    private static string BuildHtml(List<CrashEvent> events, DateTime start, DateTime end, ExportResult progress,
        EnvironmentSnapshot? env = null, List<SuspectRankEntry>? suspectRanking = null,
        EnvironmentHistoryData? envHistory = null)
```

调用处（L91）同步加参数：

```csharp
                File.WriteAllText(htmlPath, BuildHtml(events, startTime, endTime, result, env, suspectRanking, envHistory), Encoding.UTF8);
```

在逐条详情的 WHEA 卡片块（L203-212）之后、「原始消息」之前插入：

```csharp
            // 事发前变更
            if (envHistory is { Changes.Count: > 0 })
            {
                var before = envHistory.Changes.Where(c => c.Time <= ev.Time)
                    .OrderByDescending(c => c.Time).ToList();
                if (before.Count > 0)
                {
                    sb.AppendLine("<div class=\"card\"><h3 style=\"margin:0 0 6px;color:var(--accent);font-size:15px\">🕐 事发前环境变更</h3>");
                    sb.AppendLine("<p class=\"meta\" style=\"margin:0 0 8px\">变更只能定位到两次扫描之间，精确时刻不可知</p>");
                    int show = Math.Min(10, before.Count);
                    for (int k = 0; k < show; k++)
                    {
                        var c = before[k];
                        sb.AppendLine($"<div class=\"row\"><span class=\"k\">{Escape(RelativeBefore(ev.Time, c.Time))}</span><span class=\"v\">{Escape(c.Item)}（{CategoryName(c.Category)}）：<span class=\"mono\">{Escape(c.OldValue)}</span> → <span class=\"mono warn\">{Escape(c.NewValue)}</span></span></div>");
                    }
                    if (before.Count > show)
                        sb.AppendLine($"<p class=\"meta\" style=\"margin:8px 0 0\">另有 {before.Count - show} 条更早变更，见文末「环境变更时间线」。</p>");
                    sb.AppendLine("</div>");
                }
            }
```

- [ ] **Step 3: 报告尾部完整时间线章节**

在「跳过的 dump」区块（L246-252）之后、footer 之前插入：

```csharp
        // 环境变更时间线（完整章节）
        if (envHistory != null)
        {
            if (envHistory.Changes.Count > 0)
            {
                sb.AppendLine("<section class=\"stat\"><h3 style=\"margin:0 0 10px;color:var(--accent)\">🕐 环境变更时间线</h3>");
                sb.AppendLine("<table class=\"rel-tbl\"><thead><tr><th>检测时间</th><th>类别</th><th>项目</th><th>旧值</th><th>新值</th></tr></thead><tbody>");
                foreach (var c in envHistory.Changes.OrderByDescending(c => c.Time))
                    sb.AppendLine($"<tr><td>{c.Time:yyyy-MM-dd HH:mm}</td><td>{CategoryName(c.Category)}</td><td>{Escape(c.Item)}</td><td class=\"mono\">{Escape(c.OldValue)}</td><td class=\"mono warn\">{Escape(c.NewValue)}</td></tr>");
                sb.AppendLine("</tbody></table>");
                sb.AppendLine($"<p class=\"meta\" style=\"margin:8px 0 0\">共 {envHistory.Changes.Count} 条变更 · {envHistory.Snapshots.Count} 份环境快照</p>");
                sb.AppendLine("</section>");
            }
            else if (envHistory.Snapshots.Count > 0)
            {
                sb.AppendLine("<section class=\"stat\"><h3 style=\"margin:0 0 10px;color:var(--accent)\">🕐 环境变更时间线</h3><p class=\"meta\">已有环境快照记录，暂未检测到任何变更。</p></section>");
            }
        }
```

- [ ] **Step 4: 追加辅助方法**

在 `TypeName` 方法附近追加：

```csharp
    private static string CategoryName(ChangeCategory c) => c switch
    {
        ChangeCategory.Bios => "BIOS",
        ChangeCategory.WindowsUpdate => "Windows 更新",
        ChangeCategory.GpuDriver => "显卡驱动",
        ChangeCategory.MemorySpeed => "内存频率",
        ChangeCategory.PlatformDriver => "芯片组驱动",
        ChangeCategory.Hardware => "硬件变更",
        ChangeCategory.MemoryDiag => "内存诊断",
        _ => c.ToString(),
    };

    private static string RelativeBefore(DateTime crash, DateTime change)
    {
        var span = crash - change;
        if (span <= TimeSpan.Zero) return "崩溃同时";
        if (span.TotalDays >= 1) return $"崩溃前 {(int)span.TotalDays} 天";
        return $"崩溃前 {Math.Max(1, (int)span.TotalHours)} 小时";
    }
```

- [ ] **Step 5: 编译 + 全量测试**

Run: `dotnet build d:\break_down\CrashSniffer\CrashSniffer.csproj`
Expected: 编译成功零警告

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 全部 PASS

- [ ] **Step 6: Commit**

```powershell
git add CrashSniffer/Services/ReportExporter.cs CrashSniffer/UI/MainForm.cs
git commit -m "feat: 报告导出环境变更（事发前变更卡片/时间线/environment_history.json）"
```

---

### Task 9: 合规声明、README 与发布

**Files:**
- Modify: `THIRD-PARTY-NOTICES.txt`（补测试依赖声明）
- Modify: `README.md`（功能特性 + 更新日志）

- [ ] **Step 1: THIRD-PARTY-NOTICES.txt 补测试框架声明**

在「4. System.Management」块之后、「说明」块之前插入（延续编号；"说明"段中的"以上四个组件"相应改为"以上组件"）：

```text
-----------------------------------------------------------------------
5. xunit / Microsoft.NET.Test.Sdk / xunit.runner.visualstudio
-----------------------------------------------------------------------

组件名称：xunit 2.7.0、xunit.runner.visualstudio 2.5.7（Apache License 2.0）
          Microsoft.NET.Test.Sdk 17.9.0（MIT License）
用途说明：仅用于本仓库源码的单元测试（CrashSniffer.Tests 工程引用），
          不随 CrashSniffer.exe 发行包分发，不参与最终二进制产物。
来源：https://www.nuget.org/packages/xunit/
      https://www.nuget.org/packages/xunit.runner.visualstudio/
      https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/

Apache License 2.0 摘要：允许商用、修改与分发，需保留版权、许可与声明，
并注明修改。完整许可文本见 https://www.apache.org/licenses/LICENSE-2.0
Microsoft.NET.Test.Sdk 为 MIT License，许可文本同上（MIT License）。
```

- [ ] **Step 2: README.md 更新**

「✨ 功能特性」的「智能分析」小节追加一条：

```markdown
- 环境变更检测：每次扫描自动记录硬件/驱动快照，BIOS 刷新、驱动升级、Windows 更新、XMP 开关等变更一目了然，崩溃详情自动关联「事发前变更」
```

「📝 更新日志」顶部（`### v2.0.1` 之前）插入：

```markdown
### v2.1.0
- **新增**：环境变更检测 —— 每次扫描持久化环境快照，自动检测两次扫描之间的 BIOS / Windows 更新 / 显卡驱动 / 内存频率（XMP）/ 芯片组驱动 / 硬件清单变更
- **新增**：崩溃详情「事发前变更」卡片（自动关联崩溃前变更，附相对时间）与独立「环境变更」时间线页签
- **新增**：报告包新增 environment_history.json，HTML 报告附事发前变更与完整变更时间线
```

- [ ] **Step 3: 全量验证**

Run: `dotnet test d:\break_down\CrashSniffer.Tests\CrashSniffer.Tests.csproj`
Expected: 全部 PASS

Run: `dotnet publish d:\break_down\CrashSniffer\CrashSniffer.csproj -c Release -r win-x64 --self-contained -o d:\break_down\publish`
Expected: publish 目录更新（CrashSniffer.exe 等）

- [ ] **Step 4: 人工冒烟（运行管理员权限的 exe）**

- 启动 → 首次扫描后「环境变更」页显示"已有 1 份环境快照 · 暂未检测到任何变更"
- （可选）手动改动任一环境（如升级驱动）后再次扫描 → 页签出现 1 条变更
- 选择一条崩溃事件 → 详情「事发前变更」卡片显示相对时间与引导文案
- 导出报告包 → 检查 report.html 含时间线章节、zip 含 environment_history.json

- [ ] **Step 5: Commit**

```powershell
git add THIRD-PARTY-NOTICES.txt README.md
git commit -m "docs: 登记测试框架依赖并更新 v2.1.0 功能说明"
```

---

## 验收对照（设计文档 §8）

| 设计要求 | 对应任务 |
|----------|----------|
| 数据模型 EnvironmentChange/SnapshotRecord | Task 2 |
| 存档模式（原子写入/.bak/失败不阻断/100+500 上限） | Task 5 |
| 指纹相同跳过 / 冷启动基线 / 采集失败不误判 | Task 3、4、5 测试覆盖 |
| 详情页卡片（倒序/相对时间/10 条上限/引导文案） | Task 7 |
| 独立时间线页签 | Task 6、7 |
| report.html 两处展示 + environment_history.json | Task 8 |
| 零新增运行时依赖 | Task 1（仅测试依赖，Task 9 登记） |
| dotnet build + publish | Task 9 |
