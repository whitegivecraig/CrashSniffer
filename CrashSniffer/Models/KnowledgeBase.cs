using System.Collections.Frozen;
using CrashSniffer.Models;

namespace CrashSniffer.Models;

/// <summary>
/// BugCheck 停止代码知识库：给出英文符号名 + 中文解释 + 排障建议
/// </summary>
public static class BugCheckKnowledge
{
    public record StopCodeInfo(
        uint Code,
        string Name,
        string Chinese,
        string[] Suggestions);

    private static readonly StopCodeInfo[] _raw =
    [
        new(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL",
            "内核访问了无效内存地址且 IRQL 过高",
            ["通常由驱动程序 bug 或内存硬件故障引起", "先怀疑嫌疑驱动是否最新或是否有冲突 (如杀毒/虚拟光驱)", "跑一次 Windows 内存诊断或 MemTest86"]),
        new(0x0000001A, "MEMORY_MANAGEMENT",
            "内存管理器检测到严重不一致",
            ["驱动/程序触发内存破坏，常由超频/不稳压内存引起", "运行内存诊断；若开了 XMP/EXPO 尝试关闭或降低频率", "跑 sfc /scannow 与 Dism /Online /Cleanup-Image /RestoreHealth"]),
        new(0x0000001E, "KMODE_EXCEPTION_NOT_HANDLED",
            "内核模式程序产生异常但没有被处理",
            ["90% 是第三方驱动问题，按嫌疑驱动排查", "用 DDU 卸载显卡/芯片组驱动重装", "嫌疑驱动若为 ntoskrnl.exe，则需要靠 WinDbg 进一步分析"]),
        new(0x0000003B, "SYSTEM_SERVICE_EXCEPTION",
            "从用户态到内核态的系统调用中出现异常",
            ["嫌疑驱动排障+DDU显卡驱动重装", "关闭杀毒软件或安全软件自检一下", "sfc /scannow 修复系统文件"]),
        new(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA",
            "访问了一个不该分页的内存页，但该页不存在",
            ["常见于驱动错误或内存条不稳", "嫌疑驱动排查；若怀疑内存则降低频率/关闭XMP", "检查磁盘是否有坏块 (chkdsk /r /f)"]),
        new(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
            "系统线程抛出未处理的异常",
            ["多半是驱动或硬件兼容性问题", "按嫌疑驱动排查；ntoskrnl 时查内存条", "最近更新过的驱动回滚试试"]),
        new(0x0000007F, "UNEXPECTED_KERNEL_MODE_TRAP",
            "CPU 产生了未预料的内核陷阱 (双重故障等)",
            ["典型超频不稳表现：CPU/内存/PCIe 频率过高", "立刻关闭所有超频 / PBO / XMP 再观察", "若为 0x0D 子码 (GP Fault) 基本是 CPU/内存不稳"]),
        new(0x0000009F, "DRIVER_POWER_STATE_FAILURE",
            "驱动在电源状态转换时卡住",
            ["与休眠/睡眠/关机时的驱动有关", "检查显卡驱动、网卡、Wi-Fi、蓝牙驱动是否最新", "BIOS 中尝试关闭 PCIe ASPM 省电"]),
        new(0x000000D1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
            "驱动访问了无效内存且 IRQL 过高",
            ["几乎 100% 是驱动 bug", "直接针对嫌疑驱动重装/回滚", "常见凶手：网卡驱动、显卡驱动、杀毒软件、VPN"]),
        new(0x000000D8, "DRIVER_USED_EXTRA_FILE_SYSTEM",
            "驱动使用了错误的文件系统结构",
            ["磁盘文件系统或存储驱动问题", "chkdsk /r /f 检查系统盘", "检查 NVMe/SATA 驱动及 RAID 配置"]),
        new(0x000000EA, "THREAD_STUCK_IN_DEVICE_DRIVER",
            "设备驱动线程卡死 (旧显卡驱动 TDR 的 0xEA)",
            ["显卡驱动挂死，与 0x141 类似", "使用 DDU 彻底卸载显卡驱动重装", "检查显卡温度是否过高"]),
        new(0x00000101, "CLOCK_WATCHDOG_TIMEOUT",
            "CPU 二级缓存时钟监视狗超时：一个核未响应",
            ["典型 CPU/主板/供电问题：AMD PBO/超频不稳", "立刻关闭 PBO、降倍频、加 Vcore 一点点测试", "检查 VRM 温度、BIOS 更新、SOC 电压"]),
        new(0x00000116, "VIDEO_TDR_ERROR",
            "GPU TDR 超时未能成功恢复 (蓝屏版)",
            ["显卡驱动重置失败直接蓝屏", "DDU 重装驱动；供电不足 (显卡电源) 时也会触发", "GPU 跑 FurMark/3DMark 测稳定度；GPU 显存故障也常见"]),
        new(0x00000117, "VIDEO_TDR_TIMEOUT_DETECTED",
            "GPU 超时恢复成功但被记录为蓝屏 (Win10/11)",
            ["与 0x141 / 0x116 属同一类 GPU 驱动卡死", "先 DDU 装官方驱动；超频则降一点显存频率", "电源功率不够也会频繁触发"]),
        new(0x00000119, "VIDEO_SCHEDULER_INTERNAL_ERROR",
            "显卡调度器内部错误",
            ["GPU 核心/显存硬件问题或驱动 bug", "若新装驱动出现则回滚到旧稳定版", "GPU 跑 FurMark + VRAM 测试验证硬件"]),
        new(0x00000124, "WHEA_UNCORRECTABLE_ERROR",
            "Windows 硬件错误架构 (WHEA) 不可纠正错误",
            ["硬件级错误，几乎不可能是软件", "分类看 WHEA 记录：CPU Cache→CPU/电压；PCIe→显卡/NVMe/插槽；Memory→内存条", "立刻关闭所有超频：PBO/XMP/CPU OC，更新 BIOS"]),
        new(0x00000133, "DPC_WATCHDOG_VIOLATION",
            "延迟过程调用 DPC 监视狗超时",
            ["驱动 DPC 跑太久或 ISR 关中断太久", "嫌疑驱动排查，常见是 NVMe 驱动 / SATA AHCI / Wi-Fi", "BIOS 中关闭 C-states 或 PCIe ASPM 省电可做排除"]),
        new(0x00000139, "KERNEL_SECURITY_CHECK_FAILURE",
            "内核安全检查失败 (越界、栈溢出、结构体损坏)",
            ["驱动内存越界或内存硬件损坏", "嫌疑驱动排查；ntoskrnl 时查内存条", "关闭 XMP/EXPO 再观察"]),
        new(0x00000141, "VIDEO_TDR_FAILURE",
            "GPU 显示驱动超时未能恢复 (蓝屏版 TDR)",
            ["显卡驱动挂死：常见 amdkmdag.sys / nvlddmkm.sys", "用 DDU 卸载显卡驱动后装官方正式版", "GPU 超频→降频；检查供电；GPU 显存压力测试 (3DMark/OCCT)"]),
        new(0x00000144, "VIDEO_ENGINE_TIMEOUT_DETECTED",
            "GPU 视频编解码引擎超时",
            ["硬件视频解码引擎挂死：看片/剪片时出现", "重装显卡驱动并关闭浏览器/播放器的硬件加速", "GPU 显存或核心轻微不稳时触发，降频测试"]),
        new(0x00000154, "UNEXPECTED_STORE_EXCEPTION",
            "存储异常：文件系统 / NVMe / SSD 相关",
            ["NVMe/SSD 读写失败", "更新主板 NVMe 驱动与 BIOS，关闭 PCIe ASPM", "chkdsk /r /f；CrystalDiskInfo 看 SMART 健康状态"]),
        new(0x00000157, "KERNEL_LOCK_ENTRY_LEAKED_ON_THREAD_TERMINATION",
            "线程退出时有内核锁未释放",
            ["嫌疑驱动锁泄露，关注显示/音频/输入驱动", "最近装/更新过的驱动回滚"]),
        new(0x0000018A, "WHEA_INTERNAL_ERROR",
            "WHEA 内部错误 (AER 等 PCIe 高级错误报告)",
            ["PCIe 设备相关：显卡/NVMe/网卡 物理层/链路", "重插显卡、NVMe；清灰；检查 PCIe 插槽松动", "BIOS 中 PCIe 从 Gen4 降到 Gen3 测试是否稳定"]),
        new(0x000001C6, "DRIVER_VERIFIER_DETECTED_VIOLATION",
            "驱动验证器检测到违规",
            ["如果开启了 verifier.exe，可能是它揪出了坏驱动", "运行 verifier /reset 关闭驱动验证器", "嫌疑驱动对应的驱动就是罪魁祸首"]),
        new(0x000001C9, "DRIVER_VERIFIER_IOMANAGER_VIOLATION",
            "驱动验证器 I/O 管理器违规",
            ["verifier 找到 I/O 层面的驱动 bug", "verifier /reset 关闭，针对性重装嫌疑驱动"]),
        new(0x000001E1, "DRIVER_RETURNED_HOLDING_CANCEL_LOCK",
            "驱动退出时仍持有取消锁",
            ["驱动 bug：回滚或重装嫌疑驱动"]),
        new(0x000001EA, "XBOX_360_HUB_INVALID_REQUEST",
            "USB/Xbox 360 外设驱动请求非法 (常见手柄接收器)",
            ["拔掉 Xbox 手柄接收器 / 手柄 USB 线测试", "更新手柄驱动或换接收器固件"]),
        new(0x1000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M",
            "系统线程异常未处理 (32 位符号版)",
            ["同 0x7E，嫌疑驱动+内存排查"]),
        new(0x100000EA, "THREAD_STUCK_IN_DEVICE_DRIVER_M",
            "设备驱动线程死循环 (32 位符号版)",
            ["同 0xEA：显卡驱动卡死，DDU 重装显卡驱动"]),
    ];

    private static readonly FrozenDictionary<uint, StopCodeInfo> _byCode =
        _raw.ToFrozenDictionary(x => x.Code, x => x);

    /// <summary>
    /// 尝试根据 BugCheckCode 获取停止代码信息；找不到返回基于十六进制的默认描述
    /// </summary>
    public static StopCodeInfo Get(uint bugCheckCode)
    {
        if (_byCode.TryGetValue(bugCheckCode, out var info))
            return info;

        return new StopCodeInfo(
            bugCheckCode,
            "UNKNOWN",
            $"未知停止代码 (0x{bugCheckCode:X8})，建议使用 WinDbg 打开 dump 进一步分析",
            ["使用 WinDbg Preview (Microsoft Store 搜索) 打开 MEMORY.DMP 或对应 minidump",
                "在 WinDbg 中执行 !analyze -v 查看详细栈与驱动",
                "搜索该代码对应官方文档 / Microsoft Learn"]);
    }
}

/// <summary>
/// 常见驱动 → 硬件/组件映射，帮助用户看懂"嫌疑驱动是什么东西"
/// </summary>
public static class DriverKnowledge
{
    private static readonly (string prefix, string label)[] _map =
    [
        ("amdkmdag", "AMD 显卡内核模式驱动"),
        ("amdkmdap", "AMD 显卡用户模式驱动代理"),
        ("atikmdag", "AMD 老版本显卡驱动"),
        ("atidxx", "AMD 显卡 D3D 用户模式驱动"),
        ("nvlddmkm", "NVIDIA 显卡内核模式驱动"),
        ("nvvp", "NVIDIA 显卡用户模式驱动"),
        ("nvlddm", "NVIDIA 显卡驱动"),
        ("igdkmdn", "Intel 集显内核驱动"),
        ("igdkmd64", "Intel 集显内核驱动 (64位)"),
        ("igfx", "Intel 集显驱动"),
        ("ntoskrnl", "Windows 内核核心 (真实元凶需 WinDbg 深入分析)"),
        ("ntkrnlmp", "Windows 内核多处理器版本"),
        ("hal", "硬件抽象层 HAL"),
        ("win32kfull", "Win32k 图形子系统"),
        ("win32kbase", "Win32k 基础层"),
        ("tcpip", "TCP/IP 网络协议栈"),
        ("ndis", "网络驱动接口规范 (网卡驱动相关)"),
        ("netwtw", "Intel Wi-Fi 驱动"),
        ("rtwlane", "Realtek 无线网卡驱动"),
        ("rt640x", "Realtek 有线网卡驱动"),
        ("e1d", "Intel 有线网卡驱动"),
        ("stornvme", "Windows 自带 NVMe 存储驱动"),
        ("nvme", "NVMe 存储驱动"),
        ("storahci", "Windows AHCI SATA 驱动"),
        ("iaStorAC", "Intel RST 快速存储驱动"),
        ("amdi2c", "AMD I2C/SMBus 驱动"),
        ("amdgpio", "AMD GPIO 驱动"),
        ("amdpsp", "AMD PSP 平台安全处理器驱动"),
        ("AMDPPM", "AMD 处理器电源管理驱动"),
        ("usbhub3", "USB 3.x 集线器驱动"),
        ("ucx01000", "USB 控制器扩展驱动"),
        ("usbxhci", "USB xHCI 主机控制器驱动"),
        ("WdFilter", "Windows Defender 过滤驱动 (杀毒)"),
        ("WdNisDrv", "Windows Defender 网络实时扫描"),
        ("mssmbios", "SMBIOS 驱动 (硬件识别)"),
        ("acpi", "ACPI 高级配置电源接口 (BIOS相关)"),
        ("HDAudBus", "高保真音频总线驱动"),
        ("hdaudio", "HD Audio 音频驱动"),
        ("RTKVHD", "Realtek 瑞昱 HD Audio 驱动"),
    ];

    /// <summary>
    /// 给定驱动文件名 (不含后缀也可)，返回该驱动的中文描述
    /// </summary>
    public static string Describe(string driverFile)
    {
        if (string.IsNullOrWhiteSpace(driverFile))
            return string.Empty;

        var name = Path.GetFileNameWithoutExtension(driverFile).Trim().ToLowerInvariant();
        foreach (var (prefix, label) in _map)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        // 扩展名是 .sys 但表里没有：可能是第三方驱动，把原文件带上
        return $"第三方驱动 ({driverFile})";
    }

    /// <summary>
    /// 根据驱动名猜测 GPU 厂商
    /// </summary>
    public static string GuessGpuVendor(string driverFile)
    {
        if (string.IsNullOrWhiteSpace(driverFile))
            return "未知";
        var name = Path.GetFileNameWithoutExtension(driverFile).ToLowerInvariant();
        if (name.StartsWith("amdkmd") || name.StartsWith("atikmdag") || name.StartsWith("atidxx"))
            return "AMD";
        if (name.StartsWith("nvlddmkm") || name.StartsWith("nvvp") || name.StartsWith("nvlddm"))
            return "NVIDIA";
        if (name.StartsWith("igdkmd") || name.StartsWith("igfx"))
            return "Intel";
        return "未知";
    }
}
