using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly HashSet<string> _recordedCrashes = new();
    private readonly object _recordLock = new();

    private Thread? _pollThread;
    private volatile bool _running;
    private bool _disposed;

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
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsMemoryEnabled = true,
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
                ExtractCpuTemperature(s, v, temp);
                break;
            case SensorType.Power when s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase):
                r.CpuPower = v;
                break;
            case SensorType.Clock when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                clockSum += v;
                clockCount++;
                if (s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
                    GetOrAddCore(r, ParseTrailingIndex(s.Name)).Clock = v;
                break;
            case SensorType.Load:
                if (s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                    r.CpuMaxCoreLoad = v;
                else if (s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))
                    GetOrAddCore(r, ParseTrailingIndex(s.Name)).Load = v;
                else if (s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                    r.CpuLoad = v;
                break;
            case SensorType.Voltage:
            {
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
                r.GpuPower = v;
                break;
            case SensorType.Clock:
            {
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
                r.GpuFanRpm = v;
                break;
            case SensorType.Control when r.GpuFanPercent == null:
                r.GpuFanPercent = v;
                break;
            case SensorType.Voltage when r.GpuVoltage == null:
                r.GpuVoltage = v;
                break;
        }
    }

    private static void ExtractMotherboard(ISensor s, double v, HardwareReading r)
    {
        switch (s.SensorType)
        {
            case SensorType.Temperature:
                r.MoboMaxTemperature = Max(r.MoboMaxTemperature, v);
                break;
            case SensorType.Voltage:
            {
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
                r.CpuFanRpm ??= v;
                GetOrAddFan(r, s.Name).Rpm = v;
                break;
            }
            case SensorType.Control when s.Name.StartsWith("Fan", StringComparison.OrdinalIgnoreCase):
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
        try { _computer.Close(); } catch { }
    }
}
