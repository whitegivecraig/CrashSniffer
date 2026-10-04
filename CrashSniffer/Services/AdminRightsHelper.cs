using System;
using System.Security.Principal;
using System.Windows.Forms;

namespace CrashSniffer.Services;

/// <summary>
/// 管理员权限检测：LibreHardwareMonitor 读取硬件传感器需要管理员权限。
/// app.manifest 已配 requireAdministrator，但用户可能通过兼容模式/调试器绕过，
/// 运行时再次确认并提示。
/// </summary>
public static class AdminRightsHelper
{
    /// <summary>当前进程是否以管理员身份运行</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检测权限，非管理员时弹提示框。返回是否具备权限。
    /// 不强制退出，由调用方决定是否降级。
    /// </summary>
    public static bool CheckAndPrompt()
    {
        if (IsRunningAsAdmin()) return true;

        MessageBox.Show(
            "硬件健康监控需要管理员权限才能读取 CPU/GPU/主板传感器数据。\n\n" +
            "请以管理员身份重新运行 CrashSniffer，否则硬件看板将显示「未获取数据」。",
            "需要管理员权限",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }
}
