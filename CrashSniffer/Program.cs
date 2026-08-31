using System;
using System.Windows.Forms;
using CrashSniffer.UI;

namespace CrashSniffer;

/// <summary>
/// 程序入口：配置高 DPI + 启动主窗体
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
    }
}
