# 硬件健康看板（实时监控 + 崩溃回放）设计文档

日期：2026-09-17
状态：已获用户批准（方案 A：单 Tab 看板 + 崩溃详情内嵌硬件回放）

## 1. 背景与目标

CrashSniffer 目前能抓取崩溃事件、解析 BugCheck、检测环境变更，但缺少**硬件运行状态**这一关键维度。WHEA_UNCORRECTABLE_ERROR (0x124)、VIDEO_TDR_FAILURE (0x141)、CLOCK_WATCHDOG_TIMEOUT (0x101) 等停止代码本质是硬件级错误，排障时最核心的问题是：**崩溃发生瞬间硬件的温度、电压、频率是多少？是否超阈值？**

本功能新增「硬件健康」看板，通过 LibreHardwareMonitor 以 2s 间隔实时采集 CPU/GPU/主板/硬盘的核心指标，维护最近 10 分钟内存环形缓冲，并在检测到系统崩溃事件时把崩溃前后窗口的硬件读数落盘，供崩溃详情页回放。

目标用户场景：
- 日常监控：打开看板一眼看到 CPU/GPU 温度、电压、风扇转速是否正常。
- 崩溃排障：点开某次 WHEA 124 崩溃，回放显示崩溃前 5 分钟 CPU 温度从 70°C 飙到 96°C，直接定位为过热/超频不稳。

## 2. 架构与模块划分

### 2.1 新增模块

| 模块 | 职责 | 关键依赖 |
|---|---|---|
| `HardwareMonitorService` | 单例后台服务。封装 LibreHardwareMonitor `Computer`，2s 轮询采集核心传感器；维护内存环形缓冲（最近 10 分钟）；提供 `IProgress<HardwareReading>` 订阅；接收崩溃信号后落盘前后窗口 | LibreHardwareMonitorLib |
| `HardwareHealthTab`（UserControl） | 根 Tab「硬件健康」。分 CPU/GPU/主板/硬盘四块卡片，显示当前值 + 迷你曲线；超阈值变红 | `HardwareMonitorService` |
| `HardwareReplayView`（UserControl） | 崩溃详情「硬件回放」子 Tab。加载某次崩溃的落盘快照，绘制时间轴曲线，标注崩溃时刻 | `HardwareSnapshotStore` |
| `HardwareSnapshotStore` | 崩溃窗口快照落盘（JSON）到 `%LOCALAPPDATA%\CrashSniffer\hardware-snapshots\`，按崩溃事件 ID 索引；上限 100 份，超限淘汰最旧 | — |
| `HardwareThresholds` | 内置默认阈值表（CPU/GPU 温度、12V 电压±5%、硬盘健康度等），支持从设置文件覆盖 | — |
| `AdminRightsHelper` | 启动时检测是否管理员；未提权时弹窗提示 + 提供「以管理员重启」按钮（`runas`） | — |

### 2.2 与现有代码的集成点

1. **MainForm**：根 `TabControl` 在「环境变更」后追加第 4 个 Tab「硬件健康」→ 挂载 `HardwareHealthTab`；详情子 `_tabs` 在「原始消息」前插入「硬件回放」→ 挂载 `HardwareReplayView`。
2. **崩溃检测联动**：`CrashAggregator` 聚合出新 `CrashEvent`（或 `EventLogCollector` 扫到 BugCheck 1001 / Kernel-Power 41）时，调用 `HardwareMonitorService.RecordCrashWindow(eventId, crashTime)` 触发落盘。
3. **托盘常驻**：MainForm 加 `NotifyIcon`，关闭主窗弹「最小化到托盘 / 退出」，默认最小化到托盘；托盘菜单含「显示主窗」「当前硬件概览」「退出」。
4. **ReportExporter**：导出报告包时，把该次崩溃关联的硬件快照 JSON 一并打包。

### 2.3 设计原则

- **后台服务与 UI 解耦**：service 的 `Timer` 回调跑在线程池，通过 `IProgress<T>`（`Progress<T>` 自动捕获 `SynchronizationContext`）把数据推回 UI 线程，UI 层不直接碰硬件、不加锁。
- **单例 + 锁**：`HardwareMonitorService` 用 `Lazy<T>` 单例；环形缓冲 `Write`/`ReadSnapshot` 用 `lock` 保护（参考 HistoryStore 锁约束）。
- **只缓冲核心指标**：2s × 10min = 300 点，每点只含看板关心的核心指标（温度/电压/频率/功耗/风扇/健康度），不缓冲全部传感器，控制内存。

## 3. 数据流与崩溃回放机制

### 3.1 实时采集数据流

```
HardwareMonitorService (后台单例)
  │
  ├─ System.Threading.Timer (2s 周期, 线程池)
  │     └─ Update() ← lock(_bufferLock)
  │           ├─ computer.Update()           ← LibreHardwareMonitor 刷新
  │           ├─ 提取核心指标白名单 → HardwareReading[]
  │           ├─ 写入环形缓冲 RingBuffer<HardwareReading[]> (容量 300)
  │           └─ IProgress<HardwareReading[]> 推回 UI 线程
  │
  └─ UI 订阅: HardwareHealthTab 更新卡片数值 + 迷你曲线
```

核心指标白名单：
- CPU：封装温度、各核心温度、Vcore、SOC 电压、核心频率、总线频率、Package 功耗
- GPU：温度、核心频率、显存频率、风扇转速、功耗、负载
- 主板：CPU 风扇、机箱风扇转速、+3.3V/+5V/+12V、主板温度
- 硬盘：各盘温度、SMART 健康度、剩余寿命、通电时间

### 3.2 崩溃窗口落盘

触发时机：`CrashAggregator` 产出新 `CrashEvent` 时立即调用：

```csharp
HardwareMonitorService.Instance.RecordCrashWindow(
    crashEventId: evt.Id,
    crashTime: evt.Time,
    beforeMinutes: 5,
    afterMinutes: 1);
```

落盘逻辑：
1. 立即从环形缓冲取出 `[crashTime - 5min, crashTime]` 的点。
2. 启动 1 分钟延迟补采任务，继续记录 `[crashTime, crashTime + 1min]` 的点。
3. 合并两段 → JSON → `HardwareSnapshotStore.Save(eventId, snapshot)`。

快照 JSON 格式：

```json
{
  "eventId": "2026-09-17T10:23:45-bsod-0x124",
  "crashTime": "2026-09-17T10:23:45.123Z",
  "beforeSeconds": 300,
  "afterSeconds": 60,
  "readings": [
    { "t": "2026-09-17T10:18:45Z", "cpuTemp": 72.3, "cpuVcore": 1.25, "gpuTemp": 58.1, "fan1": 1200, "v12v": 12.04 }
  ]
}
```

约束（对齐项目 memory 硬上限风格）：
- 每份快照最多 330 点（前 300 + 后 30）。
- 磁盘最多保留 **100 份**，超限淘汰最旧。
- 写入用原子 `File.Move` 覆盖（先写 `.tmp`，成功后 `File.Move(tmp, final, overwrite: true)`）。

### 3.3 崩溃回放视图

`HardwareReplayView`（崩溃详情子 Tab）：
1. `UpdateDetail(CrashEvent evt)` 时按 `evt.Id` 从 `HardwareSnapshotStore.Load(evt.Id)` 取快照。
2. 无快照 → 显示占位提示「此次崩溃发生时未捕获硬件数据」。
3. 有快照 → 自绘 `Panel` 或 `Chart` 绘制温度/电压/风扇分组曲线，X 轴为相对崩溃时刻秒数，t=0 处画红色竖线 + ▼ 标注。
4. 鼠标悬停显示数值 tooltip；支持时间窗口切换（前 5min / 1min / 30s）。

### 3.4 与崩溃事件的关联

`MainForm.UpdateDetail()` 选中某条崩溃事件后，「硬件回放」Tab 同步调用 `HardwareReplayView.LoadSnapshot(evt.Id)`。

关键时序：系统崩溃重启后，`EventLogCollector` 扫描事件日志发现 BugCheck 1001 / Kernel-Power 41 → 立即触发 `RecordCrashWindow` 落盘（不等用户点开详情），避免环形缓冲被新数据覆盖。

## 4. UI 布局与视觉

### 4.1 硬件健康看板（根 Tab）

- 顶部**状态条**：监控状态指示灯（实时采集中 / 已停止 / 未授权 / 失败）+ 最近更新时间 + 阈值设置入口。
- 下方 **2×2 卡片网格**：CPU / GPU / 主板·SuperIO / 硬盘 SMART。
- 每张卡片：标题+设备型号 → 3~4 个大字号数值块 → 迷你趋势曲线（最近 10 分钟）→ 辅助信息小字。

视觉规范：
- 沿用 MainForm 已有颜色常量：CPU=`#c2410c`、GPU=`#6d28d9`、主板=`#0e7493`、硬盘=`#047857`。
- 数值字体 `Consolas`，标签 `Microsoft YaHei UI`（与现有 DataGrid 统一）。
- 超阈值态：数值变红、卡片边框变红、卡片顶部红色告警条。

### 4.2 崩溃硬件回放（详情子 Tab）

- 头部：崩溃事件摘要（停止代码 + 时间）+ 时间窗口下拉。
- 主体：分组曲线图（温度组 / 电压组 / 风扇组），t=0 红色竖线 + ▼ 标注「崩溃时刻」。
- 底部：崩溃时刻数值快照表。

### 4.3 与现有 UI 集成

- 根 Tab：崩溃事件 / 历史趋势 / 环境变更 / **硬件健康**。
- 详情子 Tab：BSOD / WHEA / 排障建议 / 事发前变更 / 关联事件 / **硬件回放** / 原始消息。
- 启动未提权：`AdminRightsHelper` 弹窗 + 「以管理员重启」，硬件看板 Tab 显示占位卡片。

## 5. 错误处理与边界情况

### 5.1 管理员权限缺失

- `Program.Main` 用 `WindowsPrincipal` + `WindowsBuiltInRole.Administrator` 检测。
- 未提权时主窗体正常启动（崩溃扫描等功能不受影响），「硬件健康」Tab 显示占位卡片 + 「以管理员身份重启」按钮（`ProcessStartInfo.Verb="runas"`）。

### 5.2 LibreHardwareMonitor 初始化失败

- `Computer.Open()` 异常 → `Start()` catch 后状态变「失败」，卡片显示「硬件数据读取失败」+ 异常消息，提供「重试」按钮，不弹模态框。
- catch 时用 `GetBaseException().Message` 提取有意义错误（项目 memory 约束）。

### 5.3 传感器缺失 / 硬件不支持

- 拿不到的指标显示「—」而非崩溃；`Sensor.Value` 为 `null` 统一转 `double?`，UI 层对 `null` 显示占位。
- 不支持 SMART 的硬盘显示「不支持 SMART」。

### 5.4 崩溃窗口落盘失败

- 失败时静默 catch 并更新状态条，不弹框（崩溃本身已是异常状态）。
- 原子写入：先写 `.tmp`，成功后 `File.Move` 覆盖。
- 环形缓冲不足 5 分钟（程序刚启动）时，`beforeSeconds` 记录实际时长。

### 5.5 线程安全

- `RingBuffer<T>` 的 `Write`/`ReadSnapshot` 用 `lock(_syncRoot)` 保护。
- `RecordCrashWindow` 与 `Update()` 并发由同一把锁保护。
- UI 通过 `IProgress<T>` 自动封送，UI 层不加锁。

### 5.6 资源与上限

| 资源 | 上限 | 超限处理 |
|---|---|---|
| 内存环形缓冲 | 300 点 | 覆盖最旧 |
| 落盘快照数 | 100 份 | 淘汰最旧 |
| 单份快照点数 | 330 点 | 截断 |
| 看板迷你曲线 | 300 点 | 降采样到曲线宽度 |

- `Computer` 在程序退出时 `Dispose()`，释放 Ring0 驱动（IDisposable 约束）。
- `NotifyIcon` 退出时 `Dispose()`，避免托盘图标残留。

### 5.7 退出清理

- `FormClosing`：弹「最小化到托盘 / 退出」。
- 真正退出：`HardwareMonitorService.Stop()` → `Computer.Close()` → `Dispose()`；`NotifyIcon.Dispose()`；落盘所有未落盘窗口。

## 6. 测试策略与验收标准

### 6.1 测试分层

| 层级 | 对象 | 工具 |
|---|---|---|
| 单元测试 | `RingBuffer<T>`、`HardwareSnapshotStore`、`HardwareThresholds`、`AdminRightsHelper` | CrashSniffer.Tests（xUnit） |
| 集成测试 | `HardwareMonitorService` 采集/缓冲/落盘流程 | xUnit + mock `ISensor` |
| UI 手动测试 | 看板、回放、托盘、提权弹窗 | 人工验收清单 |

### 6.2 单元测试用例

**RingBuffer\<T\>**：写入 301 点后最旧被覆盖、Count=300；并发 Write + ReadSnapshot 不抛异常；空缓冲返回空数组。

**HardwareSnapshotStore**：Save/Load 往返一致；超过 100 份最旧被删；写入中途异常不留半截文件；Load 不存在返回 null。

**HardwareThresholds**：默认阈值（CPU 95°C、GPU 85°C、12V ±5%、硬盘健康度 80%）；超阈值判断正确；自定义阈值覆盖默认。

**AdminRightsHelper**：非管理员进程返回 false；`RelaunchAsAdmin()` 返回的 ProcessStartInfo 含 `Verb="runas"`。

### 6.3 集成测试（mock 硬件）

- mock `IComputer` 固定传感器值 → Start 后 2s 内 ReadSnapshot 有值。
- 触发 RecordCrashWindow → 快照落盘，beforeSeconds ≤ 300。
- Computer.Open() 抛异常 → Start 不抛，状态为失败，GetLastError 返回消息。
- 传感器 Value == null → 对应字段 null，不抛。

### 6.4 UI 手动验收清单

- [ ] 管理员启动 → 状态条「实时采集中」，四类卡片有数值。
- [ ] 非管理员启动 → 看板占位卡片，点「以管理员重启」后提权重启。
- [ ] 数值每 2s 刷新，迷你曲线滚动。
- [ ] CPU 超 95°C → 数值变红、边框变红。
- [ ] 关闭主窗 → 弹「最小化到托盘 / 退出」，最小化后托盘有图标。
- [ ] 托盘右键 → 「显示主窗」恢复、「退出」彻底关闭。
- [ ] 崩溃事件详情「硬件回放」有曲线，t=0 红色竖线标注。
- [ ] 切换时间窗口 → 曲线重绘。
- [ ] 无快照事件 → 显示「未捕获硬件数据」。
- [ ] 持续运行 30min → 内存不持续增长。
- [ ] 落盘目录超 100 份 → 最旧被自动删除。

### 6.5 验收标准（完成定义）

1. 功能完整：看板实时显示四类核心指标；崩溃详情可回放硬件曲线；托盘常驻工作。
2. 容错到位：未提权、硬件不支持、初始化失败、落盘失败均降级处理，不崩溃、不弹模态框。
3. 资源合规：环形缓冲固定 300 点、落盘 ≤ 100 份、原子写入、IDisposable 正确释放。
4. 测试通过：单元 + 集成测试全通过，手动验收清单全勾选。
5. 依赖声明：`THIRD-PARTY-NOTICES.txt` 新增 LibreHardwareMonitor 声明（项目 memory 硬约束）。
6. 无回归：现有崩溃扫描、历史趋势、环境变更、报告导出不受影响。

## 7. 关键约束清单（对齐项目 memory）

- 开源依赖 LibreHardwareMonitor 必须在 THIRD-PARTY-NOTICES.txt 声明。
- 硬件快照落盘用原子 File.Move 覆盖，不用 Delete+Move。
- 环形缓冲与落盘淘汰用 lock 保护 Load-Modify-Save。
- IDisposable（Computer、NotifyIcon）必须 Dispose。
- 长操作（采集、落盘）跑后台线程，UI 通过 IProgress<T> 封送。
- AggregateException 用 GetBaseException().Message。
- 落盘上限 100 份、环形缓冲 300 点，超限淘汰最旧。
