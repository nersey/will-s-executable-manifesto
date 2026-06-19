using System;
using System.Collections.Generic;
using System.Globalization;

namespace STRAFTATWpfTrainer;

internal static class AobScanner
{
    private const int ChunkSize = 0x100000;

    public static nint Scan(ProcessMemory memory, string pattern, Action<string>? log = null)
    {
        var parsed = ParsePattern(pattern);
        if (parsed.Length == 0)
        {
            return 0;
        }

        NativeMethods.GetNativeSystemInfo(out var systemInfo);
        var address = systemInfo.MinimumApplicationAddress.ToInt64();
        var maxAddress = systemInfo.MaximumApplicationAddress.ToInt64();
        var mbiSize = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address > 0 && address < maxAddress && memory.IsAttached)
        {
            if (NativeMethods.VirtualQueryEx(memory.Handle, new nint(address), out var info, mbiSize) == 0)
            {
                address += systemInfo.PageSize;
                continue;
            }

            var baseAddress = info.BaseAddress.ToInt64();
            var regionSize = (long)info.RegionSize;
            var nextAddress = baseAddress + regionSize;

            if (IsScannable(info))
            {
                var found = ScanRegion(memory, baseAddress, regionSize, parsed);
                if (found != 0)
                {
                    log?.Invoke($"AOB found at 0x{found:X}");
                    return found;
                }
            }

            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        log?.Invoke("AOB not found");
        return 0;
    }

    public static List<nint> ScanAll(
        ProcessMemory memory,
        string pattern,
        int maxResults = 32,
        bool writableOnly = false)
    {
        var results = new List<nint>();
        var parsed = ParsePattern(pattern);
        if (parsed.Length == 0)
        {
            return results;
        }

        NativeMethods.GetNativeSystemInfo(out var systemInfo);
        var address = systemInfo.MinimumApplicationAddress.ToInt64();
        var maxAddress = systemInfo.MaximumApplicationAddress.ToInt64();
        var mbiSize = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address > 0 && address < maxAddress && memory.IsAttached && results.Count < maxResults)
        {
            if (NativeMethods.VirtualQueryEx(memory.Handle, new nint(address), out var info, mbiSize) == 0)
            {
                address += systemInfo.PageSize;
                continue;
            }

            var baseAddress = info.BaseAddress.ToInt64();
            var regionSize = (long)info.RegionSize;
            var nextAddress = baseAddress + regionSize;

            if (IsScannable(info) && (!writableOnly || IsWritable(info)))
            {
                ScanRegionAll(memory, baseAddress, regionSize, parsed, results, maxResults);
            }

            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        return results;
    }

    private static nint ScanRegion(ProcessMemory memory, long baseAddress, long regionSize, byte?[] pattern)
    {
        var overlap = Math.Max(0, pattern.Length - 1);
        var step = Math.Max(1, ChunkSize - overlap);

        for (long offset = 0; offset < regionSize; offset += step)
        {
            var bytesToRead = (int)Math.Min(ChunkSize, regionSize - offset);
            var buffer = new byte[bytesToRead];

            if (!memory.TryRead(new nint(baseAddress + offset), buffer, out var bytesRead))
            {
                continue;
            }

            var foundOffset = IndexOf(buffer, bytesRead, pattern);
            if (foundOffset >= 0)
            {
                return new nint(baseAddress + offset + foundOffset);
            }
        }

        return 0;
    }

    private static void ScanRegionAll(
        ProcessMemory memory,
        long baseAddress,
        long regionSize,
        byte?[] pattern,
        List<nint> results,
        int maxResults)
    {
        var overlap = Math.Max(0, pattern.Length - 1);
        var step = Math.Max(1, ChunkSize - overlap);

        for (long offset = 0; offset < regionSize && results.Count < maxResults; offset += step)
        {
            var bytesToRead = (int)Math.Min(ChunkSize, regionSize - offset);
            var buffer = new byte[bytesToRead];

            if (!memory.TryRead(new nint(baseAddress + offset), buffer, out var bytesRead))
            {
                continue;
            }

            var searchStart = 0;
            while (searchStart < bytesRead && results.Count < maxResults)
            {
                var foundOffset = IndexOf(buffer, bytesRead, pattern, searchStart);
                if (foundOffset < 0)
                {
                    break;
                }

                results.Add(new nint(baseAddress + offset + foundOffset));
                searchStart = foundOffset + 1;
            }
        }
    }

    private static int IndexOf(byte[] buffer, int length, byte?[] pattern, int start = 0)
    {
        var limit = length - pattern.Length;
        for (var i = start; i <= limit; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (pattern[j].HasValue && buffer[i + j] != pattern[j]!.Value)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private static byte?[] ParsePattern(string pattern)
    {
        var bytes = new List<byte?>();
        foreach (var part in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add(part is "?" or "??"
                ? null
                : byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return bytes.ToArray();
    }

    private static bool IsScannable(NativeMethods.MemoryBasicInformation info)
    {
        if (info.State != (uint)NativeMethods.MemoryState.Commit)
        {
            return false;
        }

        var protect = (NativeMethods.MemoryProtection)info.Protect;
        if ((protect & NativeMethods.MemoryProtection.Guard) != 0 ||
            (protect & NativeMethods.MemoryProtection.NoAccess) != 0)
        {
            return false;
        }

        return (protect & (
            NativeMethods.MemoryProtection.ReadOnly |
            NativeMethods.MemoryProtection.ReadWrite |
            NativeMethods.MemoryProtection.WriteCopy |
            NativeMethods.MemoryProtection.Execute |
            NativeMethods.MemoryProtection.ExecuteRead |
            NativeMethods.MemoryProtection.ExecuteReadWrite |
            NativeMethods.MemoryProtection.ExecuteWriteCopy)) != 0;
    }

    private static bool IsWritable(NativeMethods.MemoryBasicInformation info)
    {
        var protect = (NativeMethods.MemoryProtection)info.Protect;
        return (protect & (
            NativeMethods.MemoryProtection.ReadWrite |
            NativeMethods.MemoryProtection.WriteCopy |
            NativeMethods.MemoryProtection.ExecuteReadWrite |
            NativeMethods.MemoryProtection.ExecuteWriteCopy)) != 0;
    }
}
