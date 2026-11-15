using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private readonly record struct MachineState(LlvmOpcode Opcode, NativeType Type);
    private readonly record struct MachineCompareState(LlvmCmpPredicate Predicate);
    private readonly record struct MachineIntrinsicState(NativeMachineIntrinsic Operation, NativeType Type);

    private NativeDatum MachineBinaryDatum(NativeFrame frame, NativeInstruction instruction,
        RuntimeProfile.NativeFrameCounters counters)
    {
        NativeDatum left = frame.GetDatum(instruction.A), right = frame.GetDatum(instruction.B);
        NativeType type = image.Type(frame.Type(instruction.Result));
        counters.Machine(instruction.Op, !left.IsReady || !right.IsReady, IsWide(type),
            (left.Future is null ? 0 : 1) + (right.Future is null ? 0 : 1));
        return Track(NativeDatum.CombineMachine(left, right, new MachineState((LlvmOpcode)instruction.Immediate, type),
            static (a, b, state) => NativeMachineOperations.Binary(state.Opcode, a, b, state.Type), type, instruction.Source));
    }

    private NativeDatum MachineCompareDatum(NativeFrame frame, NativeInstruction instruction,
        RuntimeProfile.NativeFrameCounters counters)
    {
        NativeDatum left = frame.GetDatum(instruction.A), right = frame.GetDatum(instruction.B);
        NativeType type = image.Type(frame.Type(instruction.Result));
        counters.Machine(instruction.Op, !left.IsReady || !right.IsReady, IsWide(image.Type(frame.Type(instruction.A))),
            (left.Future is null ? 0 : 1) + (right.Future is null ? 0 : 1));
        return Track(NativeDatum.CombineMachine(left, right, new MachineCompareState((LlvmCmpPredicate)instruction.Immediate),
            static (a, b, state) => NativeMachineOperations.Compare(state.Predicate, a, b), type, instruction.Source));
    }

    private NativeDatum MachineUnaryDatum(NativeFrame frame, NativeInstruction instruction,
        RuntimeProfile.NativeFrameCounters counters)
    {
        NativeDatum value = frame.GetDatum(instruction.A);
        NativeType type = image.Type(frame.Type(instruction.Result));
        counters.Machine(instruction.Op, !value.IsReady, IsWide(type), value.Future is null ? 0 : 1);
        return Track(value.MapMachine(new MachineState((LlvmOpcode)instruction.Immediate, type),
            static (a, state) => NativeMachineOperations.Unary(state.Opcode, a, state.Type), type, instruction.Source));
    }

    private NativeDatum MachineCastDatum(NativeFrame frame, NativeInstruction instruction,
        RuntimeProfile.NativeFrameCounters counters)
    {
        NativeDatum value = frame.GetDatum(instruction.A);
        NativeType type = image.Type(instruction.Type);
        counters.Machine(instruction.Op, !value.IsReady, IsWide(type) || IsWide(image.Type(frame.Type(instruction.A))),
            value.Future is null ? 0 : 1);
        return Track(value.MapMachine(new MachineState((LlvmOpcode)instruction.Immediate, type),
            static (a, state) => NativeMachineOperations.Cast(state.Opcode, a, state.Type), type, instruction.Source));
    }

    private NativeDatum MachineIntrinsicDatum(NativeFrame frame, NativeInstruction instruction,
        RuntimeProfile.NativeFrameCounters counters)
    {
        IReadOnlyList<int> inputs = instruction.Inputs!;
        var operation = (NativeMachineIntrinsic)instruction.Immediate;
        NativeDatum first = frame.GetDatum(inputs[0]);
        NativeDatum second = inputs.Count > 1 ? frame.GetDatum(inputs[1]) : default;
        NativeDatum third = inputs.Count > 2 ? frame.GetDatum(inputs[2]) : default;
        NativeType type = image.Type(frame.Type(instruction.Result));
        var state = new MachineIntrinsicState(operation, type);
        bool pending = !first.IsReady || !second.IsReady || !third.IsReady;
        counters.Machine(instruction.Op, pending, IsWide(image.Type(frame.Type(inputs[0]))),
            (first.Future is null ? 0 : 1) + (second.Future is null ? 0 : 1) + (third.Future is null ? 0 : 1));
        if (inputs.Count == 1)
            return Track(first.MapMachine(state,
                static (a, s) => NativeMachineOperations.Intrinsic(s.Operation, a, default, default, s.Type), type, instruction.Source));
        if (inputs.Count == 2)
            return Track(NativeDatum.CombineMachine(first, second, state,
                static (a, b, s) => NativeMachineOperations.Intrinsic(s.Operation, a, b, default, s.Type), type, instruction.Source));
        if (!pending) return new(NativeMachineOperations.Intrinsic(operation, first.Value, second.Value, third.Value, type));
        List<NativeFuture> dependencies = [];
        if (first.Future is { } aFuture) dependencies.Add(aFuture);
        if (second.Future is { } bFuture) dependencies.Add(bFuture);
        if (third.Future is { } cFuture) dependencies.Add(cFuture);
        return Track(new(new NativeFuture(MachineTernaryAsync(first, second, third, state), dependencies,
            source: instruction.Source, logicalType: type.IsMachine ? null : TypeRef.Bool, physicalType: type)));
    }

    private static async Task<NativeValue> MachineTernaryAsync(NativeDatum first, NativeDatum second,
        NativeDatum third, MachineIntrinsicState state) => NativeMachineOperations.Intrinsic(state.Operation,
            await first.ResolveAsync(CancellationToken.None).ConfigureAwait(false),
            await second.ResolveAsync(CancellationToken.None).ConfigureAwait(false),
            await third.ResolveAsync(CancellationToken.None).ConfigureAwait(false), state.Type);

    private static bool IsWide(NativeType type) => type.Machine is { BitWidth: > 64 };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteMachineBinary(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        activation.Frame.SetDatum(instruction.Result,
            owner.MachineBinaryDatum(activation.Frame, activation.Execution.Metadata[pc], activation.Work.Counters));
        return new(pc + instruction.Span);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteMachineCompare(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        activation.Frame.SetDatum(instruction.Result,
            owner.MachineCompareDatum(activation.Frame, activation.Execution.Metadata[pc], activation.Work.Counters));
        return new(pc + instruction.Span);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteMachineUnary(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        activation.Frame.SetDatum(instruction.Result,
            owner.MachineUnaryDatum(activation.Frame, activation.Execution.Metadata[pc], activation.Work.Counters));
        return new(pc + instruction.Span);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteMachineCast(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        activation.Frame.SetDatum(instruction.Result,
            owner.MachineCastDatum(activation.Frame, activation.Execution.Metadata[pc], activation.Work.Counters));
        return new(pc + instruction.Span);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteMachineIntrinsic(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        activation.Frame.SetDatum(instruction.Result,
            owner.MachineIntrinsicDatum(activation.Frame, activation.Execution.Metadata[pc], activation.Work.Counters));
        return new(pc + instruction.Span);
    }
}
