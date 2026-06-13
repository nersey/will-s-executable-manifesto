using System;
using System.Threading.Tasks;

namespace STRAFTATWpfTrainer;

internal sealed class TrainerBackend : IDisposable
{
    private const string ProcessName = "STRAFTAT";
    private const string WeaponAob = "41 89 87 A8 02 00 00";
    private static readonly string[] SpeedAobs =
    [
        "48 8B 86 B8 02 00 00 48 8B D5 48 81 C2 60 FF FF FF 48 8B C8 83 38 00",
        "48 8B 86 B8 02 00 00 48 8B D5",
        "48 8B 86 B8 02 00 00"
    ];

    private const int DataEnableInfAmmo = 0;
    private const int DataEnableFastFire = 1;
    private const int DataEnableNoSpread = 2;
    private const int DataEnableNoRecoil = 3;
    private const int DataEnableSpeed = 4;
    private const int DataEnableFullAuto = 5;
    private const int DataEnableFlyMode = 6;
    private const int DataFireDelay = 8;
    private const int DataSpeedMultiplier = 12;
    private const int DataWalkSpeed = 16;
    private const int DataSprintSpeed = 20;
    private const int DataCrouchSpeed = 24;
    private const int DataAirSpeed = 28;
    private const int DataSprintAirSpeed = 32;
    private const int DataLastWeaponBase = 40;
    private const int DataLastControllerBase = 48;
    private const int DataFullAutoWeaponBase = 56;
    private const int DataFullAutoOriginalValue = 64;
    private const int DataBlockSize = 80;

    private readonly ProcessMemory _memory = new();
    private readonly object _gate = new();
    private Hook? _weaponHook;
    private Hook? _speedHook;
    private nint _dataBlock;
    private TrainerSettings _settings = TrainerSettings.Default;

    public TrainerBackend()
    {
        _memory.TargetExited += (_, _) =>
        {
            lock (_gate)
            {
                _weaponHook = null;
                _speedHook = null;
                _dataBlock = 0;
            }

            StatusChanged?.Invoke("Not attached");
        };
    }

    public event Action<string>? StatusChanged;

    public bool HooksEnabled => _weaponHook?.IsInstalled == true || _speedHook?.IsInstalled == true;
    public bool MovementHookEnabled => _speedHook?.IsInstalled == true;

    public Task<bool> AttachAndEnableAsync(TrainerSettings settings)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                _settings = settings;
                return AttachAndEnable();
            }
        });
    }

    public void UpdateSettings(TrainerSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            WriteSettings();
        }
    }

    public void DisableHooks()
    {
        lock (_gate)
        {
            DisableHooksCore(freeDataBlock: true);
            StatusChanged?.Invoke(_memory.IsAttached ? "Attached | Disabled" : "Not attached");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisableHooksCore(freeDataBlock: true);
            _memory.Dispose();
        }
    }

    private bool AttachAndEnable()
    {
        if (HooksEnabled)
        {
            WriteSettings();
            StatusChanged?.Invoke("Attached | Enabled");
            return true;
        }

        if (!_memory.IsAttached && !_memory.Attach(ProcessName, out var attachMessage))
        {
            StatusChanged?.Invoke(attachMessage);
            return false;
        }

        var weaponAddress = AobScanner.Scan(_memory, WeaponAob);
        if (weaponAddress == 0)
        {
            StatusChanged?.Invoke("YOU did something wrong so now you have to restart smh");
            return false;
        }

        var speedAddress = FindMovementHook();

        if (_dataBlock == 0)
        {
            _dataBlock = _memory.Allocate(DataBlockSize, NativeMethods.MemoryProtection.ReadWrite);
            if (_dataBlock == 0)
            {
                StatusChanged?.Invoke("Could not enable");
                return false;
            }
        }

        if (!WriteSettings())
        {
            StatusChanged?.Invoke("Could not enable");
            return false;
        }

        _weaponHook = new Hook(_memory, "Weapon hook", weaponAddress, 7);
        if (!_weaponHook.Install(BuildWeaponHook, out var weaponMessage))
        {
            StatusChanged?.Invoke("Could not enable");
            DisableHooksCore(freeDataBlock: true);
            return false;
        }

        if (speedAddress != 0)
        {
            _speedHook = new Hook(_memory, "Movement hook", speedAddress, 7);
            if (!_speedHook.Install(BuildMovementHook, out var speedMessage))
            {
                _speedHook = null;
                StatusChanged?.Invoke($"Enabled | W:0x{weaponAddress:X} S:0x{speedAddress:X} D:0x{_dataBlock:X} M:no");
                return true;
            }

            StatusChanged?.Invoke($"Enabled | W:0x{weaponAddress:X} S:0x{speedAddress:X} D:0x{_dataBlock:X} M:yes");
            return true;
        }

        StatusChanged?.Invoke($"Enabled | W:0x{weaponAddress:X} S:0x0 D:0x{_dataBlock:X} M:no");
        return true;
    }

    private nint FindMovementHook()
    {
        foreach (var pattern in SpeedAobs)
        {
            var address = AobScanner.Scan(_memory, pattern);
            if (address != 0)
            {
                return address;
            }
        }

        return 0;
    }

    private void DisableHooksCore(bool freeDataBlock)
    {
        _speedHook?.Disable();
        _speedHook = null;

        _weaponHook?.Disable();
        _weaponHook = null;
        RestoreFullAutoWeapon();

        if (freeDataBlock && _dataBlock != 0 && _memory.IsAttached)
        {
            _memory.Free(_dataBlock);
            _dataBlock = 0;
        }
    }

    private bool WriteFloat(nint address, float value)
    {
        return _memory.Write(address, BitConverter.GetBytes(value));
    }

    private bool WriteSettings()
    {
        if (!_memory.IsAttached || _dataBlock == 0)
        {
            return false;
        }

        if (!WriteFloat(_dataBlock + DataFireDelay, _settings.FireDelay) ||
            !WriteFloat(_dataBlock + DataSpeedMultiplier, _settings.SpeedMultiplier) ||
            !WriteFloat(_dataBlock + DataWalkSpeed, 5.5f * _settings.SpeedMultiplier) ||
            !WriteFloat(_dataBlock + DataSprintSpeed, 8.5f * _settings.SpeedMultiplier) ||
            !WriteFloat(_dataBlock + DataCrouchSpeed, 4.0f * _settings.SpeedMultiplier) ||
            !WriteFloat(_dataBlock + DataAirSpeed, 6.0f * _settings.SpeedMultiplier) ||
            !WriteFloat(_dataBlock + DataSprintAirSpeed, 9.0f * _settings.SpeedMultiplier))
        {
            return false;
        }

        var flagsWritten = _memory.Write(
            _dataBlock,
            [
                _settings.EnableInfAmmo ? (byte)1 : (byte)0,
                _settings.EnableFastFire ? (byte)1 : (byte)0,
                _settings.EnableNoSpread ? (byte)1 : (byte)0,
                _settings.EnableNoRecoil ? (byte)1 : (byte)0,
                _settings.EnableSpeed ? (byte)1 : (byte)0,
                _settings.EnableFullAuto ? (byte)1 : (byte)0,
                _settings.EnableFlyMode ? (byte)1 : (byte)0
            ]);

        if (flagsWritten && _settings.EnableInfAmmo)
        {
            ApplyInfiniteAmmoToLastWeapon();
        }

        if (flagsWritten && !_settings.EnableFullAuto)
        {
            RestoreFullAutoWeapon();
        }

        return flagsWritten;
    }

    private void RestoreFullAutoWeapon()
    {
        if (!_memory.IsAttached || _dataBlock == 0)
        {
            return;
        }

        var pointerBytes = new byte[sizeof(long)];
        var originalValue = new byte[1];
        if (!_memory.TryRead(
                _dataBlock + DataFullAutoWeaponBase,
                pointerBytes,
                out var pointerBytesRead) ||
            pointerBytesRead != pointerBytes.Length ||
            !_memory.TryRead(
                _dataBlock + DataFullAutoOriginalValue,
                originalValue,
                out var originalBytesRead) ||
            originalBytesRead != originalValue.Length)
        {
            return;
        }

        var weaponBase = new nint(BitConverter.ToInt64(pointerBytes));
        if (weaponBase != 0)
        {
            _memory.Write(weaponBase + 0x2F2, originalValue);
        }

        _memory.Write(
            _dataBlock + DataFullAutoWeaponBase,
            new byte[sizeof(long)]);
        _memory.Write(
            _dataBlock + DataFullAutoOriginalValue,
            [0]);
    }

    private void ApplyInfiniteAmmoToLastWeapon()
    {
        var pointerBytes = new byte[sizeof(long)];
        if (!_memory.TryRead(
                _dataBlock + DataLastWeaponBase,
                pointerBytes,
                out var bytesRead) ||
            bytesRead != pointerBytes.Length)
        {
            return;
        }

        var weaponBase = new nint(BitConverter.ToInt64(pointerBytes));
        if (weaponBase == 0)
        {
            return;
        }

        _memory.Write(weaponBase + 0x2A8, BitConverter.GetBytes(999));
        _memory.Write(weaponBase + 0x398, BitConverter.GetBytes(999));
        _memory.Write(weaponBase + 0x3A0, BitConverter.GetBytes(999.0f));
    }

    private byte[] BuildWeaponHook(nint caveAddress, nint returnAddress)
    {
        var code = new X64CodeBuilder(caveAddress);

        code.PushFlags();
        code.PushRax();
        code.PushRbx();
        code.PushR10();
        code.MovR10Imm64(_dataBlock);
        code.MovPtrR10Disp8R15(DataLastWeaponBase);

        code.CmpBytePtrR10Disp8(DataEnableFullAuto, 0);
        code.Je("afterFullAuto");
        code.MovRbxPtrR10Disp8(DataFullAutoWeaponBase);
        code.CmpRbxR15();
        code.Je("forceFullAuto");
        code.TestRbxRbx();
        code.Je("captureFullAutoWeapon");
        code.MovAlPtrR10Disp8(DataFullAutoOriginalValue);
        code.MovPtrRbxDisp32Al(0x2F2);
        code.Label("captureFullAutoWeapon");
        code.MovPtrR10Disp8R15(DataFullAutoWeaponBase);
        code.MovAlPtrR15Disp32(0x2F2);
        code.MovPtrR10Disp8Al(DataFullAutoOriginalValue);
        code.Label("forceFullAuto");
        code.MovBytePtrR15Disp32Imm8(0x2F2, 0);
        code.Label("afterFullAuto");

        code.CmpBytePtrR10Disp8(DataEnableFastFire, 0);
        code.Je("afterFastFire");
        code.MovEbxPtrR10Disp8(DataFireDelay);
        code.MovPtrR15Disp32Ebx(0x2AC);
        code.MovPtrR15Disp32Ebx(0x2FC);
        code.Label("afterFastFire");

        code.CmpBytePtrR10Disp8(DataEnableNoSpread, 0);
        code.Je("afterNoSpread");
        WriteZeroDwordsR15(code, 0x2D4, 0x2D8, 0x2DC, 0x2E0, 0x2E4, 0x2EC, 0x300);
        code.Label("afterNoSpread");

        code.CmpBytePtrR10Disp8(DataEnableNoRecoil, 0);
        code.Je("afterNoRecoil");
        WriteZeroDwordsR15(
            code,
            0x318, 0x31C, 0x320, 0x324, 0x328, 0x32C,
            0x340, 0x344, 0x348, 0x34C, 0x350,
            0x35C, 0x360, 0x364, 0x368, 0x36C, 0x370,
            0x374, 0x378, 0x37C, 0x380);
        WriteZeroBytesR15(code, 0x330, 0x33C, 0x33D, 0x354, 0x355, 0x356, 0x357, 0x358);
        code.Label("afterNoRecoil");

        code.CmpBytePtrR10Disp8(DataEnableInfAmmo, 0);
        code.Je("originalAmmoWrite");
        code.MovDwordPtrR15Disp32Imm32(0x2A8, 0x000003E7);
        code.MovDwordPtrR15Disp32Imm32(0x398, 0x000003E7);
        code.MovDwordPtrR15Disp32Imm32(0x3A0, 0x4479C000);
        code.Jmp("afterAmmoWrite");
        code.Label("originalAmmoWrite");
        code.MovPtrR15Disp32Eax(0x2A8);
        code.Label("afterAmmoWrite");

        code.PopR10();
        code.PopRbx();
        code.PopRax();
        code.PopFlags();
        code.JmpAbsolute(returnAddress);

        return code.ToArray();
    }

    private byte[] BuildMovementHook(nint caveAddress, nint returnAddress)
    {
        var code = new X64CodeBuilder(caveAddress);

        code.PushFlags();
        code.PushRbx();
        code.PushR10();
        code.MovRaxPtrRsiDisp32(0x2B8);
        code.MovR10Imm64(_dataBlock);
        code.MovPtrR10Disp8Rsi(DataLastControllerBase);

        code.CmpBytePtrR10Disp8(DataEnableFlyMode, 0);
        code.Je("flyOff");
        code.MovBytePtrRsiDisp32Imm8(0x664, 1);
        code.Jmp("afterFly");
        code.Label("flyOff");
        code.MovBytePtrRsiDisp32Imm8(0x664, 0);
        code.Label("afterFly");

        code.CmpBytePtrR10Disp8(DataEnableSpeed, 0);
        code.Je("restoreDefaults");

        code.MovEbxPtrR10Disp8(DataWalkSpeed);
        code.MovPtrRsiDisp32Ebx(0x3A0);
        code.MovEbxPtrR10Disp8(DataSprintSpeed);
        code.MovPtrRsiDisp32Ebx(0x3A4);
        code.MovEbxPtrR10Disp8(DataCrouchSpeed);
        code.MovPtrRsiDisp32Ebx(0x3A8);
        code.MovEbxPtrR10Disp8(DataAirSpeed);
        code.MovPtrRsiDisp32Ebx(0x3B4);
        code.MovEbxPtrR10Disp8(DataSprintAirSpeed);
        code.MovPtrRsiDisp32Ebx(0x3B8);
        code.Jmp("doneSpeed");

        code.Label("restoreDefaults");
        code.MovDwordPtrRsiDisp32Imm32(0x3A0, FloatBits(5.5f));
        code.MovDwordPtrRsiDisp32Imm32(0x3A4, FloatBits(8.5f));
        code.MovDwordPtrRsiDisp32Imm32(0x3A8, FloatBits(4.0f));
        code.MovDwordPtrRsiDisp32Imm32(0x3B4, FloatBits(6.0f));
        code.MovDwordPtrRsiDisp32Imm32(0x3B8, FloatBits(9.0f));

        code.Label("doneSpeed");
        code.PopR10();
        code.PopRbx();
        code.PopFlags();
        code.JmpAbsolute(returnAddress);

        return code.ToArray();
    }

    private static void WriteZeroDwordsR15(X64CodeBuilder code, params int[] offsets)
    {
        foreach (var offset in offsets)
        {
            code.MovDwordPtrR15Disp32Imm32(offset, 0);
        }
    }

    private static void WriteZeroBytesR15(X64CodeBuilder code, params int[] offsets)
    {
        foreach (var offset in offsets)
        {
            code.MovBytePtrR15Disp32Imm8(offset, 0);
        }
    }

    private static uint FloatBits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }
}

internal readonly record struct TrainerSettings(
    bool EnableInfAmmo,
    bool EnableFastFire,
    bool EnableFullAuto,
    bool EnableNoSpread,
    bool EnableNoRecoil,
    bool EnableSpeed,
    bool EnableFlyMode,
    float FireDelay,
    float SpeedMultiplier)
{
    public static TrainerSettings Default => new(
        EnableInfAmmo: false,
        EnableFastFire: false,
        EnableFullAuto: false,
        EnableNoSpread: false,
        EnableNoRecoil: false,
        EnableSpeed: false,
        EnableFlyMode: false,
        FireDelay: 0.20f,
        SpeedMultiplier: 4.0f);
}
