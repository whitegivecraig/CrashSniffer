using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrashSniffer.Models;
using LibreHardwareMonitor.Hardware;

namespace CrashSniffer.Services;

/// <summary>
/// 硬件监控核心服务：后台线程以 2s 间隔轮询传感器，
/// 维护 300 点环形缓冲（~10 分钟），崩溃时输出前后窗口快照。
/// 默认启用「安全无驱动模式」（拦截 LibreHardwareMonitor 释放 WinRing0x64.sys 内核驱动），
/// 彻底避免触发 Windows Defender 的 VulnerableDriver:WinNT/Winring0 严重告警。
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

    /// <summary>
    /// 是否允许释放并加载 WinRing0 内核驱动（默认 false）。
    /// Windows Defender 会将 LibreHardwareMonitor 内置的 WinRing0x64.sys 识别为
    /// VulnerableDriver:WinNT/Winring0 (CVE-2020-14979) 并弹窗拦截。
    /// 默认关闭时：通过反射占位阻止 WinRing0.sys 释放到磁盘，显卡(NVAPI/ADL)、磁盘(SMART/NVMe)、
    /// 内存(Win32 API)、CPU 负载与基础频率/温度(WMI/性能计数器)仍全部正常采集。
    /// </summary>
    public static bool AllowWinRing0Driver { get; set; } = false;

    private Computer _computer;
    private readonly InpOutSensorBridge _inpOutBridge = new();
    private readonly RingBuffer<HardwareReading> _buffer = new(BufferCapacity);
    private readonly object _sensorLock = new();
    private readonly HashSet<string> _recordedCrashes = new();
    private readonly object _recordLock = new();

    private Thread? _pollThread;
    private volatile bool _running;
    private bool _disposed;
    private int _wmiFallbackTick;
    private double? _cachedWmiCpuClock;
    private double? _cachedWmiCpuTemp;

    /// <summary>快照落盘完成后触发（参数为 EventId，可能在后台线程触发）</summary>
    public event Action<string>? SnapshotSaved;

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
        _computer = CreateComputer();
    }

    private static Computer CreateComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        // 未启用 WinRing0 时关闭 SuperIO 主板端口扫描，避免无效的 LPC/IO 端口探测
        IsMotherboardEnabled = AllowWinRing0Driver,
        IsStorageEnabled = true,
        IsMemoryEnabled = true,
        IsNetworkEnabled = false,
        IsControllerEnabled = false,
    };

    /// <summary>
    /// 通过反射在 LibreHardwareMonitor.Hardware.Ring0.Open() 执行前注入未打开的占位实例，
    /// 阻止其向程序目录释放 CrashSniffer.sys (WinRing0x64.sys)，从而彻底避免 Windows Defender 报毒拦截。
    /// </summary>
    internal static void ConfigureRing0Guard(bool allowDriver)
    {
        try
        {
            CleanupResidualSysFile();

            var asm = typeof(Computer).Assembly;
            var ring0Type = asm.GetType("LibreHardwareMonitor.Hardware.Ring0");
            var kdType = asm.GetType("LibreHardwareMonitor.Hardware.KernelDriver");
            if (ring0Type == null || kdType == null) return;

            var driverField = ring0Type.GetField("_driver", BindingFlags.NonPublic | BindingFlags.Static);
            if (driverField == null) return;

            if (!allowDriver)
            {
                // 构造一个未调用 Install/Open 的空壳 KernelDriver：
                // Ring0.Open() 检测到 _driver != null 会直接 return，不会调用 Extract() 释放 .sys 文件，
                // 同时 _driver.IsOpen 为 false，所有依赖 Ring0 的代码安全走降级分支。
                var ctor = kdType.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(string), typeof(string) },
                    modifiers: null);
                if (ctor != null)
                {
                    object dummyDriver = ctor.Invoke(new object[] { "CrashSniffer_NoRing0", "WinRing0_1_2_0" });
                    driverField.SetValue(null, dummyDriver);
                }
            }
            else
            {
                // 用户显式开启 Ring0 时，若此前是空壳占位（IsOpen == false），清空以允许正常加载
                object? current = driverField.GetValue(null);
                if (current != null)
                {
                    var isOpenProp = kdType.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    bool isOpen = isOpenProp?.GetValue(current) is true;
                    if (!isOpen)
                        driverField.SetValue(null, null);
                }
            }
        }
        catch
        {
            // 反射守卫异常不阻断主流程
        }
    }

    /// <summary>清理之前版本可能遗留在程序目录下的 .sys 驱动文件</summary>
    private static void CleanupResidualSysFile()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string sysPath = Path.ChangeExtension(processPath, ".sys");
                if (File.Exists(sysPath))
                    File.Delete(sysPath);
            }
            string baseSys = Path.Combine(AppContext.BaseDirectory, "CrashSniffer.sys");
            if (File.Exists(baseSys))
                File.Delete(baseSys);
        }
        catch { }
    }

    /// <summary>启动监控（打开传感器 + 启动轮询线程）</summary>
    public void Start()
    {
        if (_running || _disposed) return;
        try
        {
            ConfigureRing0Guard(AllowWinRing0Driver);
            _computer.Open();
            if (!AllowWinRing0Driver)
            {
                // 关键修复：LibreHardwareMonitor 的 Ring0.IsOpen 仅判断 _driver != null（不检查 _driver.IsOpen）。
                // 在 _computer.Open() 内部完成 Ring0.Open() 短路（成功阻止 CrashSniffer.sys 释放）后，
                // 必须立即将 _driver 置回 null，使 Ring0.IsOpen 返回 false，防止 Amd17Cpu 等模块在 Update() 时
                // 误调未打开的驱动句柄并把 0.0°C / 0.0W 写入传感器。
                ClearDummyRing0BeforeClose();
            }
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

    /// <summary>动态切换是否启用底层 Ring0 驱动并重启传感器</summary>
    public void RestartWithRing0Setting(bool allowRing0)
    {
        if (_disposed) return;
        AllowWinRing0Driver = allowRing0;
        Stop();
        lock (_sensorLock)
        {
            try
            {
                if (!allowRing0)
                    ClearDummyRing0BeforeClose();
                _computer.Close();
            }
            catch { }

            _computer = CreateComputer();
        }
        Start();
    }

    private static void ClearDummyRing0BeforeClose()
    {
        try
        {
            var asm = typeof(Computer).Assembly;
            var ring0Type = asm.GetType("LibreHardwareMonitor.Hardware.Ring0");
            var kdType = asm.GetType("LibreHardwareMonitor.Hardware.KernelDriver");
            var driverField = ring0Type?.GetField("_driver", BindingFlags.NonPublic | BindingFlags.Static);
            if (driverField == null || kdType == null) return;

            object? current = driverField.GetValue(null);
            if (current != null)
            {
                var isOpenProp = kdType.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                bool isOpen = isOpenProp?.GetValue(current) is true;
                if (!isOpen)
                    driverField.SetValue(null, null);
            }
        }
        catch { }
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

    /// <summary>供单元测试或单次即时探测调用</summary>
    internal HardwareReading? CollectSingleReading() => CollectReading();

    /// <summary>采集一次硬件读数</summary>
    private HardwareReading? CollectReading()
    {
        var reading = new HardwareReading { Timestamp = DateTime.Now };
        var cpuTemp = new CpuTempAccumulator();
        double cpuClockSum = 0;
        int cpuClockCount = 0;

        lock (_sensorLock)
        {
            try
            {
                foreach (var hw in _computer.Hardware)
                    CollectHardware(hw, reading, cpuTemp, ref cpuClockSum, ref cpuClockCount, depth: 0);
            }
            catch
            {
                return null;
            }
        }

        reading.CpuTemperature = cpuTemp.Primary ?? cpuTemp.Fallback;
        if (cpuClockCount > 0)
            reading.CpuClock = cpuClockSum / cpuClockCount;

        // 通过安全 InpOut 桥接器直接采集 AMD SMU（温度/功耗/核心频率/SVI2电压）与主板 SuperIO（主板温度/电压/风扇转速）
        if (!AllowWinRing0Driver || reading.CpuTemperature == null || reading.MoboMaxTemperature == null)
        {
            _inpOutBridge.SupplementReading(reading);
        }

        // 若仍缺少 CPU 频率或温度，使用 Windows 原生 WMI 兜底补充
        if (reading.CpuClock == null || reading.CpuTemperature == null)
        {
            SupplementCpuViaWmiFallback(reading);
        }

        reading.CpuCores.Sort((a, b) => a.Index.CompareTo(b.Index));
        if (reading.CpuMaxCoreLoad == null && reading.CpuCores.Count > 0)
        {
            var loads = reading.CpuCores.Where(c => c.Load.HasValue).Select(c => c.Load!.Value).ToList();
            if (loads.Count > 0) reading.CpuMaxCoreLoad = loads.Max();
        }
        FinalizeFans(reading);
        FinalizeStorage(reading);
        return reading;
    }

    /// <summary>
    /// 当未加载 WinRing0 驱动时，通过系统自带 WMI（无需任何第三方内核驱动）补充 CPU 实时频率与 ACPI 热区温度。
    /// 每 3 次轮询（约 6s）刷新一次缓存以降低 WMI 开销。
    /// </summary>
    private void SupplementCpuViaWmiFallback(HardwareReading reading)
    {
        int tick = Interlocked.Increment(ref _wmiFallbackTick);
        if (tick == 1 || tick % 3 == 0)
        {
            if (reading.CpuClock == null)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo["CurrentClockSpeed"] != null &&
                            double.TryParse(mo["CurrentClockSpeed"].ToString(), out double mhz) && mhz > 0)
                        {
                            _cachedWmiCpuClock = mhz;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (reading.CpuTemperature == null)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    double maxC = double.MinValue;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo["CurrentTemperature"] != null &&
                            double.TryParse(mo["CurrentTemperature"].ToString(), out double tenthsKelvin) && tenthsKelvin > 2732)
                        {
                            double c = (tenthsKelvin / 10.0) - 273.15;
                            if (c is > 0 and < 125 && c > maxC)
                                maxC = c;
                        }
                    }
                    if (maxC > double.MinValue)
                        _cachedWmiCpuTemp = maxC;
                }
                catch { }
            }
        }

        reading.CpuClock ??= _cachedWmiCpuClock;
        reading.CpuTemperature ??= _cachedWmiCpuTemp;
    }

    /// <summary>过滤未接测速的风扇插针，仅保留 CPU 风扇与转速大于 0 的风扇</summary>
    private static void FinalizeFans(HardwareReading reading)
    {
        reading.Fans.RemoveAll(f =>
            !(f.Rpm is > 0) && !string.Equals(f.Name, "Fan #1", StringComparison.OrdinalIgnoreCase));
        reading.Fans.Sort((a, b) =>
        {
            int ai = ParseTrailingIndex(a.Name);
            int bi = ParseTrailingIndex(b.Name);
            if (ai != bi) return ai.CompareTo(bi);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>采集单个硬件（递归 SubHardware，SuperIO 挂载在主板下）</summary>
    private static void CollectHardware(IHardware hw, HardwareReading reading, CpuTempAccumulator cpuTemp,
        ref double clockSum, ref int clockCount, int depth)
    {
        try { hw.Update(); } catch { return; }

        if (hw.HardwareType == HardwareType.Storage)
        {
            reading.Storages.Add(BuildStorage(hw));
            return;
        }

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value == null) continue;
            double v = sensor.Value.Value;
            // 驱动未就绪/传感器异常时会返回 NaN/Infinity，写入 JSON 会抛异常
            if (!double.IsFinite(v)) continue;

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    ExtractCpu(sensor, v, reading, cpuTemp, ref clockSum, ref clockCount);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    ExtractGpu(sensor, v, reading);
                    break;
                case HardwareType.Motherboard:
                case HardwareType.SuperIO:
                    ExtractMotherboard(sensor, v, reading);
                    break;
                case HardwareType.Memory:
                    ExtractMemory(sensor, v, reading);
                    break;
            }
        }

        if (depth >= 2) return;
        foreach (var sub in hw.SubHardware)
            CollectHardware(sub, reading, cpuTemp, ref clockSum, ref clockCount, depth + 1);
    }

    private sealed class CpuTempAccumulator
    {
        /// <summary>Tctl/Tdie 或 Package（优先）</summary>
        public double? Primary;

        /// <summary>CCD/核心温度最大值（兜底）</summary>
        public double? Fallback;
    }

    private static void ExtractCpu(ISensor s, double v, HardwareReading r, CpuTempAccumulator temp,
        ref double clockSum, ref int clockCount)
    {
        switch (s.SensorType)
        {
            case SensorType.Temperature:
                if (v is > 0.5 and < 150.0)
                    ExtractCpuTemperature(s, v, temp);
                break;
            case SensorType.Power when s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase):
                if (v is > 0.1 and < 2000.0)
                    r.CpuPower = v;
                break;
            case SensorType.Clock when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                if (v is > 10.0 and < 20000.0)
                {
                    clockSum += v;
                    clockCount++;
                    if (s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
                        GetOrAddCore(r, ParseTrailingIndex(s.Name)).Clock = v;
                }
                break;
            case SensorType.Load:
                if (v is >= 0.0 and <= 100.0)
                {
                    if (s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                        r.CpuMaxCoreLoad = v;
                    else if (s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))
                        GetOrAddCore(r, ParseTrailingIndex(s.Name)).Load = v;
                    else if (s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                        r.CpuLoad = v;
                }
                break;
            case SensorType.Voltage:
            {
                // 排除 Amd17Cpu 在 Ring0 未就绪时因 eax=0 计算出的 1.55V (1.55 - 0*0.00625) 假读数
                if (v is <= 0.1 or >= 1.549) break;
                string n = s.Name.ToLowerInvariant();
                if (n.Contains("soc") && n.Contains("svi2"))
                    r.CpuSocVoltage = v;
                else if (n.Contains("core") && n.Contains("svi2") && r.CpuVoltage == null)
                    r.CpuVoltage = v;
                break;
            }
        }
    }

    private static CpuCoreReading GetOrAddCore(HardwareReading reading, int index)
    {
        var core = reading.CpuCores.FirstOrDefault(c => c.Index == index);
        if (core != null) return core;
        core = new CpuCoreReading { Index = index };
        reading.CpuCores.Add(core);
        return core;
    }

    private static void ExtractCpuTemperature(ISensor s, double v, CpuTempAccumulator temp)
    {
        if (v is <= 0.5 or >= 150.0) return;
        string n = s.Name.ToLowerInvariant();
        if (n.Contains("ccd"))
            temp.Fallback = Max(temp.Fallback, v);
        else if (n.Contains("tctl") || n.Contains("tdie") || n.Contains("package"))
            temp.Primary = Max(temp.Primary, v);
        else if (n.Contains("core") || n.Contains("cpu"))
            temp.Fallback = Max(temp.Fallback, v);
    }

    private static void ExtractGpu(ISensor s, double v, HardwareReading r)
    {
        switch (s.SensorType)
        {
            case SensorType.Temperature:
            {
                if (v is <= 0.5 or >= 150.0) break;
                string n = s.Name.ToLowerInvariant();
                if (n.Contains("hot") && (n.Contains("spot") || n.Contains("point")))
                    r.GpuHotspot = Max(r.GpuHotspot, v);
                else if (n.Contains("memory") || n.Contains("vram") || n.Contains("junction"))
                    r.GpuMemoryTemperature = Max(r.GpuMemoryTemperature, v);
                else if (n.Contains("core") || n.Contains("edge") || n.Contains("gpu"))
                    r.GpuTemperature = Max(r.GpuTemperature, v);
                else if (r.GpuTemperature == null)
                    r.GpuTemperature = v;
                break;
            }
            case SensorType.Power when r.GpuPower == null:
                if (v is > 0.1 and < 2000.0)
                    r.GpuPower = v;
                break;
            case SensorType.Clock:
            {
                if (v is <= 0.0 or >= 20000.0) break;
                string n = s.Name.ToLowerInvariant();
                if (n.Contains("memory") && r.GpuMemoryClock == null)
                    r.GpuMemoryClock = v;
                else if (!n.Contains("memory") && r.GpuClock == null)
                    r.GpuClock = v;
                break;
            }
            case SensorType.SmallData:
            {
                string n = s.Name.ToLowerInvariant();
                if (n.Contains("d3d") || n.Contains("shared")) break;
                if (n.Contains("memory used"))
                    r.GpuMemoryUsedMb = v;
                else if (n.Contains("memory total"))
                    r.GpuMemoryTotalMb = v;
                break;
            }
            case SensorType.Load:
            {
                string n = s.Name.ToLowerInvariant();
                if (n.Contains("memory"))
                {
                    r.GpuMemoryLoad ??= v;
                }
                else if (r.GpuLoad == null && n.Contains("core"))
                {
                    r.GpuLoad = v;
                }
                break;
            }
            case SensorType.Fan when r.GpuFanRpm == null:
                if (v is >= 0.0 and < 15000.0)
                    r.GpuFanRpm = v;
                break;
            case SensorType.Control when r.GpuFanPercent == null:
                if (v is >= 0.0 and <= 100.0)
                    r.GpuFanPercent = v;
                break;
            case SensorType.Voltage when r.GpuVoltage == null:
                if (v is > 0.1 and < 5.0)
                    r.GpuVoltage = v;
                break;
        }
    }

    private static void ExtractMotherboard(ISensor s, double v, HardwareReading r)
    {
        switch (s.SensorType)
        {
            case SensorType.Temperature:
                if (v is > 0.5 and < 130.0)
                    r.MoboMaxTemperature = Max(r.MoboMaxTemperature, v);
                break;
            case SensorType.Voltage:
            {
                if (v is <= 0.1 or >= 25.0) break;
                string n = s.Name.ToLowerInvariant();
                if (n.Contains("vcore") || n.Contains("cpu core"))
                    r.CpuVoltage = v;
                else if (n.Contains("+12") || n.Contains("12v"))
                    r.Voltage12V = v;
                else if (n.Contains("+5") || n.Contains("5v"))
                    r.Voltage5V = v;
                else if (n.Contains("standby"))
                    r.Voltage3VStandby = v;
                else if (n.Contains("cmos"))
                    r.CmosBatteryVoltage = v;
                else if (n.Contains("3.3") || n.Contains("3v3"))
                    r.Voltage3V = v;
                break;
            }
            case SensorType.Fan:
            {
                if (v is <= 0.0 or >= 15000.0) break;
                r.CpuFanRpm ??= v;
                GetOrAddFan(r, s.Name).Rpm = v;
                break;
            }
            case SensorType.Control when s.Name.StartsWith("Fan", StringComparison.OrdinalIgnoreCase):
                if (v is >= 0.0 and <= 100.0)
                    GetOrAddFan(r, s.Name).Percent = v;
                break;
        }
    }

    private static FanReading GetOrAddFan(HardwareReading reading, string name)
    {
        var fan = reading.Fans.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (fan != null) return fan;
        fan = new FanReading { Name = name.Trim() };
        reading.Fans.Add(fan);
        return fan;
    }

    private static void ExtractMemory(ISensor s, double v, HardwareReading r)
    {
        string n = s.Name.ToLowerInvariant();
        switch (s.SensorType)
        {
            case SensorType.Load:
                if (n.Contains("virtual"))
                    r.VirtualMemoryLoad = v;
                else if (n == "memory" || n.Contains("physical"))
                    r.MemoryLoad = v;
                break;
            case SensorType.Data:
                if (n.Contains("virtual"))
                {
                    if (n.Contains("used")) r.VirtualMemoryUsedGb = v;
                    else if (n.Contains("available"))
                        r.VirtualMemoryTotalGb = (r.VirtualMemoryUsedGb ?? 0) + v;
                }
                else
                {
                    if (n.Contains("used")) r.MemoryUsedGb = v;
                    else if (n.Contains("available"))
                        r.MemoryTotalGb = (r.MemoryUsedGb ?? 0) + v;
                }
                break;
        }
    }

    private static StorageReading BuildStorage(IHardware hw)
    {
        string id = hw.Identifier?.ToString() ?? string.Empty;
        var storage = new StorageReading
        {
            Id = id,
            Index = ParseStorageIndex(id),
            Name = (hw.Name ?? string.Empty).Trim(),
        };

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value == null) continue;
            double v = sensor.Value.Value;
            if (!double.IsFinite(v)) continue;
            string n = sensor.Name.ToLowerInvariant();

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (v is > 0.5 and < 130.0)
                        storage.Temperature = Max(storage.Temperature, v);
                    break;
                case SensorType.Level:
                    if (n.Contains("percentage used"))
                        storage.LifePercent = Math.Clamp(100.0 - v, 0.0, 100.0);
                    else if (n.Contains("available spare") && n.Contains("threshold"))
                        storage.SpareThresholdPercent = v;
                    else if (n.Contains("available spare"))
                        storage.AvailableSparePercent = v;
                    else if (n.Contains("remaining life") || n.Contains("life left") || n == "life")
                        storage.LifePercent = Math.Clamp(v, 0.0, 100.0);
                    break;
                case SensorType.Load:
                    if (n.Contains("used space"))
                        storage.UsedSpacePercent = v;
                    break;
                case SensorType.Throughput:
                    if (n.Contains("read"))
                        storage.ReadRateMBps = v / 1048576.0;
                    else if (n.Contains("write"))
                        storage.WriteRateMBps = v / 1048576.0;
                    break;
                case SensorType.Data:
                    if (n.Contains("read"))
                        storage.TotalReadGb = v;
                    else if (n.Contains("written") || n.Contains("write"))
                        storage.TotalWrittenGb = v;
                    break;
            }
        }

        return storage;
    }

    /// <summary>从 LHM 标识（/nvme/1、/hdd/0）解析物理磁盘序号</summary>
    internal static int ParseStorageIndex(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return -1;
        int slash = identifier.LastIndexOf('/');
        if (slash < 0 || slash >= identifier.Length - 1) return -1;
        return int.TryParse(identifier.AsSpan(slash + 1), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int index) ? index : -1;
    }

    /// <summary>从传感器名称末尾的 #n 解析编号（CPU Core #1、Fan #2）</summary>
    internal static int ParseTrailingIndex(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        int hash = name.LastIndexOf('#');
        if (hash < 0 || hash >= name.Length - 1) return -1;
        int start = hash + 1;
        while (start < name.Length && name[start] == ' ') start++;
        if (start >= name.Length) return -1;
        return int.TryParse(name.AsSpan(start), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int index) ? index : -1;
    }

    /// <summary>补盘符/型号、排序磁盘、回填兼容字段</summary>
    private static void FinalizeStorage(HardwareReading reading)
    {
        if (reading.Storages.Count == 0) return;

        if (reading.Storages.Any(s => s.Index >= 0))
        {
            try
            {
                var disks = StorageVolumeMapper.GetDisks();
                foreach (var storage in reading.Storages)
                {
                    if (storage.Index < 0) continue;
                    if (!disks.TryGetValue(storage.Index, out var info)) continue;
                    if (info.Volumes.Count > 0)
                        storage.Volumes = new List<string>(info.Volumes);
                    if (string.IsNullOrWhiteSpace(storage.Name))
                        storage.Name = info.Model;
                }
            }
            catch { /* 盘符映射失败不影响读数 */ }
        }

        var ordered = StorageLabel.OrderForDisplay(reading.Storages).ToList();
        reading.Storages.Clear();
        reading.Storages.AddRange(ordered);

        var temps = reading.Storages.Where(s => s.Temperature.HasValue)
            .Select(s => s.Temperature!.Value).ToList();
        if (temps.Count > 0)
            reading.SsdTemperature = temps.Max();

        var lives = reading.Storages.Where(s => s.LifePercent.HasValue)
            .Select(s => s.LifePercent!.Value).ToList();
        if (lives.Count > 0)
            reading.SsdLife = lives.Min();
    }

    private static double Max(double? current, double value)
        => current.HasValue && current.Value > value ? current.Value : value;

    /// <summary>
    /// 崩溃事件发生时：从环形缓冲提取崩溃前 5min 的读数，
    /// 并在崩溃后继续采集 1min，然后整体落盘。
    /// 仅当崩溃前已有监控数据（崩溃发生在本次监控期间）时记录；
    /// 同一崩溃按 EventId 去重，重复扫描不会重复采集。
    /// </summary>
    /// <returns>是否新建了采集任务</returns>
    public bool TryRecordCrashWindow(DateTime crashTime, string crashType)
    {
        // 未来时间（时钟异常）或崩溃前无读数（崩溃发生在本进程启动之前）时跳过
        if (crashTime > DateTime.Now.AddMinutes(1)) return false;

        var before = _buffer.GetRange(
            crashTime.AddMinutes(-BeforeCrashMinutes),
            crashTime,
            r => r.Timestamp);
        if (before.Length == 0) return false;

        string eventId = HardwareSnapshotStore.BuildEventId(crashTime);
        lock (_recordLock)
        {
            if (!_recordedCrashes.Add(eventId)) return false;
        }

        var snapshot = new HardwareSnapshot
        {
            EventId = eventId,
            CrashTime = crashTime,
            CrashType = crashType,
            Readings = before.ToList(),
        };

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
                SnapshotSaved?.Invoke(eventId);
            }
            catch { /* 落盘失败不阻断 */ }
        });

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            _inpOutBridge.Dispose();
            ClearDummyRing0BeforeClose();
            _computer.Close();
        }
        catch { }
    }
}
