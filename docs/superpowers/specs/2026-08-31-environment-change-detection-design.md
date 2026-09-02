# 环境变更检测（「崩溃前什么变了」）设计文档

日期：2026-08-31
状态：已获用户批准（方案 A）

## 1. 背景与目标

CrashSniffer 目前在每次扫描时后台采集环境快照（MainForm.cs L281-290），但快照用完即弃，用户无法回答排障中最核心的问题：**崩溃前系统里改了什么**。本功能将环境快照持久化为历史，自动检测两次扫描之间的变更（BIOS、驱动、Windows 更新、内存频率等），并与崩溃事件关联展示。

目标用户场景：换了显卡驱动 / 刷了 BIOS / 开关 XMP 之后开始崩溃，打开 CrashSniffer 即可看到"事发前 N 天发生了什么变更"。

## 2. 数据模型

### 2.1 变更记录 `EnvironmentChange`

新文件 `CrashSniffer/Models/EnvironmentChange.cs`：

```csharp
public enum ChangeCategory
{
    Bios,            // BIOS 版本/日期
    WindowsUpdate,   // OS Build / 显示版本
    GpuDriver,       // 显卡驱动版本
    MemorySpeed,     // 内存运行频率（XMP/EXPO 开关推断）
    PlatformDriver,  // 芯片组驱动
    Hardware,        // CPU/主板/GPU/内存条硬件清单变化
    MemoryDiag,      // 内存诊断结果变化
}

public class EnvironmentChange
{
    public DateTime Time { get; set; }          // 变更被检测到的扫描时间
    public ChangeCategory Category { get; set; }
    public string Item { get; set; }            // 如 "GPU 0 (AMD RX 7800 XT)"、"BIOS"
    public string OldValue { get; set; }
    public string NewValue { get; set; }
}
```

### 2.2 快照历史记录 `SnapshotRecord`

存档中持久化的快照条目（不复用 `EnvironmentSnapshot` 原样，避免将来字段演进导致旧档反序列化失败）：

```csharp
public class SnapshotRecord
{
    public DateTime CollectedAt { get; set; }
    public string Fingerprint { get; set; }     // 内容指纹，见 §3
    public EnvironmentSnapshot Snapshot { get; set; }
}
```

## 3. 存储设计

新文件 `CrashSniffer/Services/EnvironmentHistoryStore.cs`，静态类，完全参照 `HistoryStore.cs` 的既有模式：

- 路径：`%ProgramData%\CrashSniffer\environment_history.json`
- 序列化：System.Text.Json，`WriteIndented` + UnsafeRelaxedJsonEscaping（与 HistoryStore 一致）
- 原子写入：先写 `.tmp` 再 `File.Move` 替换
- 文件损坏：备份为 `.bak` 后重建，返回空数据
- 所有公共方法 catch-all，失败不阻断扫描主流程

存档 JSON 结构：

```json
{
  "Snapshots": [ SnapshotRecord... ],
  "Changes": [ EnvironmentChange... ]
}
```

**上限（防退化，遵循项目既有教训）：**
- 快照保留最近 **100** 条
- 变更记录保留最近 **500** 条
- 超出时淘汰最旧记录

## 4. 变更检测逻辑

检测时机：MainForm 扫描完成后台任务采集到环境快照之后（现有 `_ = Task.Run(async () => ...)` 块内追加）。

检测算法位于独立静态类 `CrashSniffer/Services/EnvironmentChangeDetector.cs`，入口 `Detect(EnvironmentSnapshot old, EnvironmentSnapshot new)` 返回 `List<EnvironmentChange>`；指纹计算 `FingerprintOf(EnvironmentSnapshot)` 与之同文件：

1. 读取存档中最近一条 `SnapshotRecord`；无记录 → 本次作为基线快照入库，不产生变更
2. 计算新快照**内容指纹**：BIOS 版本+日期、OS Build+显示版本、各 GPU（名称+驱动版本+驱动日期）、各内存槽（槽位+实际运行频率）、芯片组驱动（文件名+版本）、硬件清单（CPU/主板/内存条 PartNumber 清单）拼接哈希（SHA-256 取前 16 字符即可）
3. 指纹相同 → 跳过，不入库、不产生记录（防止重复扫描堆积）
4. 指纹不同 → 追加快照 + 逐项 diff 生成变更记录，`Time` = 本次扫描时间
5. `CollectErrors` 非空的子项不参与指纹与 diff（采集失败 ≠ 变更）

逐项 diff 规则：

| 类别 | 对比字段 | 变更条目示例 |
|------|----------|--------------|
| Bios | BiosVersion, BiosDate | `BIOS: 1.2.0 → 2.1.0` |
| WindowsUpdate | OsBuild, OsDisplayVersion | `OS Build: 22621.1 → 22621.4` |
| GpuDriver | 每个 GPU 的 DriverVersion/DriverDate | `GPU 0 (RX 7800 XT) 驱动: 31.0.x → 32.0.x` |
| MemorySpeed | 每槽 ConfiguredSpeed | `插槽 DIMM2: 2133 → 6000 MT/s（XMP 疑似开启）` |
| PlatformDriver | 每个驱动 Version | `芯片组驱动 amdppm.sys: 1.x → 2.x` |
| Hardware | 硬件清单增删 | `新增内存条 / 移除 GPU` |
| MemoryDiag | ErrorsDetected/Found | `内存诊断: 无记录 → 检测到错误` |

**已知局限（UI 与报告中明示）：** 变更只能定位到「两次扫描之间」的时间段，精确时刻不可知。若两次扫描间隔很长，UI 文案注明"该变更发生在 X 与 Y 两次扫描之间"。

## 5. UI 展示

### 5.1 崩溃详情页卡片

MainForm 详情面板新增「事发前变更」区块：

- 按时间倒序列出该崩溃事件时间之前的全部变更记录
- 每条附相对时间：`崩溃前 3 天`（`ev.Time - change.Time` 天数取整，<1 天显示小时）
- 最多显示 10 条，超出显示"…另有 N 条，见环境变更页"
- 无任何变更记录时显示引导文案："尚无环境变更记录，从本次扫描起开始记录"
- 依据项目 GDI 教训：区块字体使用现有字体缓存，不新建 Font

### 5.2 独立时间线页签

新文件 `CrashSniffer/UI/EnvironmentHistoryTab.cs`（UserControl，与 HistoryTab.cs 平级，不往现有文件里塞代码）：

- MainForm TabControl 新增「环境变更」页签
- 表格列：时间 / 类别 / 项目 / 旧值 → 新值
- 数据来源：`EnvironmentHistoryStore.Load()` 的 Changes 列表，倒序
- 支持随 `OnScanCompleted()` 同步刷新（参照 HistoryTab 的既有刷新机制）
- 无数据时显示基线说明

## 6. 报告导出

`ReportExporter.cs`：

- `report.html` 每个崩溃详情区块附「事发前变更」列表（同 §5.1 规则）
- 报告尾部新增「环境变更时间线」完整章节
- zip 包新增 `environment_history.json`（结构化导出存档内容）

## 7. 约束与合规

- **零新增第三方依赖**：仅用 BCL（System.Text.Json、System.Security.Cryptography），THIRD-PARTY-NOTICES.txt 无需改动
- 所有新增 IO/解析路径 catch-all，不阻断扫描
- IDisposable 规范：本功能不引入新的需释放资源

## 8. 验证方案

1. 手工构造两份不同的快照 JSON（模拟驱动升级 / BIOS 刷新 / XMP 开启三种场景），调用 detector 验证 diff 输出
2. 指纹相同场景：连续两次相同快照，确认不产生记录
3. 冷启动场景：空存档首次入库为基线
4. UI 验证：详情卡片相对时间、页签表格、无数据引导文案
5. `dotnet build` 零警告后 `dotnet publish -c Release -r win-x64 --self-contained` 更新 publish 目录
