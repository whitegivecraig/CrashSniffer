using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using CrashSniffer.Services;
using CrashSniffer.UI;

namespace CrashSniffer;

/// <summary>
/// 程序入口：配置高 DPI + 启动主窗体；--sampler 参数进入后台连续采样模式
/// </summary>
internal static class Program
{
    /// <summary>采样器批量落盘间隔（毫秒）</summary>
    private const int SamplerFlushMs = 10_000;

    /// <summary>采样器启动宽限期：超过仍无读数说明传感器不可用，直接退出（毫秒）</summary>
    private const int SamplerStartupGraceMs = 60_000;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--sampler", StringComparison.OrdinalIgnoreCase)))
        {
            RunSampler();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
    }

    /// <summary>
    /// 后台采样器：由计划任务在登录时以最高权限启动，每 10s 把新读数追加到连续采样存档。
    /// 与主界面使用同一套采集服务；互斥锁防止计划任务重复触发多个实例。
    /// </summary>
    private static void RunSampler()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: "CrashSnifferSampler", createdNew: out bool createdNew);
        if (!createdNew) return;

        using var monitor = new HardwareMonitorService();
        monitor.Start();

        DateTime lastTimestamp = DateTime.MinValue;
        DateTime startedAt = DateTime.Now;

        while (true)
        {
            var fresh = monitor.GetBuffer().Where(r => r.Timestamp > lastTimestamp).ToList();
            if (fresh.Count > 0)
            {
                lastTimestamp = fresh[^1].Timestamp;
                ContinuousSampleStore.Append(fresh);
            }
            else if ((DateTime.Now - startedAt).TotalMilliseconds >= SamplerStartupGraceMs)
            {
                // 传感器始终不可用（未提权/驱动异常等），空转没有意义，下次登录再重试
                return;
            }

            Thread.Sleep(SamplerFlushMs);
        }
    }
}
