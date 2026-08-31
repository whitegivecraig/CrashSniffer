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
    /// 对比新旧快照，返回变更列表（Time 由调用方填充检测时间）
    /// </summary>
    public static List<EnvironmentChange> Detect(EnvironmentSnapshot oldSnap, EnvironmentSnapshot newSnap)
    {
        // Task 4 实现
        return new List<EnvironmentChange>();
    }
}
