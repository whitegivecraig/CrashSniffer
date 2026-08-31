# 🪦 CrashSniffer

**Windows 系统崩溃信息抓取工具** —— 崩溃/蓝屏重启后一键采集 BSOD / WHEA / TDR / LiveKernel / 事件日志，内置停止代码知识库与排障建议，生成可直接分享的排障报告。

> 适合 DIY 玩家、装机与超频排障场景：显卡 TDR、WHEA 硬件错误、内存 XMP/EXPO 不稳、驱动冲突……崩溃之后不用再手动翻事件查看器、找 WinDbg。

## ✨ 功能特性

### 一键采集
- **Minidump / MEMORY.DMP 解析**：内置解析器直接提取停止代码、BugCheck 参数与嫌疑驱动，不依赖 WinDbg
- **系统事件日志**：Kernel-Power 41、BugCheck 1001、EventLog 6008、WHEA-Logger、Display TDR（4101~4105）、nvlddmkm / amdkmdag 显卡驱动事件
- **LiveKernelReports**：抓取 GPU 等设备挂死但未蓝屏时留下的 dump
- **环境快照**：CPU / 主板 / BIOS / 显卡与驱动版本 / 内存频率（含 XMP/EXPO 状态推断）/ 芯片组驱动版本 / Windows 内存诊断结果

### 智能分析
- 30 分钟时间窗口内将同一崩溃的多条记录归并为单一事件（严重度：BSOD > WHEA > TDR > Event 41 > 6008）
- 30+ 常见停止代码的中文解释与针对性排障建议
- 嫌疑驱动排行（"惯犯名单"），并附系统内实际驱动版本
- WHEA 事件细化解析：直接给出出错部位结论（CPU / PCIe 设备 / 内存）
- 崩溃前后 ±5 分钟关联事件一览，方便还原现场

### 历史趋势
- 最近 30 天每日崩溃次数柱状图
- 停止代码 / 嫌疑驱动分布统计
- A/B 时段对比：换驱动、改 BIOS、关 XMP 前后效果一目了然

### 报告导出
导出 zip 报告包，方便发论坛求助或存档：
- `report.html` —— 可视化排障报告（含环境快照、嫌疑驱动排行、逐条详情）
- `events.json` —— 结构化事件数据 + 原始事件 XML
- `environment.json` —— 硬件环境快照
- `dumps/*.dmp` —— 崩溃 dump 原文件（超过 500MB 自动跳过）

## 🚀 快速开始

### 运行要求
- Windows 10 / 11（x64）
- **管理员权限**（读取 MEMORY.DMP 与系统事件日志必需，程序会自动请求 UAC 提权）
- Release 附带的 zip 为框架依赖式发布，需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)；从源码自行构建需要 .NET 8 SDK

### 使用方法
1. 以管理员身份运行 `CrashSniffer.exe`
2. 选择时间范围（今天 / 近 7 天 / 近 30 天 / 本月 / 全部）
3. 点击「🔄 刷新扫描」
4. 在左侧列表选择崩溃事件，右侧查看停止代码、嫌疑驱动、排障建议与关联事件
5. 点击「📦 导出报告包」生成 zip 报告

### 从源码构建

```bash
git clone https://github.com/whitegivecraig/CrashSniffer.git
cd CrashSniffer
dotnet publish CrashSniffer/CrashSniffer.csproj -c Release -r win-x64 --self-contained
```

## 📊 崩溃事件类型

| 类型 | 数据来源 | 说明 |
|------|----------|------|
| 🟦 BSOD 蓝屏 | Minidump / Event 1001 | 停止代码 + 参数 + 嫌疑驱动 |
| 🔥 WHEA 硬件错误 | WHEA-Logger | CPU / PCIe / 内存等硬件层错误 |
| 🎮 GPU TDR | Display 4101~4105 | 显卡驱动超时重置 |
| ⚡ Kernel-Power 41 | 事件日志 | 异常重启（断电 / 蓝屏后自动重启） |
| 🔌 意外关闭 6008 | 事件日志 | 未走正常关机流程 |
| 🧩 LiveKernel | LiveKernelReports | 设备挂死但未蓝屏（如 0x141） |

## 📄 第三方组件

本项目基于 .NET 8 运行时，使用 System.Text.Json、System.Diagnostics.EventLog、System.Management（均为 MIT 许可），详见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

## 📝 许可证

[MIT](LICENSE)
