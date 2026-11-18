using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal const int TierAddressReturn = -13;
    internal const int TierNumberReturn = -8, TierBoolReturn = -9;
    private readonly NativeTierOptions tierOptions;
    private readonly NativeTierCounters tierCounters = new();
    private readonly ConditionalWeakTable<NativeImage, NativeExecutionImage> executableImages = new();
    private NativeExecutionImage? lastExecutionImage;
    private TierInvocation? spareTier;
    private readonly ConcurrentStack<TierInvocation> tierPool = new();
    public NativeTierStatistics TierStatistics => tierCounters.Snapshot();

    private void DisposeTierPool()
    {
        Interlocked.Exchange(ref spareTier, null)?.Dispose();
        while (tierPool.TryPop(out TierInvocation? item)) item.Dispose();
    }

    private TierInvocation RentTierInvocation() => Interlocked.Exchange(ref spareTier, null)
        ?? (tierPool.TryPop(out TierInvocation? pooled) ? pooled : new TierInvocation());

    private void ReturnTierInvocation(TierInvocation invocation)
    {
        invocation.ClearReferences();
        if (disposed) { invocation.Dispose(); return; }
        if (Interlocked.CompareExchange(ref spareTier, invocation, null) is not null)
            tierPool.Push(invocation);
    }

    internal static unsafe nint TierBridgeAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&TierBridge;
    internal static unsafe nint TierFrameCallAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&TierFrameCall;
    internal static unsafe nint TierNumberAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, double, int>)&TierNumber;
    internal static unsafe nint TierBoolAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int>)&TierBool;
    internal static unsafe nint TierCommitAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&TierCommit;

    private NativeExecutionFunction ExecutionFunction(NativeImage selected, int index)
    {
        NativeExecutionImage? cached = Volatile.Read(ref lastExecutionImage);
        if (cached is null || !ReferenceEquals(cached.Image, selected))
        {
            cached = executableImages.GetValue(selected, image => new(image, tierOptions, scopedGuestValues));
            Volatile.Write(ref lastExecutionImage, cached);
        }
        return cached.Function(index);
    }

    private void ObserveTier(NativeExecutionFunction executable, NativeFrame frame, int pc)
    {
        if (!executable.NeedsProfiling) return;
        NativeInstruction instruction = executable.Metadata[pc];
        if (instruction.Op != NativeOp.Binary) return;
        NativeCodeType first = image.Program.Types[executable.Definition.Slots[instruction.A].Type];
        NativeCodeType second = image.Program.Types[executable.Definition.Slots[instruction.B].Type];
        if (first.Kind == TypeKind.Number && second.Kind == TypeKind.Number) return;
        NativeDatum left = frame.GetDatum(instruction.A), right = frame.GetDatum(instruction.B);
        if (left.IsReady && right.IsReady) executable.Observe(pc, new(left.Value.Kind, right.Value.Kind));
    }

    private TierOutcome RunTier(Activation activation, NativeExecutionFunction executable,
        NativeCompiledFunction version, int pc, CancellationToken token, ref TierInvocation? cached)
    {
        if (profile is not null) activation.Work.Counters.TierWorkspace(cached is not null);
        TierInvocation invocation = cached ??= RentTierInvocation();
        try
        {
            int status = RunTierSync(invocation, activation, executable, version, pc, token);
            return ResolveTierStatus(invocation, activation, executable, version, pc, status);
        }
        finally
        {
            invocation.ClearReferences();
        }
    }

    private TierOutcome ResolveTierStatus(TierInvocation invocation, Activation activation,
        NativeExecutionFunction executable, NativeCompiledFunction version, int pc, int status)
    {
        if (status == TierInvocation.DisposedExit) return new TierOutcome(pc, false, true);
        if (status == TierInvocation.ReturnNumberExit)
        {
            ReturnScalar(activation, NativeKind.Number, BitConverter.DoubleToUInt64Bits(invocation.ReturnNumber));
            return new TierOutcome(executable.Metadata.Length, true);
        }
        if (status == TierInvocation.ReturnBoolExit)
        {
            ReturnScalar(activation, NativeKind.Bool, invocation.ReturnBool ? 1UL : 0UL);
            return new TierOutcome(executable.Metadata.Length, true);
        }
        if (status == TierInvocation.ReturnNullExit)
        {
            ReturnSlot(activation, -1);
            return new TierOutcome(executable.Metadata.Length, true);
        }
        if (status == TierAddressReturn)
        {
            ReturnSlot(activation, executable.Instructions[invocation.Pc].A);
            return new TierOutcome(executable.Metadata.Length, true);
        }
        if (status == TierInvocation.TailExit) return new TierOutcome(invocation.Pc, false, true);
        if (status == TierInvocation.MachineCallExit)
            ThrowTierFailure(activation, executable.Diagnostic(invocation.Pc), invocation.Pc,
                new InvalidOperationException("A native closure left its synchronous call frame without returning."));
        if (invocation.Failure is { } failure) ThrowTierFailure(activation, executable.Diagnostic(invocation.Pc), invocation.Pc, failure);
        if (status == TierInvocation.DemandExit)
        {
            tierCounters.ReadinessExit();
            activation.Work.Counters.TierReadinessExit();
            return new TierOutcome(invocation.Pc, false, true);
        }
        if (status == TierInvocation.GuardExit)
        {
            tierCounters.GuardMiss();
            activation.Work.Counters.TierGuardMiss();
            executable.Invalidate(version);
            ObserveTier(executable, activation.Frame, invocation.Pc);
            return new TierOutcome(invocation.Pc, false, true);
        }
        if (status == TierInvocation.AwaitExit)
        {
            return CompleteTierDemand(invocation, activation, executable);
        }
        return new TierOutcome(executable.Metadata.Length, invocation.Returned);
    }

    private TierOutcome CompleteTierDemand(TierInvocation invocation, Activation activation, NativeExecutionFunction executable)
    {
        tierCounters.Suspended();
        activation.Work.Counters.TierSuspension();
        NativeStep step;
        try { step = DemandStep(invocation.Pending, activation, invocation.Pc); }
        catch (Exception error) { ThrowTierFailure(activation, executable.Diagnostic(invocation.Pc), invocation.Pc, error); throw; }
        if (!step.Returned) activation.Work.Counters.Edge(invocation.Pc, step.Next);
        if (step.Next <= invocation.Pc) executable.Backedge();
        return new(step.Next, step.Returned);
    }

    private unsafe int RunTierSync(TierInvocation invocation, Activation activation, NativeExecutionFunction executable,
        NativeCompiledFunction version, int pc, CancellationToken token)
    {
        Msync.Runtime.NativeCodePage.Entry entry;
        try { entry = version.OpenEntry(); }
        catch (ObjectDisposedException) { return TierInvocation.DisposedExit; }
        using var code = entry;
        var jump = (delegate* unmanaged[Cdecl]<nint, int, int>)entry.Address;
        NativeAllocation frameAllocation = activation.Frame.Allocation;
        ref NativeStorage.Data storage = ref frameAllocation.Storage;
        NativeAllocation? ext0 = null, ext1 = null;
        int[] externalSlots = version.Layout.DynamicExternalSlots;
        if (externalSlots.Length != 0)
        {
            NativeFrame frame = activation.Frame;
            for (int i = 0; i < externalSlots.Length; i++)
            {
                NativeAllocation alloc = frame.Location(externalSlots[i]).Allocation;
                if (ReferenceEquals(alloc, frameAllocation) || ReferenceEquals(alloc, ext0)) continue;
                if (ext0 is null) { ext0 = alloc; continue; }
                if (!ReferenceEquals(alloc, ext1)) { ext1 = alloc; break; }
            }
        }
        fixed (byte* frameBytes = storage.RawSpan)
        fixed (bool* frameInit = storage.InitializationSpan)
        fixed (byte* ext0Bytes = ext0 is null ? default : ext0.Storage.RawSpan)
        fixed (bool* ext0Init = ext0 is null ? default : ext0.Storage.InitializationSpan)
        fixed (byte* ext1Bytes = ext1 is null ? default : ext1.Storage.RawSpan)
        fixed (bool* ext1Init = ext1 is null ? default : ext1.Storage.InitializationSpan)
        fixed (int* pending = &memory.ActivePendingCount)
        {
            invocation.Reset(this, activation, executable, version, token, frameAllocation, (nint)frameBytes, (nint)frameInit, profile is not null,
                ext0, (nint)ext0Bytes, (nint)ext0Init,
                ext1, (nint)ext1Bytes, (nint)ext1Init);
            invocation.Memory.Context->FixedPending = (nint)pending;
            if (profile is not null)
                activation.Work.Counters.TierPendingContext(invocation.Memory.HadGlobalPending, invocation.Memory.HasRelevantPending);
            try
            {
                while (true)
                {
                    int status = jump(invocation.Handle, pc);
                    DrainFixedNativeReturn(invocation);
                    invocation.Memory.Flush(activation.Work.Counters, tierCounters, status);
                    tierCounters.Entered();
                    activation.Work.Counters.TierEntry();
                    if (status == TierInvocation.ReturnNumberExit)
                    {
                        invocation.ReturnNumber = invocation.Memory.SlotNumber(executable.Instructions[invocation.Pc].A);
                        return status;
                    }
                    if (status == TierInvocation.ReturnBoolExit)
                    {
                        invocation.ReturnBool = invocation.Memory.SlotBool(executable.Instructions[invocation.Pc].A);
                        return status;
                    }
                    if (status != TierInvocation.PollExit) return status;
                    tierCounters.Poll();
                    token.ThrowIfCancellationRequested();
                    pc = invocation.Pc;
                    if (guestHeapWorker != 0 && guestHeap.WantsActiveCollection) CollectActiveGuestHeap(activation, pc);
                    invocation.Memory.Continue();
                }
            }
            finally
            {
                invocation.FlushBridge();
                invocation.Memory.ReleasePins();
            }
        }
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe NativeStep ExecuteLinked(Activation activation, in NativeExecutionInstruction linked,
        int pc, CancellationToken token) =>
        ((delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, NativeStep>)linked.Handler)(
            this, activation, linked, pc, token);

    private static void ThrowTierFailure(Activation activation, NativeInstruction instruction, int pc, Exception error)
        => ThrowTierFailure(activation.Function, instruction, pc, error);

    private static void ThrowTierFailure(NativeFunctionCode function, NativeInstruction instruction, int pc, Exception error)
    {
        if (error is OperationCanceledException) ExceptionDispatchInfo.Capture(error).Throw();
        if (error is MsyncException msync)
        {
            if (msync.Span is null && instruction.Source is not null) throw new MsyncException(msync.Message, instruction.Source, msync);
            ExceptionDispatchInfo.Capture(msync).Throw();
        }
        if (error is InvalidOperationException)
            throw new MsyncException($"{error.Message} Native {instruction.Op} in function '{function.Name}', instruction {pc}, operands {instruction.A}/{instruction.B}/{instruction.C}, result {instruction.Result}.", instruction.Source, error);
        if (error is ArgumentException or IOException or UnauthorizedAccessException) throw new MsyncException(error.Message, instruction.Source, error);
        ExceptionDispatchInfo.Capture(error).Throw();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int TierBridge(nint handle, int pc)
    {
        if (FixedRawExit(handle, pc)) return TierInvocation.FixedRawExit;
        TierInvocation state = TierState(handle);
        if (state.Observed) state.BridgeCalls++;
        long visited = 0, polls = 0, bridgeDirect = 0, bridgeFallback = 0;
        NativeInterpreter owner = state.Owner;
        Activation activation = state.Activation;
        NativeExecutionFunction executable = state.Executable;
        NativeExecutionInstruction[] instructions = executable.Instructions;
        NativeFastOperation[] operations = state.Version.Operations;
        NativeFrame frame = activation.Frame;
        NativeMemory mem = owner.memory;
        NativeTierMemory tierMemory = state.Memory;
        bool observed = state.Observed;
        bool machineClosures = tierMemory.TracksMachineClosures;
        CancellationToken token = state.Token;
        if (observed) activation.Work.Counters.BridgeEntry(pc, operations[pc] != NativeFastOperation.Bridge);
        FixedWorkBinding work = default;
        try
        {
            DrainFixedNativeReturn(state);
            work = BindFixedWork(state, FixedWorkBoundary.Bridge);
            do
            {
                state.Pc = pc;
                if (--state.BridgeFuel <= 0)
                {
                    state.BridgeFuel = 256;
                    polls++;
                    token.ThrowIfCancellationRequested();
                }
                ref readonly NativeExecutionInstruction exec = ref instructions[pc];
                if (exec.Op == NativeOp.Call && exec.Result >= 0 && exec.Handler != DirectCallHandler
                    && activation.ReturnDestination.IsNull && owner.IsTailCall(activation, pc)) return TierInvocation.TailExit;
                if (observed)
                {
                    activation.Work.Counters.BridgeInstruction(pc);
                    ObserveBridgeOperation(state, pc, in exec);
                }
                visited += exec.Span;
                int nextPc;
                switch (exec.Op)
                {
                    case NativeOp.Jump:
                        bridgeDirect++;
                        nextPc = exec.A;
                        break;
                    case NativeOp.Branch:
                        nextPc = ReadyBridgeBranch(frame, in exec);
                        if (nextPc < 0) goto default;
                        bridgeDirect++;
                        break;
                    case NativeOp.Return:
                        bridgeDirect++;
                        owner.ReturnSlot(activation, exec.A);
                        state.Returned = true;
                        return TierInvocation.ReturnExit;
                    case NativeOp.AddressOffset:
                    case NativeOp.ProjectAddress:
                    case NativeOp.EnvironmentAddress:
                    case NativeOp.FunctionComponent:
                    case NativeOp.Unary:
                    case NativeOp.Select:
                    case NativeOp.MakeFunction:
                    case NativeOp.MakeType:
                    case NativeOp.AdaptFunction:
                    case NativeOp.Copy:
                    case NativeOp.Move:
                    case NativeOp.Drop:
                    case NativeOp.Load:
                    case NativeOp.Store:
                    case NativeOp.Allocate when exec.C < 0:
                        bridgeDirect++;
                        nextPc = owner.ExecuteLinked(activation, in exec, pc, token).Next;
                        break;
                    case NativeOp.Narrow:
                    case NativeOp.Convert:
                        bridgeDirect++;
                        nextPc = ExecuteBridgeRefinement(state, in exec, pc);
                        break;
                    case NativeOp.Binary when exec.Handler == UnsignedBinaryHandler:
                        bridgeDirect++;
                        nextPc = ExecuteUnsignedBinary(owner, activation, in exec, pc, token).Next;
                        break;
                    case NativeOp.Binary when exec.Handler != ConnectionQualifyHandler:
                        bridgeDirect++;
                        nextPc = ExecuteBinary(owner, activation, in exec, pc, token).Next;
                        break;
                    case NativeOp.BindLocation:
                        bridgeDirect++;
                        nextPc = owner.ExecuteLinked(activation, in exec, pc, token).Next;
                        tierMemory.Refresh(exec.A);
                        break;
                    case NativeOp.CallNative when owner.TryFastUnaryMath(activation.Work, frame, in executable.Metadata[pc]):
                        bridgeDirect++;
                        nextPc = pc + exec.Span;
                        break;
                    default:
                    {
                        bridgeFallback++;
                        NativeStep pending = owner.ExecuteLinked(activation, in exec, pc, token);
                        if (pending.Pending is not null)
                        {
                            state.Pending = pending;
                            return TierInvocation.AwaitExit;
                        }
                        if (pending.Returned)
                        {
                            state.Returned = true;
                            return TierInvocation.ReturnExit;
                        }
                        nextPc = pending.Next;
                        break;
                    }
                }
                if (observed) activation.Work.Counters.Edge(pc + exec.Span - 1, nextPc);
                if (machineClosures)
                    tierMemory.RefreshMachineReference(exec.Result);
                if (mem.HasAnyPending)
                    tierMemory.RefreshPending(exec.Result, exec.Op is NativeOp.Move or NativeOp.Drop ? exec.A : -1);
                pc = nextPc;
                if (polls >= 256)
                {
                    state.BridgeVisited += visited;
                    state.BridgePolls += polls;
                    state.BridgeDirect += bridgeDirect;
                    state.BridgeFallback += bridgeFallback;
                    visited = 0;
                    polls = 0;
                    bridgeDirect = 0;
                    bridgeFallback = 0;
                    state.FlushBridge();
                }
            } while ((uint)pc < (uint)instructions.Length
                && operations[pc] == NativeFastOperation.Bridge);
            if (mem.HasAnyPending || tierMemory.HasPendingRevision) tierMemory.RefreshPending();
            return pc;
        }
        catch (Exception error) { state.Failure = error; return TierInvocation.ErrorExit; }
        finally
        {
            work.Restore();
            state.BridgeVisited += visited;
            state.BridgePolls += polls;
            state.BridgeDirect += bridgeDirect;
            state.BridgeFallback += bridgeFallback;
            if (state.BridgeVisited >= 65536) state.FlushBridge();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierFrameCall(nint handle, int pc)
    {
        if (FixedRawExit(handle, pc)) return TierInvocation.FixedRawExit;
        TierInvocation state = TierState(handle);
        state.Pc = pc;
        FixedWorkBinding work = default;
        try
        {
            DrainFixedNativeReturn(state);
            if (--state.BridgeFuel <= 0)
            {
                state.BridgeFuel = 256;
                state.BridgePolls++;
                state.Token.ThrowIfCancellationRequested();
            }
            NativeInterpreter owner = state.Owner;
            Activation activation = state.Activation;
            bool fused = state.Version.Operations[pc] == NativeFastOperation.FrameLoadCall;
            int loadPc = pc;
            if (fused) pc += state.Executable.Instructions[pc].Span;
            ref readonly NativeExecutionInstruction exec = ref state.Executable.Instructions[pc];
            bool direct = exec.Handler == DirectCallHandler && !fused;
            if (!direct && !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                if (state.Observed) activation.Work.Counters.TierFrameCallExit();
                return TierInvocation.DemandExit;
            }
            work = BindFixedWork(state, FixedWorkBoundary.Call);
            if (state.Observed)
            {
                state.BridgeCalls++;
                state.Executable.Visit(activation.Work.Counters, pc);
            }
            NativeInstruction call = state.Executable.Metadata[pc];
            NativeFunction? ready = null;
            if (direct) owner.CallDirect(activation, call, pc, state.Token);
            else
            {
                NativeAddress callee;
                if (fused)
                {
                    ref readonly NativeExecutionInstruction load = ref state.Executable.Instructions[loadPc];
                    callee = activation.Frame.Location(load.A);
                    if (!callee.IsInitialized || owner.memory.TryPending(callee, out _))
                    {
                        owner.ExecuteLinked(activation, in load, loadPc, state.Token);
                        callee = activation.Frame.Location(load.Result);
                    }
                    else ready = NativeStorage.LoadFunctionDirect(callee);
                    state.FixedLoadPc = loadPc;
                    state.BridgeVisited += load.Span;
                    if (state.Observed)
                    {
                        state.Executable.Visit(activation.Work.Counters, loadPc);
                        activation.Work.Counters.Edge(pc - 1, pc);
                    }
                }
                else callee = activation.Frame.Location(call.A);
                state.Pc = pc;
                if (owner.CallFixedContinuation(activation, call, callee, pc, state.Token, ready, fused ? state : null))
                    return FixedPrepareStatus(state);
            }
            CompleteFrameCall(state, pc, fused, ready.HasValue);
            return 0;
        }
        catch (Exception error) { state.Failure = error; return TierInvocation.ErrorExit; }
        finally { work.Restore(); }
    }

    private static void CompleteFrameCall(TierInvocation state, int pc, bool fused, bool elided)
    {
        ref readonly NativeExecutionInstruction exec = ref state.Executable.Instructions[pc];
        if (state.Memory.TracksMachineClosures) state.Memory.RefreshMachineReference(exec.Result);
        if (state.Owner.memory.HasAnyPending || state.Memory.HasPendingRevision) state.Memory.RefreshPending();
        RecordFrameCall(state, pc, fused, elided);
    }

    private static void RecordFrameCall(TierInvocation state, int pc, bool fused, bool elided, int count = 1)
    {
        ref readonly NativeExecutionInstruction exec = ref state.Executable.Instructions[pc];
        state.BridgeVisited += (long)exec.Span * count;
        state.BridgeDirect += count;
        if (state.Observed)
        {
            for (int i = 0; i < count; i++)
            {
                state.Activation.Work.Counters.Edge(pc + exec.Span - 1, pc + exec.Span);
                state.Activation.Work.Counters.TierFrameCall(fused, elided);
            }
        }
        if (state.BridgeVisited >= 65536) state.FlushBridge();
    }

    // Keep wide datum and diagnostic temporaries out of the frame allocated
    // on every machine-to-VM transition, including single-op bridge batches.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadyBridgeBranch(NativeFrame frame, in NativeExecutionInstruction exec)
    {
        NativeDatum condition = frame.GetDatum(exec.A);
        return !condition.IsReady ? -1 : condition.Value.AsBool() ? exec.B : exec.C;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteBridgeRefinement(TierInvocation state, in NativeExecutionInstruction exec, int pc)
    {
        NativeInterpreter owner = state.Owner;
        Activation activation = state.Activation;
        NativeFrame frame = activation.Frame;
        ref readonly NativeExecutionOperand operand = ref state.Executable.Operands[pc];
        NativeInstruction meta = state.Executable.Metadata[pc];
        NativeAddress destination = frame.Location(exec.Result);
        bool narrow = exec.Op == NativeOp.Narrow;
        bool ready = narrow
            ? operand.Type is { } type && owner.memory.TryNarrowReady(frame, exec.A, destination, owner.image, type, meta.Type, operand.NeedsLayoutCheck)
            : owner.memory.TryConvertReady(frame, exec.A, destination, owner.image, meta.Type, operand.NeedsLayoutCheck);
        if (state.Observed) RecordTierCopy(activation.Work.Counters, frame.Location(exec.A).Type, destination, ready);
        if (!ready)
        {
            NativeDatum source = frame.GetDatum(exec.A);
            frame.SetDatum(exec.Result, narrow
                ? owner.NarrowDatum(source, meta, operand.Type!, activation.Function.Name, pc)
                : owner.ConvertDatum(source, meta));
        }
        return pc + exec.Span;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void ObserveBridgeOperation(TierInvocation state, int pc, in NativeExecutionInstruction exec)
    {
        Activation activation = state.Activation;
        NativeExecutionFunction executable = state.Executable;
        NativeFrame frame = activation.Frame;
        NativeTierMemory tierMemory = state.Memory;
        NativeFastOperation operation = state.Version.Operations[pc];
        executable.Visit(activation.Work.Counters, pc);
        if (operation.IsValueMaterialization()) activation.Work.Counters.ValueMaterializationExit(state.Owner.memory.HasAnyPending);
        if (operation.IsGuestAccess())
        {
            nint* edges = (nint*)tierMemory.Context->GuestEdges;
            if (operation == NativeFastOperation.GuestView)
                activation.Work.Counters.GuestViewFallback();
            else if (operation == NativeFastOperation.GuestCopy)
                activation.Work.Counters.GuestCopyFallback(state.Owner.memory.HasAnyPending,
                    frame.Location(exec.A).IsInitialized);
            else if (operation == NativeFastOperation.GuestReferenceLoad)
                activation.Work.Counters.GuestReferenceLoadFallback(state.Owner.memory.HasAnyPending,
                    edges is not null && NativeGuestObjectTable.IsObjectEdge(edges[executable.FrameSlot(exec.A).Offset.References]));
            else if (operation == NativeFastOperation.GuestReferenceStore)
                activation.Work.Counters.GuestReferenceStoreExit(state.Owner.memory.HasAnyPending);
            else if (operation == NativeFastOperation.GuestHasLayout)
            {
                executable.TryGuestLayoutCheck(pc, out NativeFrameSlot basis);
                activation.Work.Counters.GuestLayoutFallback(state.Owner.memory.HasAnyPending,
                    edges is null ? 0 : edges[basis.Offset.References]);
            }
            else
            {
                executable.TryGuestAccess(pc, out NativeGuestAccess access);
                activation.Work.Counters.GuestFieldFallback(state.Owner.memory.HasAnyPending,
                    edges is not null && NativeGuestObjectTable.IsObjectEdge(edges[access.Base.Offset.References]));
            }
        }
    }

    private static unsafe TierInvocation TierState(nint context) =>
        (TierInvocation)GCHandle.FromIntPtr(((NativeTierContext*)context)->Handle).Target!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool FixedRawExit(nint address, int pc)
    {
        NativeTierContext* context = (NativeTierContext*)address;
        if (context->Handle != 0) return false;
        context->Pc = pc;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierNumber(nint handle, int pc, double result) => StoreTierResult(handle, pc, NativeValue.FromNumber(result));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierBool(nint handle, int pc, int result) => StoreTierResult(handle, pc, NativeValue.FromBool(result != 0));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierCommit(nint handle, int pc)
    {
        TierInvocation state = TierState(handle);
        DrainFixedNativeReturn(state);
        NativeRegisterRegion region = state.Version.Layout.RegionsByPc[pc];
        state.Memory.Commit(region);
        return region.End;
    }

    private static int StoreTierResult(nint context, int pc, NativeValue result)
    {
        if (FixedRawExit(context, pc)) return TierInvocation.FixedRawExit;
        TierInvocation state = TierState(context);
        state.Pc = pc;
        try
        {
            DrainFixedNativeReturn(state);
            state.Owner.tierCounters.Poll();
            state.Token.ThrowIfCancellationRequested();
            NativeExecutionInstruction instruction = state.Executable.Instructions[pc];
            if (state.Observed)
            {
                state.Executable.Visit(state.Activation.Work.Counters, pc);
                if (state.Version.Operations[pc].IsUnaryMath())
                    state.Activation.Work.Counters.Native(state.Executable.Metadata[pc].Symbol!);
                else if (state.Version.Operations[pc].IsScalarSelect())
                    state.Activation.Work.Counters.DirectSelect(1);
            }
            state.Owner.tierCounters.Operation(true, instruction.Span);
            if (state.Observed) state.Activation.Work.Counters.TierOperation(true, instruction.Span);
            state.Activation.Frame.Set(instruction.Result, result);
            state.Memory.RefreshPending(instruction.Result);
            if (state.Observed) state.Activation.Work.Counters.Edge(pc + instruction.Span - 1, pc + instruction.Span);
            return pc + instruction.Span;
        }
        catch (Exception error) { state.Failure = error; return TierInvocation.ErrorExit; }
    }

    private readonly record struct TierOutcome(int Next, bool Returned, bool ExecuteBaseline = false);

    private static void RecordTierCopy(RuntimeProfile.NativeFrameCounters counters, NativeType source,
        NativeAddress destination, bool ready)
    {
        if (!ready) { counters.TierCopy(-1); return; }
        NativeType target = destination.Type;
        int roots = source.Equals(target) ? target.Representation.References
            : NativeRepresentation.Of(target.IsUnion ? destination.Allocation.Storage.ReadKind(destination.Offset) : target.Kind).References;
        counters.TierCopy(roots);
    }

    private sealed unsafe class TierInvocation : IDisposable
    {
        internal const int ReturnExit = -1, AwaitExit = -2, GuardExit = -3, ErrorExit = -4, PollExit = -5, TailExit = -6, DisposedExit = -7, ReturnNumberExit = TierNumberReturn, ReturnBoolExit = TierBoolReturn, DemandExit = -10, ReturnNullExit = -11, MachineCallExit = -12;
        internal const int FixedRawExit = -14;
        private readonly GCHandle handle;
        internal TierInvocation()
        {
            handle = GCHandle.Alloc(this);
            try { Memory = new(GCHandle.ToIntPtr(handle)); }
            catch { handle.Free(); throw; }
        }
        internal void Reset(NativeInterpreter owner, Activation activation, NativeExecutionFunction executable,
            NativeCompiledFunction version, CancellationToken token, NativeAllocation? pinnedFrame, nint pinnedBytes, nint pinnedFlags, bool observed,
            NativeAllocation? pinnedExt0 = null, nint pinnedExt0Bytes = 0, nint pinnedExt0Flags = 0,
            NativeAllocation? pinnedExt1 = null, nint pinnedExt1Bytes = 0, nint pinnedExt1Flags = 0)
        {
            Owner = owner;
            Activation = activation;
            Executable = executable;
            Version = version;
            Token = token;
            Pending = default;
            Failure = null;
            Returned = false;
            FixedEntry = false;
            FixedWorkEntered = false;
            FixedCaptureLocations = 0;
            BridgeFuel = 256;
            BridgeCalls = 0;
            BridgeVisited = 0;
            BridgePolls = 0;
            BridgeDirect = 0;
            BridgeFallback = 0;
            Memory.Reset(activation.Frame, owner.memory, executable, version, pinnedFrame, pinnedBytes, pinnedFlags, observed,
                pinnedExt0, pinnedExt0Bytes, pinnedExt0Flags, pinnedExt1, pinnedExt1Bytes, pinnedExt1Flags);
        }
        internal long BridgeCalls;
        internal int FixedLoadPc;
        private NativeCompiledFunction? fixedEntryOwner;
        private nint fixedEntryAddress;

        internal nint HoldFixedEntry(NativeCompiledFunction code, out bool reused)
        {
            reused = ReferenceEquals(code, fixedEntryOwner);
            if (reused) return fixedEntryAddress;
            nint address = code.RetainEntry();
            fixedEntryOwner?.ReleaseEntry();
            fixedEntryOwner = code;
            return fixedEntryAddress = address;
        }

        private void ReleaseFixedEntry()
        {
            fixedEntryOwner?.ReleaseEntry();
            fixedEntryOwner = null;
            fixedEntryAddress = 0;
        }

        internal void FlushBridge()
        {
            long visited = BridgeVisited, polls = BridgePolls, direct = BridgeDirect, fallback = BridgeFallback;
            long calls = BridgeCalls;
            if (visited == 0 && polls == 0 && direct == 0 && fallback == 0 && calls == 0) return;
            BridgeCalls = 0;
            BridgeVisited = 0;
            BridgePolls = 0;
            BridgeDirect = 0;
            BridgeFallback = 0;
            Owner.tierCounters.BridgeBatch(visited, polls, direct, fallback, calls);
            if (Observed) Activation.Work.Counters.TierBridgeBatch(visited);
        }
        internal void ClearReferences()
        {
            ReleaseFixedEntry();
            Memory.ClearReferences();
            Owner = null!;
            Activation = null!;
            Executable = null!;
            Version = null!;
            Token = default;
            Pending = default;
            Failure = null;
            Returned = false;
        }
        internal nint Handle => (nint)Memory.Context;
        internal NativeTierMemory Memory { get; }
        private NativeTierMemory? fixedRaw;
        internal NativeTierMemory FixedRaw => fixedRaw ??= new(0);
        internal NativeInterpreter Owner { get; private set; } = null!;
        internal bool Observed => Owner.profile is not null;
        internal Activation Activation { get; private set; } = null!;
        internal NativeExecutionFunction Executable { get; private set; } = null!;
        internal NativeCompiledFunction Version { get; private set; } = null!;
        internal CancellationToken Token { get; private set; }
        internal NativeStep Pending;
        internal PreparedFixedTier Prepared;
        internal UnattachedFixedFrame Unattached;
        internal bool FixedEntry, FixedWorkEntered;
        internal int FixedCaptureLocations;
        internal Exception? Failure;
        internal int Pc { get => Memory.Context->Pc; set => Memory.Context->Pc = value; }
        internal bool Returned;
        internal int BridgeFuel;
        internal long BridgeVisited;
        internal long BridgePolls;
        internal long BridgeDirect;
        internal long BridgeFallback;
        internal double ReturnNumber;
        internal bool ReturnBool;
        public void Dispose() { fixedRaw?.Dispose(); Memory.Dispose(); handle.Free(); }
    }
}
