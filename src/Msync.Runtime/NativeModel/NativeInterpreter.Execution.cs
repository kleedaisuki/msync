using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal static unsafe nint FrameStoreHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteFrameStore;
    internal static unsafe nint OffsetLoadHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteOffsetLoad;
    internal static unsafe nint ProjectLoadHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteProjectLoad;
    internal static unsafe nint OffsetStoreHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteOffsetStore;
    internal static unsafe nint ProjectStoreHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteProjectStore;
    internal static unsafe nint OffsetBindHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteOffsetBind;
    internal static unsafe nint NopHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteNop;

    private static ValueTask<NativeStep> ExecuteNop(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token) =>
        new(new NativeStep(pc + instruction.Span));

    private static ValueTask<NativeStep> StoreToAddress(NativeInterpreter owner, NativeFrame frame, int sourceSlot, NativeAddress target, int nextPc)
    {
        if (!owner.memory.TryCopyReady(frame.Location(sourceSlot), target))
            owner.memory.Write(target, owner.CoerceStore(frame.GetDatum(sourceSlot), target.Type));
        return new(new NativeStep(nextPc));
    }

    private static ValueTask<NativeStep> ExecuteFrameStore(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token) =>
        StoreToAddress(owner, activation.Frame, instruction.A, activation.Frame.Location(instruction.Result), pc + instruction.Span);

    private static ValueTask<NativeStep> ExecuteOffsetLoad(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        owner.memory.Copy(activation.Execution.Operands[pc].AddressField(frame.Pointer(instruction.A)), frame.Location(instruction.Result));
        return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecuteProjectLoad(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        owner.memory.Copy(activation.Execution.Operands[pc].Project(owner.image, frame.Pointer(instruction.A)), frame.Location(instruction.Result));
        return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecuteOffsetStore(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        return StoreToAddress(owner, frame, instruction.B, activation.Execution.Operands[pc].AddressField(frame.Pointer(instruction.A)), pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteProjectStore(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        return StoreToAddress(owner, frame, instruction.B, activation.Execution.Operands[pc].Project(owner.image, frame.Pointer(instruction.A)), pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteOffsetBind(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.Bind(instruction.A, activation.Execution.Operands[pc].AddressField(frame.Pointer(instruction.B)));
        return new(new NativeStep(pc + instruction.Span));
    }

    internal static unsafe nint ExecutionHandlerAddress(NativeOp op, bool number = false) => op switch
    {
        NativeOp.Constant => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteConstant,
        NativeOp.Copy => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteCopy,
        NativeOp.Move => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteMove,
        NativeOp.Drop => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteDrop,
        NativeOp.FrameAddress => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteFrameAddress,
        NativeOp.BindLocation => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteBindLocation,
        NativeOp.AddressOffset => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteAddressOffset,
        NativeOp.EnvironmentAddress => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteEnvironmentAddress,
        NativeOp.ProjectAddress => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteProjectAddress,
        NativeOp.Load => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteLoad,
        NativeOp.Store => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteStore,
        NativeOp.Unary => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteUnary,
        NativeOp.Binary when number => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteNumberBinary,
        NativeOp.Binary => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteBinary,
        NativeOp.Convert => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteConvert,
        NativeOp.Narrow => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteNarrow,
        NativeOp.MakeType => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteMakeType,
        NativeOp.Transfer => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteTransfer,
        NativeOp.PlanResult => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecutePlanResult,
        NativeOp.TestStatic => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteTestStatic,
        NativeOp.StaticAddress => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteStaticAddress,
        NativeOp.Return => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteReturn,
        NativeOp.Nop => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteNop,
        NativeOp.Allocate => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteAllocate,
        NativeOp.IndexAddress => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteIndexAddress,
        NativeOp.CopyMemory => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteCopyMemory,
        NativeOp.ClearMemory => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteClearMemory,
        NativeOp.RangeFromMemory => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteRangeFromMemory,
        NativeOp.Jump => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteJump,
        NativeOp.Branch => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteBranch,
        NativeOp.MakeFunction => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteMakeFunction,
        NativeOp.AdaptFunction => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteAdaptFunction,
        NativeOp.Call => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteCall,
        NativeOp.CallNative => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteCallNative,
        NativeOp.MakeRange => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteMakeRange,
        NativeOp.MakeLazyRange => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteMakeLazyRange,
        NativeOp.DeferRange => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteDeferRange,
        NativeOp.OpenIterator => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteOpenIterator,
        NativeOp.NextIterator => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteNextIterator,
        NativeOp.CloseIterator => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteCloseIterator,
        NativeOp.ProcessCall => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteProcessCall,
        NativeOp.SelectPath => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteSelectPath,
        NativeOp.Barrier => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteBarrier,
        NativeOp.Acquire => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteAcquire,
        NativeOp.ExecutePlan => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteExecutePlan,
        NativeOp.PublishStatic => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecutePublishStatic,
        NativeOp.Unreachable => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)&ExecuteUnreachable,
        _ => throw new InvalidOperationException($"Unknown native instruction {op}.")
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteConstant(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.Set(instruction.Result, owner.image.Constant(instruction.A, owner.deferredContext.Value?.CaptureDirectory())); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteCopy(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        if (activation.Execution.Operands[pc].Type is { } type) frame.Copy(type, instruction.A, instruction.Result);
        else frame.Copy(instruction.A, instruction.Result);
        return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteMove(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.Copy(instruction.A, instruction.Result); if (instruction.A != instruction.Result) frame.Drop(instruction.A); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteDrop(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.Drop(instruction.A); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteFrameAddress(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetPointer(instruction.Result, frame.Location(instruction.A)); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteBindLocation(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.Bind(instruction.A, frame.Pointer(instruction.B)); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteAddressOffset(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetPointer(instruction.Result, activation.Execution.Operands[pc].AddressField(frame.Pointer(instruction.A)));
        return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteEnvironmentAddress(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetPointer(instruction.Result, owner.EnvironmentAddress(frame.GetDatum(instruction.A).Value.AsFunction(), activation.Execution.Metadata[pc]));
        return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteProjectAddress(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetPointer(instruction.Result, activation.Execution.Operands[pc].Project(owner.image, frame.Pointer(instruction.A))); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteLoad(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        owner.memory.Copy(frame.Pointer(instruction.A), frame.Location(instruction.Result)); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteStore(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        NativeAddress target = frame.Pointer(instruction.A);
        if (!owner.memory.TryCopyReady(frame.Location(instruction.B), target))
            owner.memory.Write(target, owner.CoerceStore(frame.GetDatum(instruction.B), target.Type));
        return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteUnary(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetDatum(instruction.Result, owner.Unary(frame, activation.Execution.Metadata[pc])); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteBinary(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        NativeDatum left = frame.GetDatum(instruction.A), right = frame.GetDatum(instruction.B);
        NativeBinary operation = (NativeBinary)instruction.Immediate;
        NativeRuntimeContext? origin = operation is NativeBinary.PathJoin or NativeBinary.Qualify ? owner.context.CaptureDirectory() : null;
        frame.SetDatum(instruction.Result, left.IsReady && right.IsReady
            ? new(BinaryValue(operation, left.Value, right.Value, origin))
            : owner.PendingBinary(left, right, activation.Execution.Metadata[pc], origin, owner.image.SourceType(frame.Type(instruction.Result))));
        return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecuteNumberBinary(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        NativeAddress first = frame.Location(instruction.A), second = frame.Location(instruction.B);
        double left = owner.memory.TryPending(first, out NativeFuture? pendingLeft) ? 0 : NativeStorage.LoadNumber(first);
        double right = owner.memory.TryPending(second, out NativeFuture? pendingRight) ? 0 : NativeStorage.LoadNumber(second);
        NativeBinary operation = (NativeBinary)instruction.Immediate;
        if (pendingLeft is not null || pendingRight is not null)
            frame.SetDatum(instruction.Result, owner.PendingBinary(
                pendingLeft is null ? new(NativeValue.FromNumber(left)) : new(pendingLeft),
                pendingRight is null ? new(NativeValue.FromNumber(right)) : new(pendingRight),
                activation.Execution.Metadata[pc], null, owner.image.SourceType(frame.Type(instruction.Result))));
        else
        {
            NativeAddress target = frame.Location(instruction.Result);
            owner.memory.RemovePending(target);
            if (operation >= NativeBinary.Equal) NativeStorage.StoreBool(target, NumberSemantics.Compare(operation, NumberSemantics.Compare(left, right)));
            else NativeStorage.StoreNumber(target, NumberSemantics.Arithmetic(operation, left, right));
        }
        return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteConvert(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetDatum(instruction.Result, owner.ConvertDatum(frame.GetDatum(instruction.A), activation.Execution.Metadata[pc])); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteNarrow(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetDatum(instruction.Result, owner.NarrowDatum(frame.GetDatum(instruction.A), activation.Execution.Metadata[pc], activation.Execution.Operands[pc].Type!,
                    activation.Function.Name, pc)); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteMakeType(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetDatum(instruction.Result, owner.MakeTypeDatum(frame, activation.Execution.Metadata[pc])); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteTransfer(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetDatum(instruction.Result, owner.ScheduleTransfer(frame.GetDatum(instruction.A), frame.GetDatum(instruction.B), activation.Execution.Metadata[pc].Source, activation.Work, token, sourceKind: owner.image.Program.Types[frame.Type(instruction.A)].Kind)); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecutePlanResult(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.SetDatum(instruction.Result, owner.PlanResult(activation, activation.Execution.Metadata[pc])); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteTestStatic(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        frame.Set(instruction.Result, NativeValue.FromBool(Volatile.Read(ref owner.statics[instruction.A].Ready) != 0)); return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteStaticAddress(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        NativeStatic declaration = owner.image.Program.Statics[instruction.A];
        StaticState state = owner.statics[instruction.A];
        NativeAllocation? stored = Volatile.Read(ref state.Allocation);
        if (stored is null)
        {
            NativeAllocation allocated = new(owner.image.Layout(declaration.Layout), declaration.Layout)
            { AllocationSite = declaration.Declaration };
            stored = Interlocked.CompareExchange(ref state.Allocation, allocated, null);
            if (stored is null) activation.Work.Counters.Allocation(allocated);
            stored ??= allocated;
        }
        frame.SetPointer(instruction.Result, stored.Address);
        return new(new NativeStep(pc + instruction.Span));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<NativeStep> ExecuteReturn(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        activation.Result = instruction.A < 0 ? default : frame.GetDatum(instruction.A);
        return new(new NativeStep(pc + 1, true));
    }

    private static ValueTask<NativeStep> ExecuteAllocate(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        if (instruction.C >= 0) return ExecuteAllocateAsync(owner, activation, instruction, pc, token);
        NativeLayout layout = activation.Execution.Operands[pc].Layout!;
        if (activation.Frame.TryAllocate(pc, out NativeAddress placed))
        {
            activation.Work.Counters.Allocation(layout, 1, framePlaced: true);
            activation.Frame.SetPointer(instruction.Result, placed);
        }
        else
        {
            NativeAllocation allocation = new(layout, instruction.A, 1) { AllocationSite = Math.Max(0, instruction.B) };
            activation.Work.Counters.Allocation(allocation);
            activation.Frame.SetPointer(instruction.Result, allocation.Address);
        }
        return new(new NativeStep(pc + instruction.Span));
    }

    private static async ValueTask<NativeStep> ExecuteAllocateAsync(NativeInterpreter owner, Activation activation, NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        int count = instruction.C < 0 ? 1 : Count(await activation.Frame.GetDatum(instruction.C).ResolveAsync(token));
        NativeLayout layout = activation.Execution.Operands[pc].Layout!;
        if (activation.Frame.TryAllocate(pc, out NativeAddress placed))
        {
            activation.Work.Counters.Allocation(layout, count, framePlaced: true);
            activation.Frame.SetPointer(instruction.Result, placed);
        }
        else
        {
            NativeAllocation allocation = new(layout, instruction.A, count) { AllocationSite = Math.Max(0, instruction.B) };
            activation.Work.Counters.Allocation(allocation);
            activation.Frame.SetPointer(instruction.Result, allocation.Address);
        }
        return new(pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteIndexAddress(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeDatum indexDatum = activation.Frame.GetDatum(instruction.B);
        if (!indexDatum.IsReady) return ExecuteIndexAddressAsync(owner, activation, instruction, pc, token);
        int index = Count(indexDatum.Value);
        activation.Frame.SetPointer(instruction.Result, owner.image.IndexAddress(activation.Frame.Pointer(instruction.A), index,
            activation.Execution.Operands[pc].Layout!));
        return new(new NativeStep(pc + instruction.Span));
    }

    private static async ValueTask<NativeStep> ExecuteIndexAddressAsync(NativeInterpreter owner, Activation activation, NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        int index = Count(await activation.Frame.GetDatum(instruction.B).ResolveAsync(token));
        activation.Frame.SetPointer(instruction.Result, owner.image.IndexAddress(activation.Frame.Pointer(instruction.A), index,
            activation.Execution.Operands[pc].Layout!));
        return new(pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteCopyMemory(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        if (instruction.C >= 0) return ExecuteCopyMemoryAsync(owner, activation, instruction, pc, token);
        NativeLayout layout = activation.Execution.Operands[pc].Layout!;
        NativeAddress target = activation.Frame.Pointer(instruction.A);
        owner.image.CheckRegionBounds(target, layout, 1);
        NativeAddress source = activation.Frame.Pointer(instruction.B);
        owner.image.CheckRegionBounds(source, layout, 1);
        owner.memory.CopyRegionTrusted(source, target, layout, 1);
        return new(new NativeStep(pc + instruction.Span));
    }

    private static async ValueTask<NativeStep> ExecuteCopyMemoryAsync(NativeInterpreter owner, Activation activation, NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        int count = instruction.C < 0 ? 1 : Count(await activation.Frame.GetDatum(instruction.C).ResolveAsync(token));
        NativeLayout layout = activation.Execution.Operands[pc].Layout!;
        NativeAddress target = activation.Frame.Pointer(instruction.A);
        owner.image.CheckRegionBounds(target, layout, count);
        NativeAddress source = activation.Frame.Pointer(instruction.B);
        owner.image.CheckRegionBounds(source, layout, count);
        owner.memory.CopyRegionTrusted(source, target, layout, count);
        return new(pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteClearMemory(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        if (instruction.C >= 0) return ExecuteClearMemoryAsync(owner, activation, instruction, pc, token);
        NativeLayout layout = activation.Execution.Operands[pc].Layout!;
        NativeAddress target = activation.Frame.Pointer(instruction.A);
        owner.image.CheckRegionBounds(target, layout, 1);
        owner.memory.ClearRegionTrusted(target, layout, 1);
        return new(new NativeStep(pc + instruction.Span));
    }

    private static async ValueTask<NativeStep> ExecuteClearMemoryAsync(NativeInterpreter owner, Activation activation, NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        int count = instruction.C < 0 ? 1 : Count(await activation.Frame.GetDatum(instruction.C).ResolveAsync(token));
        NativeLayout layout = activation.Execution.Operands[pc].Layout!;
        NativeAddress target = activation.Frame.Pointer(instruction.A);
        owner.image.CheckRegionBounds(target, layout, count);
        owner.memory.ClearRegionTrusted(target, layout, count);
        return new(pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteRangeFromMemory(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token) =>
        ExecuteRangeFromMemoryAsync(owner, activation, instruction, pc, token);

    private static async ValueTask<NativeStep> ExecuteRangeFromMemoryAsync(NativeInterpreter owner, Activation activation, NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        int count = instruction.B < 0 ? 1 : Count(await activation.Frame.GetDatum(instruction.B).ResolveAsync(token));
        TypeRef? logicalType = activation.Execution.Metadata[pc].Inputs is { Count: > 0 } inputs
            ? (await activation.Frame.GetDatum(inputs[0]).ResolveAsync(token)).AsType() : null;
        owner.MakeMemoryRange(activation.Frame, activation.Execution.Metadata[pc], count, logicalType);
        return new(pc + instruction.Span);
    }

    private static ValueTask<NativeStep> ExecuteJump(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return new(new NativeStep(instruction.A));
    }

    private static ValueTask<NativeStep> ExecuteBranch(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return BranchAsync(activation.Frame.GetDatum(instruction.A), instruction.B, instruction.C, token);
    }

    private static ValueTask<NativeStep> ExecuteMakeFunction(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        owner.MakeFunction(activation.Frame, activation.Execution.Metadata[pc]); return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecuteAdaptFunction(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        owner.AdaptFunction(activation.Frame, activation.Execution.Metadata[pc]); return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecuteCall(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.CallAsync(activation, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteCallNative(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.CallNativeAsync(activation.Frame, activation.Execution.Metadata[pc], pc, token, activation.Execution.Operands[pc].Signature);
    }

    private static ValueTask<NativeStep> ExecuteMakeRange(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.RangeInstructionAsync(activation.Frame, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteMakeLazyRange(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.RangeInstructionAsync(activation.Frame, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteDeferRange(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.RangeInstructionAsync(activation.Frame, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteOpenIterator(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.OpenIteratorAsync(activation, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteNextIterator(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return NextIteratorAsync(activation, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteCloseIterator(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return CloseIteratorAsync(activation, instruction.A, pc);
    }

    private static ValueTask<NativeStep> ExecuteProcessCall(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.ProcessCallAsync(activation.Frame, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteSelectPath(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.SelectPathAsync(activation.Frame, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteBarrier(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.BarrierAsync(activation, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteAcquire(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        return owner.AcquireAsync(activation, activation.Execution.Metadata[pc], pc, token);
    }

    private static ValueTask<NativeStep> ExecuteExecutePlan(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        owner.ExecutePlan(activation, instruction.A, token); return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecutePublishStatic(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        Volatile.Write(ref owner.statics[instruction.A].Ready, 1); return new(new NativeStep(pc + instruction.Span));
    }

    private static ValueTask<NativeStep> ExecuteUnreachable(NativeInterpreter owner, Activation activation, in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        throw new Msync.Core.MsyncException(activation.Execution.Metadata[pc].Symbol ?? "Unreachable instruction executed.", activation.Execution.Metadata[pc].Source);
    }

}
