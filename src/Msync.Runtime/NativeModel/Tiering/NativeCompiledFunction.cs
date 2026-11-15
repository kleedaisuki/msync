using System.Runtime.InteropServices;
using System.Diagnostics;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeCompiledFunction(NativeCodePage page, int bytes,
    NativeFastOperation[] operations, NativeTierLayout layout, NativeCodegenStatistics statistics) : IDisposable
{
    internal int Bytes { get; } = bytes;
    internal NativeFastOperation[] Operations { get; } = operations;
    internal NativeTierLayout Layout { get; } = layout;
    internal NativeCodegenStatistics Statistics { get; } = statistics;

    internal static NativeCompiledFunction? Compile(NativeImage image, NativeExecutionFunction function, NativeJitPolicy policy)
    {
        if (!NativeCodePage.IsSupported || !BitConverter.IsLittleEndian || function.Metadata.Length == 0) return null;
        long started = Stopwatch.GetTimestamp();
        NativeSiteProfile?[]? observations = function.ObservedProfiles;
        NativeFastOperation[] operations = function.Instructions.Select((instruction, pc) => Select(image, function, instruction, observations?[pc]?.Snapshot(), executableForm: function.NumberForm(pc))).ToArray();
        if (policy == NativeJitPolicy.Auto && (!NativeMachineCode.HasDirectPaths
            || !IsProfitableForAuto(function, operations))) return null;
        long selection = Stopwatch.GetElapsedTime(started).Ticks * 100;
        started = Stopwatch.GetTimestamp();
        NativeTierLayout layout = NativeTierLayout.Create(function, operations, image);
        long allocation = Stopwatch.GetElapsedTime(started).Ticks * 100;
        started = Stopwatch.GetTimestamp();
        byte[] bytes = NativeMachineCode.Emit(function, operations, RuntimeInformation.ProcessArchitecture,
            OperatingSystem.IsWindows(), NativeInterpreter.TierBridgeAddress, NativeInterpreter.TierNumberAddress,
            NativeInterpreter.TierBoolAddress, image.Program.Constants, out NativeInitializationElisions initializationElisions, layout, NativeInterpreter.TierCommitAddress);
        long emission = Stopwatch.GetElapsedTime(started).Ticks * 100;
        started = Stopwatch.GetTimestamp();
        NativeCodePage? page = NativeCodePage.Create(bytes);
        NativeCodegenStatistics statistics = new(selection, allocation, emission, Stopwatch.GetElapsedTime(started).Ticks * 100,
            layout.Regions.Length, layout.Regions.Sum(region => region.Values.Length), layout.Regions.Sum(region => region.Spills),
            layout.Regions.Sum(region => region.Operations.Length - region.Outputs.Length))
        {
            InitializationChecksElided = initializationElisions.Checks,
            InitializationWritesElided = initializationElisions.Writes,
            UnsignedValues = layout.Regions.Sum(region => region.Values.Count(value => value.Unsigned)),
            UnsignedOperations = layout.Regions.Sum(region => region.Operations.Count(operation => operation.Form != NativeNumberForm.Float64))
        };
        return page is null ? null : new(page, bytes.Length, operations, layout, statistics);
    }

    private static bool IsProfitableForAuto(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        NativeExecutionInstruction[] instructions = function.Instructions;
        int directTotal = 0;
        int directCompute = 0;
        int transitions = 0;
        bool inDirect = false;
        bool hasLoop = false;
        for (int pc = 0; pc < operations.Length; pc += Math.Max(1, instructions[pc].Span))
        {
            NativeFastOperation op = operations[pc];
            if (op == NativeFastOperation.Nop) continue;
            if (op == NativeFastOperation.Bridge)
            {
                if (inDirect) { transitions++; inDirect = false; }
                NativeExecutionInstruction insn = instructions[pc];
                if (insn.Op == NativeOp.Jump && insn.A <= pc) hasLoop = true;
                else if (insn.Op == NativeOp.Branch && (insn.B <= pc || insn.C <= pc)) hasLoop = true;
                continue;
            }
            inDirect = true;
            directTotal++;
            if (op is NativeFastOperation.Branch or NativeFastOperation.IsNull
                or NativeFastOperation.NumberNegate or NativeFastOperation.BoolNot
                or NativeFastOperation.BoolEqual or NativeFastOperation.BoolNotEqual
                or >= NativeFastOperation.NumberAdd)
            {
                directCompute++;
            }
            if (op == NativeFastOperation.Branch)
            {
                NativeExecutionInstruction insn = instructions[pc];
                if (insn.B <= pc || insn.C <= pc) hasLoop = true;
            }
            else if (op == NativeFastOperation.Jump && instructions[pc].A <= pc)
            {
                hasLoop = true;
            }
        }
        if (directCompute == 0 || directTotal < 2) return false;
        return hasLoop
            ? directTotal >= transitions
            : directCompute >= 2 && directTotal >= 4 && directTotal >= transitions * 2;
    }

    private static NativeFastOperation Select(NativeImage image, NativeExecutionFunction executable, NativeExecutionInstruction instruction,
        NativeSiteShape? observed, NativeNumberForm executableForm)
    {
        NativeFunctionCode function = executable.Definition;
        bool Kind(int slot, TypeKind kind) => slot >= 0 && image.Program.Types[function.Slots[slot].Type].Pointee < 0
            && (image.Program.Types[function.Slots[slot].Type].Kind == kind);
        if (instruction.Handler == NativeInterpreter.NopHandler) return NativeFastOperation.Nop;
        if (instruction.Op == NativeOp.Jump) return NativeFastOperation.Jump;
        if (instruction.Op == NativeOp.Branch) return NativeFastOperation.Branch;
        if (instruction.Op == NativeOp.Return && instruction.A >= 0)
        {
            if (Kind(instruction.A, TypeKind.Number)) return NativeFastOperation.NumberReturn;
            if (Kind(instruction.A, TypeKind.Bool)) return NativeFastOperation.BoolReturn;
        }
        if (instruction.Op == NativeOp.Constant)
            return image.Program.Constants[instruction.A].Kind switch
            {
                NativeKind.Number => NativeFastOperation.NumberConstant,
                NativeKind.Bool => NativeFastOperation.BoolConstant,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op == NativeOp.Copy && Kind(instruction.A, TypeKind.Number)) return NativeFastOperation.NumberCopy;
        if (instruction.Op == NativeOp.Copy && Kind(instruction.A, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        if (instruction.Op == NativeOp.Move)
        {
            if (Kind(instruction.A, TypeKind.Number) && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberMove;
            if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolMove;
        }
        if (instruction.Op == NativeOp.Store && instruction.Handler == NativeInterpreter.FrameStoreHandler)
        {
            if (Kind(instruction.A, TypeKind.Number) && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberCopy;
            if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        }
        if (instruction.Op == NativeOp.Narrow && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberCopy;
        if (instruction.Op == NativeOp.Narrow && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        if (instruction.Op == NativeOp.Unary)
            return (NativeUnary)instruction.Immediate switch
            {
                NativeUnary.Negate when Kind(instruction.A, TypeKind.Number) => NativeFastOperation.NumberNegate,
                NativeUnary.Not when Kind(instruction.A, TypeKind.Bool) => NativeFastOperation.BoolNot,
                NativeUnary.IsNull when instruction.A >= 0 && image.Program.Types[function.Slots[instruction.A].Type].Pointee < 0 => NativeFastOperation.IsNull,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op != NativeOp.Binary) return NativeFastOperation.Bridge;
        if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.B, TypeKind.Bool)
            || observed is { First: NativeKind.Bool, Second: NativeKind.Bool })
        {
            return (NativeBinary)instruction.Immediate switch
            {
                NativeBinary.Equal => NativeFastOperation.BoolEqual,
                NativeBinary.NotEqual => NativeFastOperation.BoolNotEqual,
                _ => NativeFastOperation.Bridge
            };
        }
        if (!(Kind(instruction.A, TypeKind.Number) && Kind(instruction.B, TypeKind.Number)
            || observed is { First: NativeKind.Number, Second: NativeKind.Number })) return NativeFastOperation.Bridge;
        return (NativeBinary)instruction.Immediate switch
        {
            NativeBinary.Add => NativeFastOperation.NumberAdd,
            NativeBinary.Subtract => NativeFastOperation.NumberSubtract,
            NativeBinary.Multiply => NativeFastOperation.NumberMultiply,
            NativeBinary.Divide => NativeFastOperation.NumberDivide,
            NativeBinary.Remainder when executableForm.HasFlag(NativeNumberForm.UnsignedResult) => NativeFastOperation.NumberRemainder,
            NativeBinary.Equal => NativeFastOperation.NumberEqual,
            NativeBinary.NotEqual => NativeFastOperation.NumberNotEqual,
            NativeBinary.Less => NativeFastOperation.NumberLess,
            NativeBinary.LessEqual => NativeFastOperation.NumberLessEqual,
            NativeBinary.Greater => NativeFastOperation.NumberGreater,
            NativeBinary.GreaterEqual => NativeFastOperation.NumberGreaterEqual,
            _ => NativeFastOperation.Bridge
        };
    }

    internal int Run(nint context, int pc) => page.RunTier(context, pc);
    public void Dispose() => page.Dispose();
}

internal readonly record struct NativeCodegenStatistics(long SelectionNanoseconds, long AllocationNanoseconds,
    long EmissionNanoseconds, long PublicationNanoseconds, int Regions, int Values, int Spills, int EliminatedStores)
{
    internal int UnsignedValues { get; init; }
    internal int UnsignedOperations { get; init; }
    internal int InitializationChecksElided { get; init; }
    internal int InitializationWritesElided { get; init; }
}
