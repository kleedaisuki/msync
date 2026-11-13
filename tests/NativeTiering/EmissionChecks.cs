using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class EmissionChecks
{
    internal static int Run()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<NativeExecutionInstruction>())
            throw new InvalidOperationException("Linked execution instructions must not carry managed roots.");
        if (IntPtr.Size == 8 && Marshal.SizeOf<NativeExecutionInstruction>() != 40)
            throw new InvalidOperationException("Linked instructions retained redundant metadata or padding.");
        if (Marshal.SizeOf<NativeTierContext>() != 40
            || Marshal.SizeOf<NativeTierSlot>() != 32)
            throw new InvalidOperationException("Native descriptor ABI disagrees with the machine emitter.");
        int checks = 1;
        foreach (NativeOp op in Enum.GetValues<NativeOp>())
        {
            if (NativeInterpreter.ExecutionHandlerAddress(op) == 0)
                throw new InvalidOperationException($"Native operation {op} has no linked runtime lowering.");
            checks++;
        }
        NativeProgram program = new([new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 2)],
            [new("emitter", [new(0), new(0), new(0), new(1)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
                 new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add),
                 new(NativeOp.Unary, 2, 2, Immediate: (long)NativeUnary.Negate),
                 new(NativeOp.Binary, 3, 2, 1, Immediate: (long)NativeBinary.Less),
                 new(NativeOp.Unary, 3, 3, Immediate: (long)NativeUnary.Not),
                 new(NativeOp.Branch, A: 3, B: 7, C: 8), new(NativeOp.Jump, A: 9),
                 new(NativeOp.Copy, 2, 1), new(NativeOp.Return, A: 2)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(program);
        NativeExecutionFunction function = new(new(program, new()), 0, new(NativeJitPolicy.Force));
        NativeFastOperation[] operations = [NativeFastOperation.NumberConstant, NativeFastOperation.NumberConstant,
            NativeFastOperation.NumberAdd, NativeFastOperation.NumberNegate, NativeFastOperation.NumberLess,
            NativeFastOperation.BoolNot, NativeFastOperation.Branch, NativeFastOperation.Jump,
            NativeFastOperation.NumberCopy, NativeFastOperation.Bridge];
        byte[] arm = NativeMachineCode.Emit(function, operations, Architecture.Arm64, false,
            (nint)0x11111111, (nint)0x22222222, (nint)0x33333333, program.Constants);
        byte[] windows = NativeMachineCode.Emit(function, operations, Architecture.X64, true,
            (nint)0x11111111, (nint)0x22222222, (nint)0x33333333, program.Constants);
        if ((arm.Length & 3) != 0 || BitConverter.ToUInt32(arm) != 0xa9bf7bf3)
            throw new InvalidOperationException("ARM64 native code did not preserve its ABI frame.");
        if (!windows.AsSpan(0, 6).SequenceEqual(new byte[] { 0x41, 0x54, 0x48, 0x83, 0xec, 0x20 }))
            throw new InvalidOperationException("Windows x64 native entry is missing its shadow space.");
        Directory.CreateDirectory(".temp/native-tiering");
        File.WriteAllBytes(".temp/native-tiering/arm64-native.bin", arm);
        File.WriteAllBytes(".temp/native-tiering/windows-x64-native.bin", windows);
        return checks + 2;
    }
}
