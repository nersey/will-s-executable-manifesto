using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace STRAFTATWpfTrainer;

internal sealed class ProcessMemory : IDisposable
{
    private const long NearAllocationRange = 0x70000000;
    private const long AllocationStep = 0x10000;

    private static readonly NativeMethods.ProcessAccessRights DesiredAccess =
        NativeMethods.ProcessAccessRights.QueryInformation |
        NativeMethods.ProcessAccessRights.QueryLimitedInformation |
        NativeMethods.ProcessAccessRights.VmOperation |
        NativeMethods.ProcessAccessRights.VmRead |
        NativeMethods.ProcessAccessRights.VmWrite;

    public event EventHandler? TargetExited;

    public Process? TargetProcess { get; private set; }
    public nint Handle { get; private set; }

    public bool IsAttached => Handle != 0 && TargetProcess is { HasExited: false };
    public string ProcessName => TargetProcess?.ProcessName ?? "not attached";

    public bool Attach(string processName, out string message)
    {
        DisposeHandle();

        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            message = $"{processName}.exe not found";
            return false;
        }

        TargetProcess = processes[0];
        TargetProcess.EnableRaisingEvents = true;
        TargetProcess.Exited += OnTargetExited;

        Handle = NativeMethods.OpenProcess(DesiredAccess, false, TargetProcess.Id);
        if (Handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            DisposeHandle();
            message = $"Could not open {processName}.exe (Win32 error {error})";
            return false;
        }

        message = $"Attached to {TargetProcess.ProcessName}.exe";
        return true;
    }

    public bool TryRead(nint address, byte[] buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (!IsAttached || buffer.Length == 0)
        {
            return false;
        }

        var ok = NativeMethods.ReadProcessMemory(
            Handle,
            address,
            buffer,
            buffer.Length,
            out var nativeBytesRead);

        bytesRead = (int)nativeBytesRead;
        return ok && bytesRead > 0;
    }

    public byte[] Read(nint address, int length)
    {
        var buffer = new byte[length];
        if (!TryRead(address, buffer, out var bytesRead) || bytesRead != length)
        {
            throw new InvalidOperationException($"Could not read 0x{address:X}");
        }

        return buffer;
    }

    public bool Write(nint address, byte[] bytes)
    {
        if (!IsAttached || bytes.Length == 0)
        {
            return false;
        }

        return NativeMethods.WriteProcessMemory(
            Handle,
            address,
            bytes,
            bytes.Length,
            out var written) && written == bytes.Length;
    }

    public bool Protect(
        nint address,
        int length,
        NativeMethods.MemoryProtection protection,
        out NativeMethods.MemoryProtection oldProtection)
    {
        oldProtection = default;
        return IsAttached &&
               NativeMethods.VirtualProtectEx(Handle, address, (nuint)length, protection, out oldProtection);
    }

    public nint Allocate(int size, NativeMethods.MemoryProtection protection)
    {
        if (!IsAttached)
        {
            return 0;
        }

        return NativeMethods.VirtualAllocEx(
            Handle,
            0,
            (nuint)size,
            NativeMethods.AllocationType.Commit | NativeMethods.AllocationType.Reserve,
            protection);
    }

    public nint AllocateNear(nint targetAddress, int size, NativeMethods.MemoryProtection protection)
    {
        if (!IsAttached)
        {
            return 0;
        }

        var target = targetAddress.ToInt64();
        var start = target & ~0xFFFFL;

        for (long distance = 0; distance <= NearAllocationRange; distance += AllocationStep)
        {
            foreach (var candidate in new[] { start + distance, start - distance })
            {
                if (candidate <= 0)
                {
                    continue;
                }

                var address = NativeMethods.VirtualAllocEx(
                    Handle,
                    new nint(candidate),
                    (nuint)size,
                    NativeMethods.AllocationType.Commit | NativeMethods.AllocationType.Reserve,
                    protection);

                if (address != 0 && FitsRelativeJump(targetAddress, address))
                {
                    return address;
                }

                if (address != 0)
                {
                    Free(address);
                }
            }
        }

        var fallback = Allocate(size, protection);
        return fallback != 0 && FitsRelativeJump(targetAddress, fallback) ? fallback : 0;
    }

    public bool Free(nint address)
    {
        return IsAttached && address != 0 &&
               NativeMethods.VirtualFreeEx(Handle, address, 0, NativeMethods.FreeType.Release);
    }

    public void Dispose()
    {
        DisposeHandle();
    }

    private static bool FitsRelativeJump(nint source, nint destination)
    {
        var delta = destination.ToInt64() - (source.ToInt64() + 5);
        return delta is >= int.MinValue and <= int.MaxValue;
    }

    private void OnTargetExited(object? sender, EventArgs e)
    {
        TargetExited?.Invoke(this, EventArgs.Empty);
        DisposeHandle();
    }

    private void DisposeHandle()
    {
        if (TargetProcess != null)
        {
            TargetProcess.Exited -= OnTargetExited;
            TargetProcess.Dispose();
            TargetProcess = null;
        }

        if (Handle != 0)
        {
            NativeMethods.CloseHandle(Handle);
            Handle = 0;
        }
    }
}
