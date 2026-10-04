using System;
using System.Collections.Generic;
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
            IsMemoryEnabled = false,
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
