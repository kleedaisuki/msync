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
        if (IntPtr.Size == 8 && (Marshal.SizeOf<NativeTierContext>() != NativeTierLayout.HeaderBytes
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.Raw)) != NativeTierLayout.RawOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.Initialization)) != NativeTierLayout.InitializationOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.GuestEdges)) != NativeTierLayout.GuestEdgesOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.ChildContext)) != NativeTierLayout.ChildContextOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.ChildEntry)) != NativeTierLayout.ChildEntryOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedResultBits)) != NativeTierLayout.FixedResultBitsOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.StackCursor)) != NativeTierLayout.StackCursorOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedReturnTop)) != NativeTierLayout.FixedReturnTopOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedReturnState)) != NativeTierLayout.FixedReturnStateOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedCallStart)) != NativeTierLayout.FixedCallStartOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedPending)) != NativeTierLayout.FixedPendingOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedCallableInit)) != NativeTierLayout.FixedCallableInitOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedCallableEdges)) != NativeTierLayout.FixedCallableEdgesOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedEnvironment)) != NativeTierLayout.FixedEnvironmentOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedRepeatPc)) != NativeTierLayout.FixedRepeatPcOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedRepeats)) != NativeTierLayout.FixedRepeatsOffset
            || Marshal.OffsetOf<NativeTierContext>(nameof(NativeTierContext.FixedRepeatFuel)) != NativeTierLayout.FixedRepeatFuelOffset
            || Marshal.OffsetOf<NativeStackCursor>(nameof(NativeStackCursor.Frame)) != NativeStackCursor.X64FrameOffset
            || Marshal.OffsetOf<NativeStackCursor>(nameof(NativeStackCursor.Top)) != NativeStackCursor.X64TopOffset
            || Marshal.OffsetOf<NativeStackCursor>(nameof(NativeStackCursor.Data)) != NativeStackCursor.X64DataOffset
            || Marshal.OffsetOf<NativeStackCursor>(nameof(NativeStackCursor.Initialization)) != NativeStackCursor.X64InitializationOffset
            || Marshal.OffsetOf<NativeStackCursor>(nameof(NativeStackCursor.References)) != NativeStackCursor.X64ReferencesOffset
            || Marshal.SizeOf<NativeOffset>() != sizeof(long)
            || Marshal.SizeOf<NativeStackHeader>() != NativeStackHeader.X64Bytes
            || Marshal.OffsetOf<NativeStackHeader>(nameof(NativeStackHeader.Data)) != NativeStackHeader.X64DataOffset
            || Marshal.OffsetOf<NativeStackHeader>(nameof(NativeStackHeader.Initialization)) != NativeStackHeader.X64InitializationOffset
            || Marshal.OffsetOf<NativeStackHeader>(nameof(NativeStackHeader.References)) != NativeStackHeader.X64ReferencesOffset
            || Marshal.OffsetOf<NativeStackHeader>(nameof(NativeStackHeader.Previous)) != NativeStackHeader.X64PreviousOffset
            || Marshal.OffsetOf<NativeStackHeader>(nameof(NativeStackHeader.Arena)) != NativeStackHeader.X64ArenaOffset
            || Marshal.OffsetOf<NativeStackHeader>(nameof(NativeStackHeader.ReferenceCount)) != NativeStackHeader.X64ReferenceCountOffset
            || Marshal.SizeOf<NativeTierSlot>() != 32))
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
                 new(NativeOp.Copy, 2, 1), new(NativeOp.Return, A: 2)], new([], 0, []))
                { Storage = new(new NativeSlotStorage[4], new NativeInstructionStorage[10]) }],
            [new(TypeKind.Number), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(program);
        NativeProgram compiled = NativeBackendCompiler.Compile(program, new());
        using NativeExecutionFunction function = new(new(compiled, new()), 0, new(NativeJitPolicy.Force));
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
        if (!windows.AsSpan(0, 2).SequenceEqual(new byte[] { 0x41, 0x54 })
            || !windows.AsSpan(7, 3).SequenceEqual(new byte[] { 0x48, 0x81, 0xec })
            || BitConverter.ToInt32(windows, 10) < 32 || BitConverter.ToInt32(windows, 10) % 16 != 0)
            throw new InvalidOperationException("Windows x64 native entry is missing its shadow space.");
        Directory.CreateDirectory(".temp/native-tiering");
        File.WriteAllBytes(".temp/native-tiering/arm64-native.bin", arm);
        File.WriteAllBytes(".temp/native-tiering/windows-x64-native.bin", windows);
        return checks + 2;
    }
}
