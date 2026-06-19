using System;
using System.Collections.Generic;

namespace STRAFTATWpfTrainer;

internal sealed class Hook : IDisposable
{
    private readonly ProcessMemory _memory;
    private readonly int _patchLength;
    private byte[] _originalBytes = [];
    private nint _codeCave;

    public Hook(ProcessMemory memory, string name, nint targetAddress, int patchLength)
    {
        _memory = memory;
        Name = name;
        TargetAddress = targetAddress;
        _patchLength = patchLength;
    }

    public string Name { get; }
    public nint TargetAddress { get; }
    public bool IsInstalled { get; private set; }

    public bool Install(Func<nint, nint, byte[]> buildCode, out string message)
    {
        if (IsInstalled)
        {
            message = $"{Name} is already enabled";
            return true;
        }

        if (!_memory.IsAttached)
        {
            message = "Not attached";
            return false;
        }

        _originalBytes = _memory.Read(TargetAddress, _patchLength);
        _codeCave = _memory.AllocateNear(
            TargetAddress,
            0x1000,
            NativeMethods.MemoryProtection.ExecuteReadWrite);

        if (_codeCave == 0)
        {
            message = $"{Name}: could not allocate nearby code cave";
            return false;
        }

        var code = buildCode(_codeCave, TargetAddress + _patchLength);
        if (code.Length > 0x1000)
        {
            message = $"{Name}: generated code was too large";
            _memory.Free(_codeCave);
            _codeCave = 0;
            return false;
        }

        if (!_memory.Write(_codeCave, code))
        {
            message = $"{Name}: could not write code cave";
            _memory.Free(_codeCave);
            _codeCave = 0;
            return false;
        }

        if (!TryBuildJump(TargetAddress, _codeCave, _patchLength, out var patch))
        {
            message = $"{Name}: code cave is too far away for a safe patch";
            _memory.Free(_codeCave);
            _codeCave = 0;
            return false;
        }

        if (!_memory.Protect(
                TargetAddress,
                _patchLength,
                NativeMethods.MemoryProtection.ExecuteReadWrite,
                out var oldProtection))
        {
            message = $"{Name}: could not unlock target bytes";
            _memory.Free(_codeCave);
            _codeCave = 0;
            return false;
        }

        var patched = _memory.Write(TargetAddress, patch);
        _memory.Protect(TargetAddress, _patchLength, oldProtection, out _);

        if (!patched)
        {
            message = $"{Name}: could not patch target bytes";
            _memory.Free(_codeCave);
            _codeCave = 0;
            return false;
        }

        IsInstalled = true;
        message = $"{Name} enabled at 0x{TargetAddress:X}";
        return true;
    }

    public void Disable()
    {
        if (!IsInstalled)
        {
            return;
        }

        if (_memory.IsAttached && _originalBytes.Length > 0)
        {
            if (_memory.Protect(
                    TargetAddress,
                    _originalBytes.Length,
                    NativeMethods.MemoryProtection.ExecuteReadWrite,
                    out var oldProtection))
            {
                _memory.Write(TargetAddress, _originalBytes);
                _memory.Protect(TargetAddress, _originalBytes.Length, oldProtection, out _);
            }
        }

        if (_memory.IsAttached && _codeCave != 0)
        {
            _memory.Free(_codeCave);
        }

        _codeCave = 0;
        _originalBytes = [];
        IsInstalled = false;
    }

    public void Dispose()
    {
        Disable();
    }

    private static bool TryBuildJump(nint source, nint destination, int patchLength, out byte[] patch)
    {
        patch = new byte[patchLength];
        var delta = destination.ToInt64() - (source.ToInt64() + 5);
        if (delta is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        patch[0] = 0xE9;
        BitConverter.GetBytes((int)delta).CopyTo(patch, 1);
        for (var i = 5; i < patch.Length; i++)
        {
            patch[i] = 0x90;
        }

        return true;
    }
}

internal sealed class X64CodeBuilder
{
    private readonly nint _baseAddress;
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _labels = [];
    private readonly List<(int OperandOffset, string Label)> _jumps = [];

    public X64CodeBuilder(nint baseAddress)
    {
        _baseAddress = baseAddress;
    }

    public void Label(string name)
    {
        _labels[name] = _bytes.Count;
    }

    public void Emit(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
    }

    public void PushRbx() => Emit(0x53);
    public void PopRbx() => Emit(0x5B);
    public void PushRax() => Emit(0x50);
    public void PopRax() => Emit(0x58);
    public void PushR10() => Emit(0x41, 0x52);
    public void PopR10() => Emit(0x41, 0x5A);
    public void PushFlags() => Emit(0x9C);
    public void PopFlags() => Emit(0x9D);

    public void MovR10Imm64(nint value)
    {
        Emit(0x49, 0xBA);
        EmitInt64(value.ToInt64());
    }

    public void MovPtrR10Disp8R15(byte displacement)
    {
        Emit(0x4D, 0x89, 0x7A, displacement);
    }

    public void MovPtrR10Disp8Rsi(byte displacement)
    {
        Emit(0x49, 0x89, 0x72, displacement);
    }

    public void CmpBytePtrR10Disp8(byte displacement, byte value)
    {
        Emit(0x41, 0x80, 0x7A, displacement, value);
    }

    public void MovEbxPtrR10Disp8(byte displacement)
    {
        Emit(0x41, 0x8B, 0x5A, displacement);
    }

    public void MovRbxPtrR10Disp8(byte displacement)
    {
        Emit(0x49, 0x8B, 0x5A, displacement);
    }

    public void CmpRbxR15()
    {
        Emit(0x4C, 0x39, 0xFB);
    }

    public void TestRbxRbx()
    {
        Emit(0x48, 0x85, 0xDB);
    }

    public void MovAlPtrR10Disp8(byte displacement)
    {
        Emit(0x41, 0x8A, 0x42, displacement);
    }

    public void MovPtrR10Disp8Al(byte displacement)
    {
        Emit(0x41, 0x88, 0x42, displacement);
    }

    public void MovAlPtrR15Disp32(int displacement)
    {
        Emit(0x41, 0x8A, 0x87);
        EmitInt32(displacement);
    }

    public void MovPtrRbxDisp32Al(int displacement)
    {
        Emit(0x88, 0x83);
        EmitInt32(displacement);
    }

    public void MovPtrR15Disp32Ebx(int displacement)
    {
        Emit(0x41, 0x89, 0x9F);
        EmitInt32(displacement);
    }

    public void MovPtrR15Disp32Eax(int displacement)
    {
        Emit(0x41, 0x89, 0x87);
        EmitInt32(displacement);
    }

    public void MovDwordPtrR15Disp32Imm32(int displacement, uint value)
    {
        Emit(0x41, 0xC7, 0x87);
        EmitInt32(displacement);
        EmitUInt32(value);
    }

    public void MovBytePtrR15Disp32Imm8(int displacement, byte value)
    {
        Emit(0x41, 0xC6, 0x87);
        EmitInt32(displacement);
        Emit(value);
    }

    public void MovBytePtrRsiDisp32Imm8(int displacement, byte value)
    {
        Emit(0xC6, 0x86);
        EmitInt32(displacement);
        Emit(value);
    }

    public void MovRaxPtrRsiDisp32(int displacement)
    {
        Emit(0x48, 0x8B, 0x86);
        EmitInt32(displacement);
    }

    public void MovPtrRsiDisp32Ebx(int displacement)
    {
        Emit(0x89, 0x9E);
        EmitInt32(displacement);
    }

    public void MovDwordPtrRsiDisp32Imm32(int displacement, uint value)
    {
        Emit(0xC7, 0x86);
        EmitInt32(displacement);
        EmitUInt32(value);
    }

    public void Je(string label) => Jcc(0x84, label);
    public void Jne(string label) => Jcc(0x85, label);

    public void Jmp(string label)
    {
        Emit(0xE9);
        _jumps.Add((_bytes.Count, label));
        EmitInt32(0);
    }

    public void JmpAbsolute(nint destination)
    {
        Emit(0xE9);
        var operandOffset = _bytes.Count;
        var nextInstruction = _baseAddress.ToInt64() + operandOffset + 4;
        var delta = destination.ToInt64() - nextInstruction;
        if (delta is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException("Jump target is out of range.");
        }

        EmitInt32((int)delta);
    }

    public byte[] ToArray()
    {
        var output = _bytes.ToArray();
        foreach (var jump in _jumps)
        {
            if (!_labels.TryGetValue(jump.Label, out var destinationOffset))
            {
                throw new InvalidOperationException($"Unknown label {jump.Label}");
            }

            var delta = destinationOffset - (jump.OperandOffset + 4);
            BitConverter.GetBytes(delta).CopyTo(output, jump.OperandOffset);
        }

        return output;
    }

    private void Jcc(byte opcode, string label)
    {
        Emit(0x0F, opcode);
        _jumps.Add((_bytes.Count, label));
        EmitInt32(0);
    }

    private void EmitInt32(int value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
    }

    private void EmitUInt32(uint value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
    }

    private void EmitInt64(long value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
    }
}
