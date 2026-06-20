using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    private const string SteamLobbyMaxPlayersAob = "89 87 A0 01 00 00 48 8B 47 70 48 8B 40 28 48 63 8F A0 01 00 00 89 48 2C";
    private static readonly string[] ServerSocketConnectionCapAobs =
    [
        "48 63 4E 70 3B C1 0F 8C ?? ?? ?? ?? 48 8B 46 10",
        "48 63 4E 70 3B C1 0F 8C ?? ?? ?? ??",
        "48 63 4E 70 3B C1"
    ];
    private static readonly byte[] SixteenPlayerConfigPatch = [0xB9, 0x10, 0x00, 0x00, 0x00, 0x90, 0x90];
    private static readonly byte[] SixteenPlayerSocketPatch = [0x83, 0xF8, 0x10, 0x90, 0x90, 0x90];

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
    private Hook? _sixteenPlayerSteamHook;
    private MemoryPatch? _sixteenPlayerConfigPatch;
    private MemoryPatch? _sixteenPlayerSocketPatch;
    private Timer? _speedFallbackTimer;
    private Timer? _weaponFallbackTimer;
    private Timer? _sixteenPlayerSocketRetryTimer;
    private readonly List<nint> _speedFallbackBases = [];
    private nint _dataBlock;
    private TrainerSettings _settings = TrainerSettings.Default;
    private DateTime _lastSpeedFallbackScan = DateTime.MinValue;
    private bool _reportedSpeedFallbackFound;
    private bool _sixteenPlayerLobbyRequested;

    public TrainerBackend()
    {
        _memory.TargetExited += (_, _) =>
        {
            lock (_gate)
            {
                _weaponHook = null;
                _speedHook = null;
                _sixteenPlayerSteamHook = null;
                _sixteenPlayerConfigPatch = null;
                _sixteenPlayerSocketPatch = null;
                _sixteenPlayerLobbyRequested = false;
                _dataBlock = 0;
                _speedFallbackBases.Clear();
                StopWeaponFallback();
                StopSixteenPlayerSocketRetry();
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

    public Task<bool> EnableSixteenPlayerLobbyAsync()
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                if (!_memory.IsAttached && !_memory.Attach(ProcessName, out var attachMessage))
                {
                    StatusChanged?.Invoke(attachMessage);
                    return false;
                }

                _sixteenPlayerLobbyRequested = true;
                var enabled = EnableSixteenPlayerLobbyCore();
                if (!enabled)
                {
                    _sixteenPlayerLobbyRequested = false;
                }

                return enabled;
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
                StatusChanged?.Invoke("Attached | Enabled");
                return true;
            }

            StatusChanged?.Invoke("Attached | Enabled");
            return true;
        }

        StatusChanged?.Invoke("Attached | Enabled");
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
        DisableSixteenPlayerLobbyCore();

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

    private void UpdateWeaponFallback()
    {
        if (!_memory.IsAttached || _dataBlock == 0)
        {
            StopWeaponFallback();
            return;
        }

        if (_settings.EnableInfAmmo || _settings.EnableFastFire || _settings.EnableNoSpread || _settings.EnableNoRecoil)
        {
            _weaponFallbackTimer ??= new Timer(_ =>
            {
                lock (_gate)
                {
                    ApplyLastWeaponSettings();
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(150));
            ApplyLastWeaponSettings();
            return;
        }

        StopWeaponFallback();
    }

    private void StopWeaponFallback()
    {
        _weaponFallbackTimer?.Dispose();
        _weaponFallbackTimer = null;
    }

    private void ApplyLastWeaponSettings()
    {
        if (!_memory.IsAttached || _dataBlock == 0)
        {
            return;
        }

        var weaponBase = ReadPointer(_dataBlock + DataLastWeaponBase);
        if (weaponBase == 0)
        {
            return;
        }

        if (_settings.EnableFastFire)
        {
            WriteFloat(weaponBase + 0x2AC, _settings.FireDelay);
            WriteFloat(weaponBase + 0x2FC, _settings.FireDelay);
        }

        if (_settings.EnableNoSpread)
        {
            WriteZeroDwords(weaponBase, 0x2D4, 0x2D8, 0x2DC, 0x2E0, 0x2E4, 0x2EC, 0x300);
        }

        if (_settings.EnableNoRecoil)
        {
            WriteZeroDwords(
                weaponBase,
                0x318, 0x31C, 0x320, 0x324, 0x328, 0x32C,
                0x340, 0x344, 0x348, 0x34C, 0x350,
                0x35C, 0x360, 0x364, 0x368, 0x36C, 0x370,
                0x374, 0x378, 0x37C, 0x380);
            WriteZeroBytes(weaponBase, 0x330, 0x33C, 0x33D, 0x354, 0x355, 0x356, 0x357, 0x358);
        }
    }

    private void UpdateSpeedFallback(TrainerSettings previousSettings)
    {
        if (!_memory.IsAttached)
        {
            StopSpeedFallback();
            return;
        }

        if (_settings.EnableSpeed)
        {
            CacheLastControllerBase();
            CacheMovementSpeedBlocks(previousSettings.SpeedMultiplier);

            _speedFallbackTimer ??= new Timer(_ =>
            {
                lock (_gate)
                {
                    RefreshSpeedFallback(restoreDefaults: false);
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
            ApplyKnownMovementSpeeds(_settings.SpeedMultiplier);
            return;
        }

        StopSpeedFallback();
        CacheLastControllerBase();
        RestoreKnownMovementSpeeds();
        if (previousSettings.EnableSpeed)
        {
            ScanAndRestoreMovementSpeeds(previousSettings.SpeedMultiplier);
        }
    }

    private void StopSpeedFallback()
    {
        _speedFallbackTimer?.Dispose();
        _speedFallbackTimer = null;
    }

    private void RefreshSpeedFallback(bool restoreDefaults)
    {
        if (!_memory.IsAttached)
        {
            return;
        }

        if (_speedFallbackBases.Count == 0 || DateTime.UtcNow - _lastSpeedFallbackScan > TimeSpan.FromSeconds(5))
        {
            _lastSpeedFallbackScan = DateTime.UtcNow;
            CacheLastControllerBase();
            var found = FindMovementSpeedBlocks(_settings.SpeedMultiplier);

            foreach (var controllerBase in found)
            {
                if (!_speedFallbackBases.Contains(controllerBase))
                {
                    _speedFallbackBases.Add(controllerBase);
                }
            }

            if (_speedFallbackBases.Count > 0 && !_reportedSpeedFallbackFound)
            {
                _reportedSpeedFallbackFound = true;
                StatusChanged?.Invoke("Attached | Enabled");
            }
        }

        if (_speedFallbackBases.Count == 0)
        {
            return;
        }

        var multiplier = restoreDefaults ? 1.0f : _settings.SpeedMultiplier;
        CacheLastControllerBase();
        ApplyKnownMovementSpeeds(multiplier);
    }

    private List<nint> FindMovementSpeedBlocks(float multiplier)
    {
        var patterns = new[]
        {
            BuildMovementSpeedBlockAob(multiplier),
            BuildMovementSpeedBlockAob(1.0f)
        };

        return patterns
            .SelectMany(pattern => AobScanner.ScanAll(_memory, pattern, maxResults: 24, writableOnly: true))
            .Select(address => address - 0x3A0)
            .Where(IsLikelyControllerBase)
            .Distinct()
            .ToList();
    }

    private void ScanAndRestoreMovementSpeeds(float previousMultiplier)
    {
        if (!_memory.IsAttached)
        {
            return;
        }

        var found = FindMovementSpeedBlocks(previousMultiplier);
        foreach (var controllerBase in found)
        {
            WriteMovementSpeeds(controllerBase, 1.0f);
            if (!_speedFallbackBases.Contains(controllerBase))
            {
                _speedFallbackBases.Add(controllerBase);
            }
        }
    }

    private void CacheMovementSpeedBlocks(float multiplier)
    {
        foreach (var controllerBase in FindMovementSpeedBlocks(multiplier))
        {
            if (!_speedFallbackBases.Contains(controllerBase))
            {
                _speedFallbackBases.Add(controllerBase);
            }
        }
    }

    private void CacheLastControllerBase()
    {
        if (!_memory.IsAttached || _dataBlock == 0)
        {
            return;
        }

        var controllerBase = ReadPointer(_dataBlock + DataLastControllerBase);
        if (controllerBase == 0 || !IsLikelyControllerBase(controllerBase))
        {
            return;
        }

        if (!_speedFallbackBases.Contains(controllerBase))
        {
            _speedFallbackBases.Add(controllerBase);
        }
    }

    private void ApplyKnownMovementSpeeds(float multiplier)
    {
        if (!_memory.IsAttached || _speedFallbackBases.Count == 0)
        {
            return;
        }

        foreach (var controllerBase in _speedFallbackBases.ToArray())
        {
            if (!WriteMovementSpeeds(controllerBase, multiplier))
            {
                _speedFallbackBases.Remove(controllerBase);
            }
        }
    }

    private bool WriteMovementSpeeds(nint controllerBase, float multiplier)
    {
        return WriteFloat(controllerBase + 0x3A0, 5.5f * multiplier) &&
               WriteFloat(controllerBase + 0x3A4, 8.5f * multiplier) &&
               WriteFloat(controllerBase + 0x3A8, 4.0f * multiplier) &&
               WriteFloat(controllerBase + 0x3B4, 6.0f * multiplier) &&
               WriteFloat(controllerBase + 0x3B8, 9.0f * multiplier);
    }

    private void RestoreKnownMovementSpeeds()
    {
        if (!_memory.IsAttached || _speedFallbackBases.Count == 0)
        {
            return;
        }

        foreach (var controllerBase in _speedFallbackBases.ToArray())
        {
            if (!WriteMovementSpeeds(controllerBase, 1.0f))
            {
                _speedFallbackBases.Remove(controllerBase);
            }
        }
    }

    private bool IsLikelyControllerBase(nint controllerBase)
    {
        return IsReasonableSpeed(ReadFloat(controllerBase + 0x3A0), 5.5f) &&
               IsReasonableSpeed(ReadFloat(controllerBase + 0x3A4), 8.5f) &&
               IsReasonableSpeed(ReadFloat(controllerBase + 0x3A8), 4.0f) &&
               IsReasonableSpeed(ReadFloat(controllerBase + 0x3B4), 6.0f) &&
               IsReasonableSpeed(ReadFloat(controllerBase + 0x3B8), 9.0f);
    }

    private float ReadFloat(nint address)
    {
        var bytes = new byte[4];
        return _memory.TryRead(address, bytes, out var read) && read == 4
            ? BitConverter.ToSingle(bytes)
            : float.NaN;
    }

    private nint ReadPointer(nint address)
    {
        var bytes = new byte[8];
        return _memory.TryRead(address, bytes, out var read) && read == 8
            ? new nint(BitConverter.ToInt64(bytes))
            : 0;
    }

    private bool WriteFloat(nint address, float value)
    {
        return _memory.Write(address, BitConverter.GetBytes(value));
    }

    private bool WriteByte(nint address, bool value)
    {
        return _memory.Write(address, [value ? (byte)1 : (byte)0]);
    }

    private void WriteZeroDwords(nint baseAddress, params int[] offsets)
    {
        var zero = BitConverter.GetBytes(0);
        foreach (var offset in offsets)
        {
            _memory.Write(baseAddress + offset, zero);
        }
    }

    private void WriteZeroBytes(nint baseAddress, params int[] offsets)
    {
        foreach (var offset in offsets)
        {
            _memory.Write(baseAddress + offset, [0]);
        }
    }

    private static bool IsReasonableSpeed(float actual, float normal)
    {
        if (float.IsNaN(actual) || actual <= 0 || actual > normal * 12.0f)
        {
            return false;
        }

        var ratio = actual / normal;
        return ratio is >= 0.75f and <= 10.25f;
    }


    private void UpdateSixteenPlayerLobby()
    {
        if (_settings.Enable16PlayerLobby)
        {
            EnableSixteenPlayerLobbyCore();
            return;
        }

        DisableSixteenPlayerLobbyCore();
    }

    private bool EnableSixteenPlayerLobbyCore()
    {
        if (!_memory.IsAttached)
        {
            StatusChanged?.Invoke("16 Player Lobby failed | not attached");
            return false;
        }

        if (_sixteenPlayerSteamHook?.IsInstalled == true &&
            _sixteenPlayerConfigPatch?.IsApplied == true)
        {
            if (_sixteenPlayerSocketPatch?.IsApplied == true)
            {
                return true;
            }

            if (TryApplySixteenPlayerSocketPatch(out _))
            {
                StopSixteenPlayerSocketRetry();
                StatusChanged?.Invoke("16 Player Lobby applied | change dropdown then create a fresh lobby");
                return true;
            }

            StartSixteenPlayerSocketRetry();
            StatusChanged?.Invoke("16 Player Lobby Steam patch applied | waiting for ServerSocket code");
            return true;
        }

        DisableSixteenPlayerLobbyCore();

        var steamMatches = AobScanner.ScanAll(_memory, SteamLobbyMaxPlayersAob, maxResults: 2);
        if (steamMatches.Count != 1)
        {
            StatusChanged?.Invoke($"16 Player Lobby failed | SteamLobby AOB matches: {steamMatches.Count}");
            return false;
        }

        var steamAddress = steamMatches[0];

        _sixteenPlayerSteamHook = new Hook(_memory, "16 Player Lobby Steam max hook", steamAddress, 6);
        if (!_sixteenPlayerSteamHook.Install(BuildSixteenPlayerSteamMaxHook, out var steamHookMessage))
        {
            StatusChanged?.Invoke($"16 Player Lobby failed | {steamHookMessage}");
            DisableSixteenPlayerLobbyCore();
            return false;
        }

        _sixteenPlayerConfigPatch = new MemoryPatch(
            _memory,
            "16 Player Lobby Steam config patch",
            steamAddress + 14,
            SixteenPlayerConfigPatch);

        if (!_sixteenPlayerConfigPatch.Apply(out var configMessage))
        {
            StatusChanged?.Invoke($"16 Player Lobby failed | {configMessage}");
            DisableSixteenPlayerLobbyCore();
            return false;
        }

        if (TryApplySixteenPlayerSocketPatch(out _))
        {
            StopSixteenPlayerSocketRetry();
            StatusChanged?.Invoke("16 Player Lobby applied | change dropdown then create a fresh lobby");
            return true;
        }

        StartSixteenPlayerSocketRetry();
        StatusChanged?.Invoke("16 Player Lobby Steam patch applied | waiting for ServerSocket code");
        return true;
    }

    private bool TryApplySixteenPlayerSocketPatch(out string message)
    {
        message = "ServerSocket AOB not found yet";

        if (_sixteenPlayerSocketPatch?.IsApplied == true)
        {
            message = "ServerSocket patch already applied";
            return true;
        }

        foreach (var pattern in ServerSocketConnectionCapAobs)
        {
            var matches = AobScanner.ScanAll(_memory, pattern, maxResults: 3);
            if (matches.Count != 1)
            {
                message = $"ServerSocket AOB matches: {matches.Count}";
                continue;
            }

            _sixteenPlayerSocketPatch = new MemoryPatch(
                _memory,
                "16 Player Lobby socket cap patch",
                matches[0],
                SixteenPlayerSocketPatch);

            if (_sixteenPlayerSocketPatch.Apply(out message))
            {
                return true;
            }

            _sixteenPlayerSocketPatch = null;
            return false;
        }

        return false;
    }

    private void StartSixteenPlayerSocketRetry()
    {
        _sixteenPlayerSocketRetryTimer ??= new Timer(_ =>
        {
            lock (_gate)
            {
                if (!_sixteenPlayerLobbyRequested || !_memory.IsAttached)
                {
                    StopSixteenPlayerSocketRetry();
                    return;
                }

                if (_sixteenPlayerSocketPatch?.IsApplied == true)
                {
                    StopSixteenPlayerSocketRetry();
                    return;
                }

                if (TryApplySixteenPlayerSocketPatch(out var message))
                {
                    StopSixteenPlayerSocketRetry();
                    StatusChanged?.Invoke("16 Player Lobby fully applied | change dropdown then create a fresh lobby");
                }
            }
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    private void StopSixteenPlayerSocketRetry()
    {
        _sixteenPlayerSocketRetryTimer?.Dispose();
        _sixteenPlayerSocketRetryTimer = null;
    }

    private void DisableSixteenPlayerLobbyCore()
    {
        _sixteenPlayerLobbyRequested = false;
        StopSixteenPlayerSocketRetry();

        var hadPatch = _sixteenPlayerSocketPatch?.IsApplied == true ||
                       _sixteenPlayerConfigPatch?.IsApplied == true ||
                       _sixteenPlayerSteamHook?.IsInstalled == true;

        _sixteenPlayerSocketPatch?.Restore();
        _sixteenPlayerSocketPatch = null;

        _sixteenPlayerConfigPatch?.Restore();
        _sixteenPlayerConfigPatch = null;

        _sixteenPlayerSteamHook?.Disable();
        _sixteenPlayerSteamHook = null;

        if (hadPatch && _memory.IsAttached)
        {
            StatusChanged?.Invoke("16 Player Lobby removed");
        }
    }

    private static byte[] BuildSixteenPlayerSteamMaxHook(nint caveAddress, nint returnAddress)
    {
        var code = new X64CodeBuilder(caveAddress);
        code.Emit(0xB8, 0x10, 0x00, 0x00, 0x00);
        code.Emit(0x89, 0x87, 0xA0, 0x01, 0x00, 0x00);
        code.JmpAbsolute(returnAddress);
        return code.ToArray();
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

        var weaponBase = ReadPointer(_dataBlock + DataFullAutoWeaponBase);
        if (weaponBase != 0)
        {
            var originalValue = new byte[1];
            if (_memory.TryRead(
                    _dataBlock + DataFullAutoOriginalValue,
                    originalValue,
                    out var originalBytesRead) &&
                originalBytesRead == originalValue.Length)
            {
                _memory.Write(weaponBase + 0x2F2, originalValue);
            }
        }

        _memory.Write(_dataBlock + DataFullAutoWeaponBase, new byte[sizeof(long)]);
        _memory.Write(_dataBlock + DataFullAutoOriginalValue, [0]);
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
        code.MovRbxPtrR10Disp8(DataLastControllerBase);
        code.TestRbxRbx();
        code.Je("afterFullAuto");
        code.CmpRbxPtrR15Disp32(0x238);
        code.Jne("afterFullAuto");
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
        code.MovRbxPtrR10Disp8(DataLastControllerBase);
        code.TestRbxRbx();
        code.Je("originalAmmoWrite");
        code.CmpRbxPtrR15Disp32(0x238);
        code.Jne("originalAmmoWrite");
        code.CmpDwordPtrR15Disp32Imm8(0x2A8, 0);
        code.Jne("afterCurrentAmmoRefill");
        code.MovDwordPtrR15Disp32Imm32(0x2A8, 1);
        code.Label("afterCurrentAmmoRefill");
        code.MovDwordPtrR15Disp32Imm32(0x398, 1);
        code.MovDwordPtrR15Disp32Imm32(0x3A0, FloatBits(1.0f));
        code.MovBytePtrR15Disp32Imm8(0x394, 0);
        code.MovBytePtrR15Disp32Imm8(0x395, 0);
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

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        BitConverter.GetBytes(value).CopyTo(data, offset);
    }

    private static uint FloatBits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    private static string BuildMovementSpeedBlockAob(float multiplier)
    {
        var bytes = new List<string>();
        AddFloatPattern(bytes, 5.5f * multiplier);
        AddFloatPattern(bytes, 8.5f * multiplier);
        AddFloatPattern(bytes, 4.0f * multiplier);
        bytes.AddRange(["??", "??", "??", "??", "??", "??", "??", "??"]);
        AddFloatPattern(bytes, 6.0f * multiplier);
        AddFloatPattern(bytes, 9.0f * multiplier);
        return string.Join(' ', bytes);
    }

    private static void AddFloatPattern(List<string> bytes, float value)
    {
        foreach (var item in BitConverter.GetBytes(value))
        {
            bytes.Add(item.ToString("X2"));
        }
    }
}


internal sealed class MemoryPatch
{
    private readonly ProcessMemory _memory;
    private readonly byte[] _newBytes;
    private byte[] _originalBytes = [];

    public MemoryPatch(ProcessMemory memory, string name, nint address, byte[] newBytes)
    {
        _memory = memory;
        Name = name;
        Address = address;
        _newBytes = newBytes;
    }

    public string Name { get; }
    public nint Address { get; }
    public bool IsApplied { get; private set; }

    public bool Apply(out string message)
    {
        if (IsApplied)
        {
            message = $"{Name} is already applied";
            return true;
        }

        if (!_memory.IsAttached)
        {
            message = $"{Name}: not attached";
            return false;
        }

        _originalBytes = _memory.Read(Address, _newBytes.Length);

        if (!_memory.Protect(
                Address,
                _newBytes.Length,
                NativeMethods.MemoryProtection.ExecuteReadWrite,
                out var oldProtection))
        {
            message = $"{Name}: could not unlock target bytes";
            _originalBytes = [];
            return false;
        }

        var written = _memory.Write(Address, _newBytes);
        _memory.Protect(Address, _newBytes.Length, oldProtection, out _);

        if (!written)
        {
            message = $"{Name}: could not write patch";
            _originalBytes = [];
            return false;
        }

        IsApplied = true;
        message = $"{Name} applied at 0x{Address:X}";
        return true;
    }

    public void Restore()
    {
        if (!IsApplied)
        {
            return;
        }

        if (_memory.IsAttached && _originalBytes.Length > 0 &&
            _memory.Protect(
                Address,
                _originalBytes.Length,
                NativeMethods.MemoryProtection.ExecuteReadWrite,
                out var oldProtection))
        {
            _memory.Write(Address, _originalBytes);
            _memory.Protect(Address, _originalBytes.Length, oldProtection, out _);
        }

        _originalBytes = [];
        IsApplied = false;
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
    bool Enable16PlayerLobby,
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
        Enable16PlayerLobby: false,
        FireDelay: 0.01f,
        SpeedMultiplier: 4.0f);
}
