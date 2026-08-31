using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CrashSniffer.Models;

namespace CrashSniffer.Services;

/// <summary>
/// 环境快照内容指纹计算与差异检测：
/// 对比两次扫描的快照，生成「崩溃前什么变了」的变更记录。
/// 规则：某类别数据在任一侧为空（WMI 采集失败）时，该类别不参与指纹与对比，
/// 避免"采集失败"被误判为"环境变更"
/// </summary>
public static class EnvironmentChangeDetector
{
    /// <summary>
    /// 计算快照内容指纹（SHA-256 前 8 字节的十六进制，16 字符）。
    /// 参与指纹的字段：BIOS 版本/日期、OS Build/显示版本、CPU、主板、
    /// 各 GPU 名称+驱动版本+驱动日期、各内存槽位+实际频率+PartNumber、
    /// 各平台驱动文件名+版本、内存诊断结果
    /// </summary>
    public static string FingerprintOf(EnvironmentSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.Append("BIOS=").Append(snap.BiosVersion).Append('/').Append(snap.BiosDate).Append(';');
        sb.Append("OS=").Append(snap.OsBuild).Append('/').Append(snap.OsDisplayVersion).Append(';');
        sb.Append("CPU=").Append(snap.CpuName).Append(';');
        sb.Append("MB=").Append(snap.Motherboard).Append(';');
        foreach (var gpu in snap.Gpus)
            sb.Append("GPU=").Append(gpu.Name).Append('/').Append(gpu.DriverVersion).Append('/')
              .Append(gpu.DriverDate).Append(';');
        foreach (var m in snap.MemorySticks)
            sb.Append("MEM=").Append(m.Slot).Append('/').Append(m.ConfiguredSpeed).Append('/')
              .Append(m.PartNumber).Append(';');
        foreach (var d in snap.PlatformDrivers)
            sb.Append("PDRV=").Append(d.FileName).Append('/').Append(d.Version).Append(';');
        sb.Append("DIAG=").Append(snap.MemoryDiag.Found ? (snap.MemoryDiag.ErrorsDetected ? "ERR" : "OK") : "-").Append(';');

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// 对比新旧快照，返回变更列表（Time 由调用方填充检测时间）。
    /// 只有双方都具备该类别数据时才对比，单侧缺失（采集失败）不产生变更
    /// </summary>
    public static List<EnvironmentChange> Detect(EnvironmentSnapshot oldSnap, EnvironmentSnapshot newSnap)
    {
        var changes = new List<EnvironmentChange>();

        // BIOS
        if (!string.IsNullOrEmpty(oldSnap.BiosVersion) && !string.IsNullOrEmpty(newSnap.BiosVersion)
            && (oldSnap.BiosVersion != newSnap.BiosVersion || oldSnap.BiosDate != newSnap.BiosDate))
        {
            changes.Add(new EnvironmentChange
            {
                Category = ChangeCategory.Bios,
                Item = "BIOS",
                OldValue = $"{oldSnap.BiosVersion}（{oldSnap.BiosDate}）",
                NewValue = $"{newSnap.BiosVersion}（{newSnap.BiosDate}）",
            });
        }

        // Windows 更新（Build / 显示版本）
        if (!string.IsNullOrEmpty(oldSnap.OsBuild) && !string.IsNullOrEmpty(newSnap.OsBuild)
            && (oldSnap.OsBuild != newSnap.OsBuild || oldSnap.OsDisplayVersion != newSnap.OsDisplayVersion))
        {
            changes.Add(new EnvironmentChange
            {
                Category = ChangeCategory.WindowsUpdate,
                Item = "Windows 版本",
                OldValue = $"{oldSnap.OsDisplayVersion} Build {oldSnap.OsBuild}",
                NewValue = $"{newSnap.OsDisplayVersion} Build {newSnap.OsBuild}",
            });
        }

        // 显卡驱动（按显卡名称配对）
        foreach (var newGpu in newSnap.Gpus)
        {
            var oldGpu = oldSnap.Gpus.FirstOrDefault(g => g.Name == newGpu.Name);
            if (oldGpu == null) continue; // 旧侧无此显卡（新装或清单差异），由硬件清单对比处理
            if (oldGpu.DriverVersion != newGpu.DriverVersion || oldGpu.DriverDate != newGpu.DriverDate)
            {
                changes.Add(new EnvironmentChange
                {
                    Category = ChangeCategory.GpuDriver,
                    Item = $"显卡 {newGpu.Name}",
                    OldValue = $"驱动 {oldGpu.DriverVersion}（{oldGpu.DriverDate}）",
                    NewValue = $"驱动 {newGpu.DriverVersion}（{newGpu.DriverDate}）",
                });
            }
        }

        // 内存实际运行频率（XMP/EXPO 开关推断）
        foreach (var newMem in newSnap.MemorySticks)
        {
            var oldMem = oldSnap.MemorySticks.FirstOrDefault(m => m.Slot == newMem.Slot);
            if (oldMem == null) continue;
            if (oldMem.ConfiguredSpeed != newMem.ConfiguredSpeed)
            {
                string xmpHint = newMem.ConfiguredSpeed > oldMem.ConfiguredSpeed
                    ? "（疑似开启 XMP/EXPO）"
                    : "（疑似关闭 XMP/EXPO）";
                changes.Add(new EnvironmentChange
                {
                    Category = ChangeCategory.MemorySpeed,
                    Item = $"内存 {newMem.Slot}",
                    OldValue = $"{oldMem.ConfiguredSpeed} MT/s",
                    NewValue = $"{newMem.ConfiguredSpeed} MT/s {xmpHint}",
                });
            }
        }

        // 芯片组/平台驱动（按文件名配对）
        foreach (var newDrv in newSnap.PlatformDrivers)
        {
            var oldDrv = oldSnap.PlatformDrivers.FirstOrDefault(d => d.FileName == newDrv.FileName);
            if (oldDrv == null) continue;
            if (oldDrv.Version != newDrv.Version)
            {
                changes.Add(new EnvironmentChange
                {
                    Category = ChangeCategory.PlatformDriver,
                    Item = $"芯片组驱动 {newDrv.FileName}",
                    OldValue = oldDrv.Version,
                    NewValue = newDrv.Version,
                });
            }
        }

        // 硬件清单增删
        changes.AddRange(DetectHardwareChanges(oldSnap, newSnap));

        // 内存诊断结果（双方都有记录才对比）
        if (oldSnap.MemoryDiag.Found && newSnap.MemoryDiag.Found
            && oldSnap.MemoryDiag.ErrorsDetected != newSnap.MemoryDiag.ErrorsDetected)
        {
            changes.Add(new EnvironmentChange
            {
                Category = ChangeCategory.MemoryDiag,
                Item = "Windows 内存诊断",
                OldValue = oldSnap.MemoryDiag.ErrorsDetected ? "检测到硬件错误" : "未发现错误",
                NewValue = newSnap.MemoryDiag.ErrorsDetected ? "检测到硬件错误" : "未发现错误",
            });
        }

        return changes;
    }

    /// <summary>
    /// 硬件清单增删检测：CPU / 主板 / GPU / 内存条（PartNumber）。
    /// 双方该类别都有数据才对比，避免采集失败误判为"移除硬件"
    /// </summary>
    private static IEnumerable<EnvironmentChange> DetectHardwareChanges(EnvironmentSnapshot oldSnap, EnvironmentSnapshot newSnap)
    {
        if (!string.IsNullOrEmpty(oldSnap.CpuName) && !string.IsNullOrEmpty(newSnap.CpuName)
            && oldSnap.CpuName != newSnap.CpuName)
        {
            yield return new EnvironmentChange
            { Category = ChangeCategory.Hardware, Item = "CPU", OldValue = oldSnap.CpuName, NewValue = newSnap.CpuName };
        }

        if (!string.IsNullOrEmpty(oldSnap.Motherboard) && !string.IsNullOrEmpty(newSnap.Motherboard)
            && oldSnap.Motherboard != newSnap.Motherboard)
        {
            yield return new EnvironmentChange
            { Category = ChangeCategory.Hardware, Item = "主板", OldValue = oldSnap.Motherboard, NewValue = newSnap.Motherboard };
        }

        if (oldSnap.Gpus.Count > 0 && newSnap.Gpus.Count > 0)
        {
            var oldNames = oldSnap.Gpus.Select(g => g.Name).ToHashSet();
            var newNames = newSnap.Gpus.Select(g => g.Name).ToHashSet();
            foreach (var removed in oldNames.Where(n => !newNames.Contains(n)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "移除显卡", OldValue = removed, NewValue = "-" };
            foreach (var added in newNames.Where(n => !oldNames.Contains(n)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "新增显卡", OldValue = "-", NewValue = added };
        }

        if (oldSnap.MemorySticks.Count > 0 && newSnap.MemorySticks.Count > 0)
        {
            var oldParts = oldSnap.MemorySticks.Select(m => m.PartNumber).Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
            var newParts = newSnap.MemorySticks.Select(m => m.PartNumber).Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
            foreach (var removed in oldParts.Where(p => !newParts.Contains(p)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "移除内存条", OldValue = removed, NewValue = "-" };
            foreach (var added in newParts.Where(p => !oldParts.Contains(p)))
                yield return new EnvironmentChange
                { Category = ChangeCategory.Hardware, Item = "新增内存条", OldValue = "-", NewValue = added };
        }
    }
}
