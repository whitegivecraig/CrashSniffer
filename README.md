# 🪦 CrashSniffer

**Windows 系统崩溃诊断与硬件黑匣子监控工具** —— 崩溃/蓝屏重启后一键抓取并深度解析 **Minidump (`PAGEDU64` 崩溃模块偏移)** / **BSOD** / **WHEA** / **GPU TDR** / **LiveKernel** / **系统事件日志**；内置 **免 `WinRing0` 驱动的硬件传感器直采引擎** 与 **崩溃前 10 分钟黑匣子回放**，支持 **系统转储配置体检与一键修复**、**论坛求诊帖一键生成**，并自动打包关联 `.dmp` 转储文件与现代交互式 `report.html` 排障报告。

> 适合 DIY 玩家、装机与超频排障场景：显卡 TDR 掉驱动、WHEA 硬件错误、内存 XMP/EXPO 不稳、CPU PBO/CO 负压过激、电源偏离……崩溃之后无需手动翻事件查看器、无需安装数十 GB 的 WinDbg，也不会因为传统 `WinRing0` 驱动被 Windows Defender 拦截报毒。

## ✨ 功能特性

### 🔍 深度采集与内核转储解析
- **PAGEDU64 `_TRIAGE_DUMP64` 深度解析**：无需 WinDbg，直接解析 Windows 64 位内核转储的 `_EXCEPTION_RECORD64`、`_CONTEXT` (`RIP`) 与已加载内核模块表，精准定位**崩溃指令地址**与**故障模块+偏移量**（如 `amdkmdag.sys+0x1D0FB0`）及精确时间戳
- **Minidump 已卸载驱动 (`UnloadedModuleListStream`) 深度追踪**：解析 Minidump 流类型 14，精准捕获崩溃发生前卸载的内核驱动，当崩溃发生在已卸载代码段时，自动标记 `driver.sys+0xOffset [已卸载]`，破解反作弊与外设驱动卸载后引发悬空指针崩溃的定位难题
- **全量转储关联保留**：同一崩溃聚合窗口内的多个 `.dmp` 文件（如 `Watchdog` 连续生成的多个 `LiveKernelReports` + `Minidump`）全部保留在 `AssociatedDumpPaths` 中，绝不覆盖丢失
- **WER 错误报告归档 (`ReportArchive`) 深度回溯**：全面扫描系统及用户级 WER 目录，在 `.dmp` 转储被系统临时清理时，自动解析 `Report.wer` 恢复蓝屏停止代码、四维参数、故障驱动名与崩溃时间戳
- **系统事件日志全覆盖**：`Kernel-Power 41`、`WER-SystemErrorReporting 1001`（自动解析 `param1`/`param2` 十六进制蓝屏代码与四维参数）、`EventLog 6008`、`WHEA-Logger`（含 17/18/19/46/47 全事件 ID 部位识别）、`Display TDR (4101~4105)`、`nvlddmkm` / `amdkmdag` 显卡驱动事件，以及 **`DriverFrameworks-UserMode (10110/10111)` 用户态驱动宿主崩溃**
- **环境快照与变更追踪**：自动采集 CPU / 主板 / BIOS / 显卡与驱动版本（规范化 `YYYY-MM-DD` 日期） / 内存频率（含 XMP/EXPO 状态推断） / 芯片组驱动 / Windows 内存诊断结果，每次扫描自动比对环境变更并关联「事发前变更」

### 🧠 智能分析与排障工具箱
- **⚡ 本地 WinDbg / `cdb.exe` 深度调试器集成**：自动探查本地安装的 Windows SDK 或微软商店 WinDbg Preview 调试引擎；支持对在盘 `.dmp` 执行自动化 `!analyze -v`，提取符号化内核调用栈与 `FAILURE_BUCKET_ID`，弹窗展示并支持一键复制完整输出
- **⚠️ 已知高危/冲突驱动特征知识库 (`ProblematicDriverKnowledge`)**：针对灯控外设（`AsIO`、`GLckIO`、`ene`、`GDRV`、`corsairvbus`、`lgdcore`、`rzdev`）、游戏反作弊（`vgk`、`EasyAntiCheat`、`bedrive`、`acebase`、`mhyprot`）、老版无线网卡（`netwtw`、`rtwlane`）等驱动特征，精准生成高危驱动警报并提供针对性解决方案
- **智能事件聚合与实时检索**：30 分钟窗口自动归并同源崩溃（严重度：`BSOD > WHEA > TDR > Event 41 > 6008`）；支持按事件类型快速过滤与关键词实时搜索（驱动名、错误码、模块偏移等）
- **停止代码与驱动知识库**：内置 30+ 常见蓝屏停止代码与 70+ 常见内核/外设/反作弊/RGB 驱动中文解读及针对性排障建议，自动统计「惯犯驱动排行」
- **🔧 系统转储配置体检与一键修复**：自动检测注册表 `CrashControl`（小内存转储是否开启、`AlwaysKeepMemoryDump` 防覆盖、系统日志写入、分页文件大小、`Minidump` 目录），发现配置缺失可一键修复，避免下次蓝屏不留 `.dmp`
- **📋 一键生成求诊帖**：支持将当前选中崩溃或全局汇总一键生成结构化 Markdown 排障帖（含环境配置、蓝屏代码、模块偏移、硬件异常摘要、驱动预警、已卸载驱动與已关联转储清单）并复制到剪贴板，可直接发往贴吧 / NGA / Chiphell / V2EX
- **📂 一键导出 / 定位 `.dmp`**：详情页与顶部工具栏支持一键在资源管理器中高亮定位原始 `.dmp` 文件，或将选中事件的全部关联 `.dmp` 单独导出到指定目录

### 🌡️ 硬件健康监控与崩溃黑匣子（免 WinRing0 拦截）
- **🛡️ 免 `WinRing0` 安全传感器桥接（`InpOutSensorBridge`）**：默认拦截 `WinRing0x64.sys` 释放，彻底杜绝 Windows Defender `VulnerableDriver:WinNT/Winring0` 报毒弹窗；通过安全桥接与 ACPI `MCFG` 物理内存映射直读 **AMD Ryzen SMU / PM Table** 与 **主板 SuperIO (`Nuvoton NCT679xD` / `ITE`)** 硬件寄存器
- **实时看板**：左侧分类导航（CPU / GPU / 内存 / 电压 / 磁盘 / 主板），2 秒刷新；内置 **「🖥️ 标准台式机 / 💻 游戏本耐温 / ❄️ 水冷严格」三档告警阈值预设**，一键切换避免游戏本满载误报
- **CPU 细项**：封装温度 (`Tctl/Tdie`)、总负载、最高核心负载、平均频率、封装功耗 (`PPT W`)，以及每个逻辑核心的实时负载与加速频率 (`MHz`)
- **GPU 细项**：核心 / 热点 / 显存温度、显存占用、GPU 与显存控制器负载、核心 / 显存频率、功耗、风扇转速与百分比、核心电压
- **主板与供电细项**：主板传感器温度 (`SYSTIN`)、全部 SuperIO 风扇转速与 PWM 占空比、+12V / +5V / +3.3V / +3V Standby / Vcore / CPU SoC 电压、CMOS 电池 (`CR2032`) 电压
- **逐盘监控**：自动识别全部物理磁盘（NVMe / SATA / USB），按盘符 C → D → E → F 排序，展示温度、剩余寿命、NVMe 备用块余量、空间占用、读写速率与累计写入量
- **崩溃黑匣子回放与异常诊断**：环形缓冲保留最近 10 分钟传感器读数（配合「开机后台采样」计划任务可跨蓝屏重启保留 48 小时），崩溃前异常分析自动识别过热、降频、掉压、风扇停转、备用块耗尽等隐患，并支持多指标叠加及导出 CSV / PNG

### 📦 现代交互式报告导出
一键导出自包含 `.zip` 排障报告包，**自动提取并打包所有关联 `.dmp` 转储文件**：
- `report.html` —— **现代化交互式排障报告**：
  - 顶部 KPI 概览仪表盘（总崩溃数、蓝屏数、WHEA 硬件错误、头号嫌疑驱动、已打包 `.dmp` 数量与体积）
  - 实时关键词搜索框 + 崩溃类型筛选胶囊 + **深色/浅色主题一键切换** + 打印/导出 PDF 样式优化
  - **一键复制文本诊断摘要**按钮
  - 每条崩溃卡片直接展示 **`dumps/xxx.dmp` 相对路径徽章与文件大小**，解压后可直接点击或用 WinDbg 分析
- `dumps/*.dmp` —— 自动收集并按大小优先策略打包全部关联崩溃转储原文件（单文件上限 500MB，总预算 2GB）
- `events.json` / `environment.json` / `environment_history.json` / `hardware_snapshots/*.json` —— 完整结构化诊断数据

## 🚀 快速开始

### 运行要求
- Windows 10 / 11（x64）
- **管理员权限**（读取系统事件日志、内核转储与底层硬件传感器必需，程序启动会自动请求 UAC 提权）

### 下载安装
从 [Releases](https://github.com/whitegivecraig/CrashSniffer/releases) 下载最新版 `CrashSniffer_v*_win-x64.zip`，解压后直接运行 `CrashSniffer.exe`。发布为自包含单文件，**无需安装 .NET 运行时**。

### 使用方法
1. 以管理员身份运行 `CrashSniffer.exe`
2. 选择时间范围（今天 / 近 7 天 / 近 30 天 / 本月 / 全部），可使用列表上方搜索框或类型筛选框快速定位
3. 在左侧列表选择崩溃事件，右侧查看停止代码、故障模块偏移、嫌疑驱动、事发前变更、排障建议与硬件回放曲线
4. 点击「🔧 转储体检」检查系统蓝屏转储设置，或点击「📋 生成求诊帖」复制论坛求助格式
5. 点击「📦 导出报告包」生成包含交互式 `report.html` 与 `dumps/*.dmp` 的完整压缩包

### 从源码构建

```bash
git clone https://github.com/whitegivecraig/CrashSniffer.git
cd CrashSniffer

# 运行全部单元测试（189 项）
dotnet test -c Release

# 发布单文件独立可执行文件
dotnet publish CrashSniffer/CrashSniffer.csproj -c Release -r win-x64 --self-contained
```

> 发布产物不入库，二进制统一通过 GitHub Releases 分发。

## 📊 崩溃事件类型

| 类型 | 数据来源 | 说明 |
|------|----------|------|
| 🟦 BSOD 蓝屏 | Minidump / Event 1001 | 停止代码 + 四维参数 + 崩溃模块偏移 + 嫌疑驱动 |
| 🔥 WHEA 硬件错误 | WHEA-Logger (17/18/19/46/47) | CPU / PCIe / 内存等硬件层错误精准部位识别 |
| 🎮 GPU TDR | Display 4101~4105 / UMDF 10110 | 显卡驱动超时重置与用户态驱动宿主崩溃 |
| ⚡ Kernel-Power 41 | 事件日志 | 异常重启（断电 / 蓝屏后自动重启） |
| 🔌 意外关闭 6008 | 事件日志 | 未走正常关机流程 |
| 🧩 LiveKernel | LiveKernelReports | 设备挂死但未蓝屏（如 0x141 / 0x117 显卡超时） |

## 📄 第三方组件

本项目基于 .NET 8 运行时，使用 System.Text.Json、System.Diagnostics.EventLog、System.Management（均为 MIT 许可）与 LibreHardwareMonitorLib（MPL-2.0），详见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

## 📝 更新日志

### v2.6.0
- **新增**：**Minidump 已卸载驱动 (`UnloadedModuleListStream`) 深度追踪** —— 解析流类型 14 (`0x0E`)，提取崩溃前已卸载驱动列表；当崩溃地址或 BugCheck 参数落在已卸载模块地址区间时，自动将偏移标注为 `driver.sys+0xOffset [已卸载]`，完美解决反作弊/外设驱动卸载后遗留回调引发 0x3B / 0x139 / 0xCE 蓝屏却无法在已加载模块表中寻址的定位盲区
- **新增**：**已知高危/冲突驱动特征知识库 (`ProblematicDriverKnowledge`)** —— 深度覆盖主板灯控（`AsIO`、`GLckIO`、`ene`、`GDRV`）、外设驱动（`corsairvbus`、`lgdcore`、`rzdev`）、游戏反作弊（`vgk`、`EasyAntiCheat`、`bedrive`、`acebase`、`mhyprot`）、老版无线网卡（`netwtw`、`rtwlane`）与虚拟机驱动（`vboxdrv`）；自动生成高危驱动警报，并在详情面板、HTML 导出报告与发帖摘要中高亮提示并注入专项排障建议
- **新增**：**Windows Error Reporting (WER) 归档目录全量扫描** —— 自动检索 `%ProgramData%\Microsoft\Windows\WER\ReportArchive`、`ReportQueue` 与 `%LocalAppData%\...`；在磁盘 `.dmp` 原文件被 Windows 维护任务清理时，自动解析 `Report.wer` 恢复蓝屏停止代码、四维参数、故障驱动名与精确时刻
- **新增**：**⚡ 本地 WinDbg / `cdb.exe` 深度调试引擎集成 (`WinDbgService`)** —— 自动检测本地 Windows SDK 与微软商店 WinDbg Preview 调试器；主界面一键对在盘 `.dmp` 执行全自动 `!analyze -v`，提取内核符号化调用栈 (`STACK_TEXT`) 与故障特征桶 (`FAILURE_BUCKET_ID`)，弹窗展示并支持一键复制完整输出
- **测试**：新增 4 项深度单元测试（高危驱动匹配、WER 归档解析、Unloaded 模块仿真、WinDbg 输出正则抽取），全套单元测试扩充至 **189 项** 100% 通过

### v2.5.0
- **新增**：**免 `WinRing0` 驱动的底层传感器直采桥接器 (`InpOutSensorBridge`)** —— 默认阻止释放 `WinRing0x64.sys`，彻底解决 Windows Defender 报毒拦截 `VulnerableDriver:WinNT/Winring0` 的问题；通过 ACPI `MCFG` 物理内存映射与 SuperIO 端口桥接直接采集 AMD Ryzen SMU / PM Table（CPU `Tctl/Tdie` 温度、`PPT` 功耗、16 线程实时加速频率、`Vcore`/`SoC` 电压）与主板 `NCT679xD`/`ITE` 传感器（主板温度、`+12V`/`+5V`/`+3.3V`/`3VSB`/`CMOS` 电压、风扇转速与占空比）
- **新增**：**`PAGEDU64` (`_TRIAGE_DUMP64`) 64 位内核转储深度解析** —— 自动提取已加载模块表、异常记录 (`_EXCEPTION_RECORD64`) 与上下文 (`_CONTEXT.Rip`)，精确定位故障模块偏移量（如 `amdkmdag.sys+0x1D0FB0`）与精确崩溃时间戳
- **新增**：**多转储文件全量保留与一键导出 `.dmp`** —— 聚合事件保留全部关联 `.dmp` 路径 (`AssociatedDumpPaths`)；导出 ZIP 报告包自动收集全部关联 `.dmp` 并按大小优先策略打包；新增「💾 导出 .dmp」与「📂 定位文件」快捷操作
- **新增**：**现代交互式 `report.html` 排障报告** —— 全新设计 KPI 统计卡片、关键词实时搜索、事件类型筛选胶囊、深色/浅色主题切换、一键复制诊断摘要，以及崩溃卡片内的 `dumps/*.dmp` 联动徽章
- **新增**：**🔧 系统转储配置体检与一键修复 (`CrashDumpConfigService`)** —— 自动检测注册表小内存转储、`AlwaysKeepMemoryDump`、分页文件与 `Minidump` 目录状态并支持一键修复
- **新增**：**📋 论坛求诊帖一键生成器 (`ForumPostBuilder`)** —— 一键生成包含环境配置、崩溃摘要、模块偏移与关联转储清单的 Markdown 求诊帖
- **新增**：**硬件告警阈值三档预设切换** —— 支持「标准台式机 / 游戏本耐温 / 水冷严格」三档温度阈值实时切换
- **新增**：主界面崩溃列表**关键词实时搜索框**与**事件类型筛选下拉框**；新增 `DriverFrameworks-UserMode (10110/10111)` 采集与 `Event 1001` 十六进制蓝屏参数解析
- **修复**：修复传感器调度中 `Ring0.IsOpen` 判空陷阱与无效零值未过滤导致的 `0.0 °C (正常)`、`0.0 W` 假读数问题，建立采集层、判定层、界面层三道异常值过滤防线
- **测试**：新增 75 项单元测试，全套测试扩充至 **185 项** 100% 通过

### v2.4.1
- **修复**：事件页左侧列表因分割条在窗口布局完成前设置而塌缩（仅约 128px），四个表头挤在一起
- **修复**：事件详情区始终空白 —— 首行随添加自动选中时 `Tag` 尚未赋值，详情被清空后不再刷新；重扫后也会保留原选中项
- **修复**：初始窗口尺寸可能超出屏幕工作区，导致左侧内容被裁掉
- **改进**：硬件回放页指标选择框独占一行、高度 190px，32 项指标一屏可见，导出按钮右对齐
- **改进**：磁盘列表九列按窗口宽度等比铺开，长型号名不再截断，右侧不再留白

### v2.4.0
- **新增**：逐盘监控全部物理磁盘（此前只读取第一块 SSD）——温度、剩余寿命、NVMe 备用块、空间占用、读写速率、累计写入量，并通过 WMI 关联显示盘符
- **新增**：CPU 每个逻辑核心的负载 / 频率、最高核心负载；GPU 显存控制器负载、风扇百分比、核心电压；物理 / 虚拟内存已用量（GB）
- **新增**：全部 SuperIO 风扇（转速 + 占空比）、+3V Standby、CPU SoC、CMOS 电池电压
- **新增**：CMOS 电池电压偏低、NVMe 备用块低于厂商阈值两类异常检测
- **新增**：内存 / 虚拟内存占用、GPU 显存温度与显存占用、主板传感器最高温度；健康看板改为左侧分类导航 + 右侧明细
- **修复**：主板 SuperIO 子传感器未遍历，导致 +3.3V / Vcore / CPU 风扇等电压与转速始终为空
- **修复**：AMD 平台 CPU 温度读不到（原逻辑只匹配 "Package"，现优先 Tctl/Tdie，并回退 CCD / 核心温度）
- **修复**：磁盘"寿命"误把 NVMe Available Spare 当作剩余寿命，现改为 100 − Percentage Used
- **改进**：异常分析支持逐盘过热 / 寿命不足、显存过热、内存占用过高、主板过热；回放页、CSV 与 HTML 报告同步新增指标
- **改进**：CSV 动态追加逐核心负载 / 频率与逐风扇转速 / 占空比列，便于离线分析
- **改进**：磁盘列表按盘符 C → D → E → F 排序，看板、回放与导出报告顺序一致

### v2.3.0
- **新增**：硬件回放页支持同单位多指标叠加，并支持导出 CSV 数据表与 PNG 图表
- **新增**：崩溃前硬件异常分析 —— 自动识别 CPU / GPU / SSD 过热、温度骤升、+12V / +5V / +3.3V 电压偏离、CPU 降频、风扇停转，回放页与 HTML 报告同步展示
- **新增**：开机后台采样 —— 通过计划任务在登录后后台记录传感器读数（按小时滚动，保留约 48 小时），蓝屏重启后仍可回放崩溃前窗口；硬件健康页可一键开关
- **改进**：没有落盘快照时，回放页与导出报告自动回退到后台连续采样数据

### v2.2.0
- **新增**：硬件健康看板 —— 实时监控 CPU / GPU / 主板电压 / SSD 温度与负载，2 秒刷新并按安全阈值配色
- **新增**：崩溃硬件回放 —— 环形缓冲保留最近 10 分钟读数，崩溃时保存前 5 分钟 + 后 1 分钟窗口，曲线标注崩溃时刻
- **新增**：报告包新增 `hardware_snapshots/` 目录，随报告一起导出崩溃时刻硬件读数
- **改进**：仅记录监控期间发生的崩溃，快照按 EventId 去重；落盘后回放页自动刷新；快照匹配改为读取文件名，避免 UI 线程全量反序列化
- **合规**：新增 LibreHardwareMonitorLib（MPL-2.0）第三方声明

### v2.1.1
- **修复**：wevtutil 挂起导致导出报告时 UI 永久假死（超时强杀 + 导出移至后台线程）
- **修复**：扫描期间切换日期范围可并发触发两次扫描、历史存档互相覆盖的问题（重入保护 + 存档加锁）
- **修复**：Event 41 缺失 PowerButtonTimestamp 字段时被误标"可能按下电源键"，误导断电排障方向
- **修复**：报告包内不同目录同名 dump 互相覆盖，现在自动重命名并注明来源
- **修复**：存档原子写入残留 Delete+Move 窗口，进程被杀可能丢文件；LiveKernelReports 单目录无权限导致整体采集失败；minidump 边界数据丢失整条 BugCheck 信息
- **改进**：6008 关机时间按多日期格式精确解析；导出 dumps 加 2GB 总量预算防撑爆系统盘；事件 XML 单次解析 + 关联事件查询共享会话，大批量扫描明显提速
- **发布**：二进制改为 GitHub Releases 分发，仓库仅保留源码

### v2.1.0
- **新增**：环境变更检测 —— 每次扫描持久化环境快照，自动检测两次扫描之间的 BIOS / Windows 更新 / 显卡驱动 / 内存频率（XMP）/ 芯片组驱动 / 硬件清单变更
- **新增**：崩溃详情「事发前变更」卡片（自动关联崩溃前变更，附相对时间）与独立「环境变更」时间线页签
- **新增**：报告包新增 environment_history.json，HTML 报告附事发前变更与完整变更时间线

### v2.0.1
- **修复**：点击「全部」时间范围时崩溃（`DateTimePicker` 越界异常），现在正确使用控件自身的日期边界
- **修复**：切换时间范围会触发两次重复扫描的问题
- **修复**：事件日志读取时的原生句柄泄漏（`EventRecord` 未释放）
- **修复**：详情面板反复点击导致的 GDI 字体句柄泄漏（改用字体缓存）
- **内存**：WHEA 事件采集过滤为 Warning 及以上，排除高频"已纠正"错误洪泛；单次扫描收录上限 5000 条
- **内存**：崩溃事件聚合窗口加 2 小时硬上限，合并消息加长度上限，防止高频事件导致内存与 CPU 失控
- **合规**：第三方声明文件补登 System.Management；升级 System.Text.Json 至 8.0.5 修复已知高危漏洞（GHSA-8g4q-xg66-9fp4 / GHSA-hh2w-p6rv-4g7w）

### v2.0.0
- 首个公开发布版本：BSOD / WHEA / TDR / LiveKernel / 事件日志采集、停止代码知识库、嫌疑驱动排行、历史趋势与 A/B 对比、zip 报告导出

## 📝 许可证

[MIT](LICENSE)
