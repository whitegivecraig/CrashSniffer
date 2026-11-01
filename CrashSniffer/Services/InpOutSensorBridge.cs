using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using CrashSniffer.Models;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace CrashSniffer.Services;

/// <summary>
/// 基于 InpOutx64 的安全传感器桥接器：
/// 在不释放、不加载 WinRing0x64.sys（避免触发 Windows Defender VulnerableDriver:WinNT/Winring0 拦截）的前提下，
/// 通过系统已信任的 \\.\inpoutx64 与 ACPI MCFG 物理内存映射直接采集：
/// 1. AMD Ryzen SMU 实时传感器（Tctl/Tdie 温度、CCD 温度、PPT 封装功耗、各核心实时加速频率、Core/SoC SVI2 电压）
/// 2. 主板 SuperIO（Nuvoton NCT677x/NCT679xD、ITE IT86xx/IT87xx）实时传感器（主板温度、+12V/+5V/+3.3V/3VSB/CMOS电池电压、风扇转速与占空比）
/// </summary>
internal sealed class InpOutSensorBridge : IDisposable
{
    private const uint IOCTL_READ_PORT_UCHAR = 0x9C402004u;
    private const uint IOCTL_WRITE_PORT_UCHAR = 0x9C402008u;

    private const uint ACPI_PROVIDER = 0x41435049u; // 'ACPI'
    private const uint MCFG_SIGNATURE = 0x4746434Du; // 'MCFG'

    // AMD Family 17h/19h SMU 寄存器地址
    private const uint F17H_M01H_THM_TCON_CUR_TMP = 0x00059800u;
    private const uint F17H_M70H_CCD1_TEMP = 0x00059954u;
    private const uint F17H_M01H_SVI_SOC = 0x0005A00Cu;
    private const uint F17H_M01H_SVI_CORE = 0x0005A010u;
    private const uint F17H_TEMP_OFFSET_FLAG = 0x00080000u;

    private readonly object _ioLock = new();
    private bool _initialized;
    private bool _disposed;

    // InpOut 反射调用缓存
    private MethodInfo? _inpOutOpenMethod;
    private MethodInfo? _mapMemoryMethod;
    private MethodInfo? _unmapMemoryMethod;
    private bool _inpOutOpened;

    // PCIe ECAM (Bus 0, Dev 0, Func 0) 映射缓存
    private ulong _ecamBase;
    private bool _isAmdCpu;
    private uint _smuCmdAddr;
    private uint _smuRspAddr;
    private uint _smuArgsAddr;
    private uint _pmTableVersion;
    private ulong _pmDramBase;
    private bool _pmTableProbed;

    // SuperIO 硬件监控端口缓存
    private SafeFileHandle? _portDevice;
    private ushort _sioChipId;
    private ushort _hwmAddrPort;
    private ushort _hwmDataPort;
    private bool _isNuvoton;
    private bool _isIte;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature,
        uint firmwareTableId,
        IntPtr pFirmwareTableBuffer,
        uint bufferSize);

    public void SupplementReading(HardwareReading reading)
    {
        if (_disposed) return;

        lock (_ioLock)
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }

            try
            {
                if (_isAmdCpu && _ecamBase != 0)
                    ReadAmdSmuSensors(reading);
            }
            catch
            {
                // SMU 读取异常不影响 SuperIO 与其他传感器
            }

            try
            {
                if (_portDevice is { IsInvalid: false, IsClosed: false } && _hwmAddrPort != 0)
                {
                    if (_isNuvoton)
                        ReadNuvotonSuperIo(reading);
                    else if (_isIte)
                        ReadIteSuperIo(reading);
                }
            }
            catch
            {
                // SuperIO 读取异常不阻断
            }
        }
    }

    private void Initialize()
    {
        try
        {
            var asm = typeof(Computer).Assembly;
            var inpOutType = asm.GetType("LibreHardwareMonitor.Hardware.InpOut");
            if (inpOutType != null)
            {
                _inpOutOpenMethod = inpOutType.GetMethod("Open", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _mapMemoryMethod = inpOutType.GetMethod("MapMemory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _unmapMemoryMethod = inpOutType.GetMethod("UnmapMemory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (_inpOutOpenMethod != null)
                    _inpOutOpened = _inpOutOpenMethod.Invoke(null, null) is true;
            }
        }
        catch { }

        // 打开 \\.\inpoutx64 设备句柄用于 SuperIO 端口读写
        try
        {
            if (File.Exists(@"\\.\inpoutx64") || _inpOutOpened)
            {
                _portDevice = File.OpenHandle(
                    @"\\.\inpoutx64",
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);
            }
        }
        catch
        {
            _portDevice = null;
        }

        if (_portDevice is { IsInvalid: false, IsClosed: false })
        {
            ProbeSuperIo();
        }

        if (_inpOutOpened && _mapMemoryMethod != null && _unmapMemoryMethod != null)
        {
            ProbeMcfgAndAmdSmu();
        }
    }

    private void ProbeMcfgAndAmdSmu()
    {
        try
        {
            uint size = GetSystemFirmwareTable(ACPI_PROVIDER, MCFG_SIGNATURE, IntPtr.Zero, 0);
            if (size < 60) return;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetSystemFirmwareTable(ACPI_PROVIDER, MCFG_SIGNATURE, buf, size) < 60)
                    return;
                byte[] mcfg = new byte[size];
                Marshal.Copy(buf, mcfg, 0, (int)size);
                _ecamBase = BitConverter.ToUInt64(mcfg, 44);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            if (_ecamBase == 0) return;

            IntPtr mapped = MapPhysicalMemory(_ecamBase, 4096, out IntPtr sectionHandle);
            if (mapped == IntPtr.Zero) return;
            try
            {
                uint vendorDev = unchecked((uint)Marshal.ReadInt32(mapped, 0));
                ushort vendor = (ushort)(vendorDev & 0xFFFF);
                if (vendor == 0x1022) // AMD Root Complex
                {
                    _isAmdCpu = true;
                    // 默认 Matisse / Vermeer / Cezanne / Raphael SMU 邮箱地址
                    _smuCmdAddr = 0x03B10524u;
                    _smuRspAddr = 0x03B10570u;
                    _smuArgsAddr = 0x03B10A40u;
                }
            }
            finally
            {
                UnmapPhysicalMemory(sectionHandle, mapped);
            }
        }
        catch { }
    }

    private void ReadAmdSmuSensors(HardwareReading reading)
    {
        IntPtr mapped = MapPhysicalMemory(_ecamBase, 4096, out IntPtr sectionHandle);
        if (mapped == IntPtr.Zero) return;

        try
        {
            uint SmuRead(int indexOffset, uint addr)
            {
                Marshal.WriteInt32(mapped, indexOffset, unchecked((int)addr));
                return unchecked((uint)Marshal.ReadInt32(mapped, indexOffset + 4));
            }

            void SmuWrite(int indexOffset, uint addr, uint val)
            {
                Marshal.WriteInt32(mapped, indexOffset, unchecked((int)addr));
                Marshal.WriteInt32(mapped, indexOffset + 4, unchecked((int)val));
            }

            // 1. 读取 THM_TCON_CUR_TMP (0x00059800) 获取 CPU Tctl/Tdie 实时温度
            uint rawTmp = SmuRead(0x60, F17H_M01H_THM_TCON_CUR_TMP);
            if (rawTmp != 0 && rawTmp != 0xFFFFFFFFu)
            {
                double tctl = ((rawTmp >> 21) & 0x7FF) * 0.125;
                if ((rawTmp & F17H_TEMP_OFFSET_FLAG) != 0)
                    tctl -= 49.0;

                if (tctl is > 5.0 and < 130.0)
                    reading.CpuTemperature = tctl;
            }

            // 2. 若 Tctl 未读到，尝试读取 CCD1 温度 (0x00059954)
            if (reading.CpuTemperature == null)
            {
                uint rawCcd = SmuRead(0x60, F17H_M70H_CCD1_TEMP);
                if (rawCcd != 0 && rawCcd != 0xFFFFFFFFu)
                {
                    double ccdTemp = ((rawCcd & 0xFFF) * 0.125) - 305.0;
                    if (ccdTemp is > 5.0 and < 130.0)
                        reading.CpuTemperature = ccdTemp;
                }
            }

            // 3. 读取 SVI2 遥测寄存器作为 Core / SoC 电压直接来源
            uint rawCoreSvi = SmuRead(0x60, F17H_M01H_SVI_CORE);
            if (rawCoreSvi != 0 && rawCoreSvi != 0xFFFFFFFFu)
            {
                byte vid = (byte)((rawCoreSvi >> 16) & 0xFF);
                double vcore = 1.550 - (vid * 0.00625);
                if (vid > 0 && vcore is > 0.3 and < 1.549)
                    reading.CpuVoltage = vcore;
            }

            uint rawSocSvi = SmuRead(0x60, F17H_M01H_SVI_SOC);
            if (rawSocSvi != 0 && rawSocSvi != 0xFFFFFFFFu)
            {
                byte vid = (byte)((rawSocSvi >> 16) & 0xFF);
                double vsoc = 1.550 - (vid * 0.00625);
                if (vid > 0 && vsoc is > 0.5 and < 1.5)
                    reading.CpuSocVoltage = vsoc;
            }

            // 4. 通过 SMU PM Table 获取封装功耗 (PPT W)、各核心实时频率与精确 SVI2 电压
            ReadAmdPmTable(SmuRead, SmuWrite, reading);
        }
        finally
        {
            UnmapPhysicalMemory(sectionHandle, mapped);
        }
    }

    private void ReadAmdPmTable(
        Func<int, uint, uint> smuRead,
        Action<int, uint, uint> smuWrite,
        HardwareReading reading)
    {
        bool SmuSend(uint msg, uint[] args)
        {
            for (int i = 0; i < 200 && smuRead(0xB8, _smuRspAddr) == 0; i++)
                Thread.Sleep(1);

            smuWrite(0xB8, _smuRspAddr, 0u);
            for (int i = 0; i < 6; i++)
                smuWrite(0xB8, _smuArgsAddr + (uint)(i * 4), i < args.Length ? args[i] : 0u);

            smuWrite(0xB8, _smuCmdAddr, msg);

            uint rsp = 0;
            for (int i = 0; i < 200; i++)
            {
                rsp = smuRead(0xB8, _smuRspAddr);
                if (rsp != 0) break;
                Thread.Sleep(1);
            }

            if (rsp == 1)
            {
                for (int i = 0; i < args.Length; i++)
                    args[i] = smuRead(0xB8, _smuArgsAddr + (uint)(i * 4));
                return true;
            }
            return false;
        }

        if (!_pmTableProbed)
        {
            _pmTableProbed = true;
            uint[] args = new uint[6];
            if (SmuSend(0x08u, args))
                _pmTableVersion = args[0];

            Array.Clear(args);
            if (SmuSend(0x06u, args))
                _pmDramBase = args[0] | ((ulong)args[1] << 32);
        }

        if (_pmDramBase == 0 || _pmDramBase == 0xFFFFFFFFFFFFFFFFul)
            return;

        uint[] transferArgs = new uint[6];
        if (!SmuSend(0x05u, transferArgs))
            return;

        IntPtr pmPtr = MapPhysicalMemory(_pmDramBase, 1024, out IntPtr pmHandle);
        if (pmPtr == IntPtr.Zero) return;

        try
        {
            byte[] pm = new byte[1024];
            Marshal.Copy(pmPtr, pm, 0, pm.Length);

            float ReadFloat(int index) => BitConverter.ToSingle(pm, index * 4);

            // PM[1] 为当前 PPT 封装功耗（W）
            float pptWatts = ReadFloat(1);
            if (float.IsFinite(pptWatts) && pptWatts is > 1.0f and < 500.0f)
                reading.CpuPower = pptWatts;

            // PM[5] 为 THM 当前核心温度（若 Tctl 未读到则用其兜底）
            float thmTemp = ReadFloat(5);
            if (reading.CpuTemperature == null && float.IsFinite(thmTemp) && thmTemp is > 5.0f and < 130.0f)
                reading.CpuTemperature = thmTemp;

            // 根据 PM Table 版本提取电压与各核心实时频率
            uint majorVer = _pmTableVersion >> 16;
            if (majorVer is 0x38 or 0x24) // Zen 3 (Vermeer 0x0038xxxx) / Zen 2 (Matisse 0x0024xxxx)
            {
                float vcore = ReadFloat(40);
                if (!float.IsFinite(vcore) || vcore is < 0.3f or > 1.75f)
                    vcore = ReadFloat(11);
                if (float.IsFinite(vcore) && vcore is > 0.3f and < 1.75f)
                    reading.CpuVoltage = vcore;

                float vsoc = majorVer == 0x38 ? ReadFloat(45) : ReadFloat(43);
                if (!float.IsFinite(vsoc) || vsoc is < 0.5f or > 1.5f)
                    vsoc = ReadFloat(28);
                if (float.IsFinite(vsoc) && vsoc is > 0.5f and < 1.5f)
                    reading.CpuSocVoltage = vsoc;

                // 0x003809xx / 0x002409xx 为 1-CCD（物理核心频率从 PM[30] 开始，单位 GHz）
                // 0x003808xx / 0x002408xx 为 2-CCD（物理核心频率从 PM[34] 开始，单位 GHz）
                bool is2Ccd = ((_pmTableVersion >> 8) & 0xFF) == 0x08;
                int coreStartIdx = is2Ccd ? 34 : 30;
                int maxPhysicalCores = is2Ccd ? 16 : 8;

                var physicalClocks = new System.Collections.Generic.List<double>(maxPhysicalCores);
                for (int i = 0; i < maxPhysicalCores; i++)
                {
                    float ghz = ReadFloat(coreStartIdx + i);
                    if (float.IsFinite(ghz) && ghz is > 0.2f and < 8.0f)
                        physicalClocks.Add(ghz * 1000.0);
                }

                if (physicalClocks.Count > 0)
                {
                    reading.CpuClock = physicalClocks.Average();

                    // 若开启了 SMT 超线程（逻辑核心数为物理核心数的 2 倍），将每个物理核心的频率同步赋给其对应的两个逻辑线程
                    bool isSmtDoubled = reading.CpuCores.Count == physicalClocks.Count * 2;
                    for (int i = 0; i < physicalClocks.Count; i++)
                    {
                        double mhz = physicalClocks[i];
                        if (isSmtDoubled)
                        {
                            SetCoreClock(reading, (i * 2) + 1, mhz);
                            SetCoreClock(reading, (i * 2) + 2, mhz);
                        }
                        else
                        {
                            SetCoreClock(reading, i + 1, mhz);
                        }
                    }
                }
            }
        }
        finally
        {
            UnmapPhysicalMemory(pmHandle, pmPtr);
        }
    }

    private static void SetCoreClock(HardwareReading reading, int coreNumber, double mhz)
    {
        var core = reading.CpuCores.FirstOrDefault(c => c.Index == coreNumber);
        if (core == null)
        {
            core = new CpuCoreReading { Index = coreNumber };
            reading.CpuCores.Add(core);
        }
        core.Clock ??= mhz;
    }

    private void ProbeSuperIo()
    {
        foreach (ushort port in new ushort[] { 0x2E, 0x4E })
        {
            // 进入 Nuvoton / Winbond / Fintek 扩展配置模式
            WriteIoByte(port, 0x87);
            WriteIoByte(port, 0x87);

            ushort chipId = (ushort)((ReadSioReg(port, 0x20) << 8) | ReadSioReg(port, 0x21));
            if (chipId != 0 && chipId != 0xFFFF)
            {
                // 选择逻辑设备 0x0B (Hardware Monitor)
                WriteSioReg(port, 0x07, 0x0B);
                ushort hwmBase = (ushort)((ReadSioReg(port, 0x60) << 8) | ReadSioReg(port, 0x61));
                WriteIoByte(port, 0xAA); // 退出扩展模式

                if (hwmBase is >= 0x100 and < 0xF000)
                {
                    _sioChipId = chipId;
                    _hwmAddrPort = (ushort)(hwmBase + 5);
                    _hwmDataPort = (ushort)(hwmBase + 6);
                    _isNuvoton = (chipId & 0xF000) is 0xB000 or 0xC000 or 0xD000;
                    if (_isNuvoton)
                    {
                        // 开启 VBAT 实时监测 (Bank 0, Reg 0x5D Bit 0)
                        byte vbatCtl = ReadNuvotonEc(0x005D);
                        WriteNuvotonEc(0x005D, (byte)(vbatCtl | 0x01));
                        return;
                    }
                }
            }
            else
            {
                WriteIoByte(port, 0xAA);
            }

            // 探测 ITE IT86xx / IT87xx
            WriteIoByte(port, 0x87);
            WriteIoByte(port, 0x01);
            WriteIoByte(port, 0x55);
            WriteIoByte(port, port == 0x2E ? (byte)0x55 : (byte)0xAA);

            ushort iteId = (ushort)((ReadSioReg(port, 0x20) << 8) | ReadSioReg(port, 0x21));
            if ((iteId >> 8) is 0x86 or 0x87)
            {
                WriteSioReg(port, 0x07, 0x04); // ITE Environment Controller LDN = 0x04
                ushort hwmBase = (ushort)((ReadSioReg(port, 0x60) << 8) | ReadSioReg(port, 0x61));
                WriteSioReg(port, 0x02, 0x02); // 退出 ITE 配置模式
                if (hwmBase is >= 0x100 and < 0xF000)
                {
                    _sioChipId = iteId;
                    _hwmAddrPort = (ushort)(hwmBase + 5);
                    _hwmDataPort = (ushort)(hwmBase + 6);
                    _isIte = true;
                    return;
                }
            }
        }
    }

    private void ReadNuvotonSuperIo(HardwareReading reading)
    {
        // 1. 主板温度：Reg 0x027 为 SYSTIN（主板环温主传感器）
        sbyte systin = unchecked((sbyte)ReadNuvotonEc(0x027));
        if (systin is > 5 and < 105)
        {
            reading.MoboMaxTemperature = reading.MoboMaxTemperature.HasValue
                ? Math.Max(reading.MoboMaxTemperature.Value, systin)
                : systin;
        }

        // 若 CPU 温度仍为空，读取 SuperIO 映射的 CPU 温度 (0x079 PECI / 0x073 CPUTIN)
        if (reading.CpuTemperature == null)
        {
            sbyte peciTemp = unchecked((sbyte)ReadNuvotonEc(0x079));
            sbyte cputin = unchecked((sbyte)ReadNuvotonEc(0x073));
            if (peciTemp is > 10 and < 115)
                reading.CpuTemperature = peciTemp;
            else if (cputin is > 10 and < 115)
                reading.CpuTemperature = cputin;
        }

        // 2. 电压：NCT679xD 电压寄存器位于 Bank 4 (0x480..0x48F)，步进 8mV (0.008V)
        // +12V: VIN2 (0x484) 采用 12x 分压 (110k / 10k)
        double vin12 = ReadNuvotonEc(0x484) * 0.008 * 12.0;
        if (vin12 is > 10.2 and < 13.8)
            reading.Voltage12V ??= vin12;

        // +5V: 主板通常接在 VIN1 (0x481) 或 VIN7 (0x48C)，采用 5x 分压 (40k / 10k)
        double vin5A = ReadNuvotonEc(0x481) * 0.008 * 5.0;
        double vin5B = ReadNuvotonEc(0x48C) * 0.008 * 5.0;
        if (reading.Voltage5V == null)
        {
            bool validA = vin5A is > 4.3 and < 5.6;
            bool validB = vin5B is > 4.3 and < 5.6;
            if (validA && validB)
                reading.Voltage5V = Math.Abs(vin5A - 5.0) <= Math.Abs(vin5B - 5.0) ? vin5A : vin5B;
            else if (validA)
                reading.Voltage5V = vin5A;
            else if (validB)
                reading.Voltage5V = vin5B;
        }

        // +3.3V: AVCC (0x482) 与 3VCC (0x483) 采用 2x 内部分压
        double avcc = ReadNuvotonEc(0x482) * 0.008 * 2.0;
        double vcc3 = ReadNuvotonEc(0x483) * 0.008 * 2.0;
        if (reading.Voltage3V == null)
        {
            bool validAvcc = avcc is > 2.85 and < 3.7;
            bool validVcc3 = vcc3 is > 2.85 and < 3.7;
            if (validAvcc && validVcc3)
                reading.Voltage3V = Math.Abs(avcc - 3.3) <= Math.Abs(vcc3 - 3.3) ? avcc : vcc3;
            else if (validAvcc)
                reading.Voltage3V = avcc;
            else if (validVcc3)
                reading.Voltage3V = vcc3;
        }

        // +3V Standby: 3VSB (0x487)，2x 内部分压
        double vsb3 = ReadNuvotonEc(0x487) * 0.008 * 2.0;
        if (vsb3 is > 2.85 and < 3.7)
            reading.Voltage3VStandby ??= vsb3;

        // CMOS 电池 (VBAT): 0x488，2x 内部分压
        double vbat = ReadNuvotonEc(0x488) * 0.008 * 2.0;
        if (vbat is > 2.0 and < 3.7)
            reading.CmosBatteryVoltage ??= vbat;

        // Vcore: VIN0 (0x480)，若 SMU 未提供则兜底
        if (reading.CpuVoltage == null)
        {
            double vin0 = ReadNuvotonEc(0x480) * 0.008;
            // ASUS B550/X570 等主板在 VIN0 上采用 2x 分压
            if (vin0 is > 0.45 and < 0.85)
                vin0 *= 2.0;
            if (vin0 is > 0.5 and < 1.65)
                reading.CpuVoltage = vin0;
        }

        // 3. 风扇转速 (0x4C0..0x4CC) 与 PWM 占空比
        ReadOnlySpan<ushort> fanRpmRegs = [0x4C0, 0x4C2, 0x4C4, 0x4C6, 0x4C8, 0x4CA, 0x4CC];
        ReadOnlySpan<ushort> fanPwmRegs = [0x001, 0x003, 0x011, 0x013, 0x015, 0x017, 0x019];

        for (int i = 0; i < fanRpmRegs.Length; i++)
        {
            ushort rpmReg = fanRpmRegs[i];
            int rpm = (ReadNuvotonEc(rpmReg) << 8) | ReadNuvotonEc((ushort)(rpmReg + 1));
            // 排除 0 与悬空引脚产生的 0xFFxx (65311 等异常值)
            if (rpm is <= 0 or >= 10000)
                continue;

            string fanName = $"Fan #{i + 1}";
            reading.CpuFanRpm ??= rpm;

            var fan = reading.Fans.FirstOrDefault(f => string.Equals(f.Name, fanName, StringComparison.OrdinalIgnoreCase));
            if (fan == null)
            {
                fan = new FanReading { Name = fanName };
                reading.Fans.Add(fan);
            }
            fan.Rpm = rpm;

            byte pwmRaw = ReadNuvotonEc(fanPwmRegs[i]);
            if (pwmRaw > 0)
                fan.Percent = Math.Round(pwmRaw / 255.0 * 100.0, 0);
        }
    }

    private void ReadIteSuperIo(HardwareReading reading)
    {
        // ITE IT86xx/IT87xx 温度寄存器 0x29..0x2B
        for (byte reg = 0x29; reg <= 0x2B; reg++)
        {
            sbyte temp = unchecked((sbyte)ReadIteReg(reg));
            if (temp is > 5 and < 105)
            {
                if (reg == 0x29 && reading.CpuTemperature == null)
                    reading.CpuTemperature = temp;
                else
                    reading.MoboMaxTemperature = reading.MoboMaxTemperature.HasValue
                        ? Math.Max(reading.MoboMaxTemperature.Value, temp)
                        : temp;
            }
        }
    }

    private byte ReadNuvotonEc(ushort address)
    {
        byte bank = (byte)(address >> 8);
        byte reg = (byte)(address & 0xFF);
        WriteIoByte(_hwmAddrPort, 0x4E);
        WriteIoByte(_hwmDataPort, bank);
        WriteIoByte(_hwmAddrPort, reg);
        return ReadIoByte(_hwmDataPort);
    }

    private void WriteNuvotonEc(ushort address, byte value)
    {
        byte bank = (byte)(address >> 8);
        byte reg = (byte)(address & 0xFF);
        WriteIoByte(_hwmAddrPort, 0x4E);
        WriteIoByte(_hwmDataPort, bank);
        WriteIoByte(_hwmAddrPort, reg);
        WriteIoByte(_hwmDataPort, value);
    }

    private byte ReadIteReg(byte reg)
    {
        WriteIoByte(_hwmAddrPort, reg);
        return ReadIoByte(_hwmDataPort);
    }

    private byte ReadSioReg(ushort basePort, byte reg)
    {
        WriteIoByte(basePort, reg);
        return ReadIoByte((ushort)(basePort + 1));
    }

    private void WriteSioReg(ushort basePort, byte reg, byte val)
    {
        WriteIoByte(basePort, reg);
        WriteIoByte((ushort)(basePort + 1), val);
    }

    private byte ReadIoByte(ushort port)
    {
        if (_portDevice == null) return 0;
        byte[] inBuf = [(byte)(port & 0xFF), (byte)(port >> 8)];
        byte[] outBuf = new byte[1];
        DeviceIoControl(_portDevice, IOCTL_READ_PORT_UCHAR, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, IntPtr.Zero);
        return outBuf[0];
    }

    private void WriteIoByte(ushort port, byte value)
    {
        if (_portDevice == null) return;
        byte[] inBuf = [(byte)(port & 0xFF), (byte)(port >> 8), value];
        DeviceIoControl(_portDevice, IOCTL_WRITE_PORT_UCHAR, inBuf, inBuf.Length, null, 0, out _, IntPtr.Zero);
    }

    private IntPtr MapPhysicalMemory(ulong physicalAddress, uint size, out IntPtr sectionHandle)
    {
        sectionHandle = IntPtr.Zero;
        if (_mapMemoryMethod == null) return IntPtr.Zero;
        object?[] args = [unchecked((IntPtr)(long)physicalAddress), size, IntPtr.Zero];
        IntPtr mapped = (IntPtr)(_mapMemoryMethod.Invoke(null, args) ?? IntPtr.Zero);
        if (args[2] is IntPtr h)
            sectionHandle = h;
        return mapped;
    }

    private void UnmapPhysicalMemory(IntPtr sectionHandle, IntPtr mappedAddress)
    {
        if (mappedAddress == IntPtr.Zero || _unmapMemoryMethod == null) return;
        try
        {
            _unmapMemoryMethod.Invoke(null, [sectionHandle, mappedAddress]);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_ioLock)
        {
            try { _portDevice?.Dispose(); } catch { }
            _portDevice = null;
        }
    }
}
