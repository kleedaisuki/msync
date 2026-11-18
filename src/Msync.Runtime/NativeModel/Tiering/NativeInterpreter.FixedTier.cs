using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal const int FixedPrepareMachineArguments = 2;
    internal const int FixedPrepareMachineFrame = 3;
    internal const byte FixedRepeatLimit = 63;

    private static unsafe int FixedPrepareStatus(TierInvocation caller) => caller.Memory.Context->FixedReturnState < 0
        ? FixedPrepareMachineFrame : caller.Prepared.MachineArguments is null ? 1 : FixedPrepareMachineArguments;

    private readonly record struct PreparedFixedTier(Generation Generation, Activation? Child,
        TierInvocation? Tier, NativeTierMemory Memory, NativeCompiledFunction Code, int CallPc, bool Elided,
        NativePhysicalCall? MachineArguments, int DeferredLocations);

    private readonly record struct UnattachedFixedFrame(NativeExecutionFunction Executable, NativeEnvironment? Environment,
        NativeArguments Arguments, NativeFrameStack.Lease Lease, RuntimeProfile.NativeFrameCounters Counters);

    internal enum FixedWorkBoundary : byte { Bridge, Call, Resume }

    private readonly record struct FixedWorkBinding(NativeInterpreter? Owner, NativeWorkScope? Previous)
    {
        internal void Restore()
        {
            if (Owner is not null) Owner.currentWork.Value = Previous;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FixedWorkBinding BindFixedWork(TierInvocation invocation, FixedWorkBoundary boundary) =>
        invocation.FixedEntry ? EnterFixedWork(invocation, boundary) : default;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedWorkBinding EnterFixedWork(TierInvocation invocation, FixedWorkBoundary boundary)
    {
        Activation child = invocation.Activation;
        NativeInterpreter owner = invocation.Owner;
        if (invocation.FixedCaptureLocations != 0)
        {
            child.Frame.BindCaptureLocations(child.Invocation.Environment!);
            child.HeapCaller!.Work.Counters.FixedTierCaptureBindings(materialized: invocation.FixedCaptureLocations);
            invocation.FixedCaptureLocations = 0;
        }
        if (!child.OwnsWork) child.Enter(new(child.Work), ownsWork: true);
        if (!invocation.FixedWorkEntered)
        {
            invocation.FixedWorkEntered = true;
            child.HeapCaller!.Work.Counters.FixedTierWorkBoundary(boundary);
        }
        NativeWorkScope? previous = owner.currentWork.Value;
        if (ReferenceEquals(previous, child.Work)) return default;
        owner.currentWork.Value = child.Work;
        return new(owner, previous);
    }

    internal static unsafe nint TierFixedReturnAddress =>
        (nint)(delegate* unmanaged[Cdecl]<nint, int, ulong, int>)&TierFixedReturn;

    private unsafe bool TryPrepareFixedTier(TierInvocation parent, NativeExecutionFunction executable,
        NativeEnvironment? environment, NativeArguments arguments, int pc, bool elided, in NativeAddress callee)
    {
        if (!scopedGuestValues || disposed || executable.PublishedTier is not { } code
            || executable.Definition.SsaBody is not null) return false;
        nint entry;
        bool reused;
        try { entry = parent.HoldFixedEntry(code, out reused); }
        catch (ObjectDisposedException) { return false; }

        Generation selected = programUpdated ? activeGeneration.Value ?? generation : generation;
        Activation? child = null;
        TierInvocation? tier = null;
        NativeTierMemory? raw = null;
        try
        {
            NativeFrame caller = parent.Activation.Frame;
            NativePhysicalCall? moves = parent.Executable.FixedMachineMoves(pc);
            if (moves is not null)
            {
                NativeArgumentPlacement placement = code.ReadsParametersOnBind || !executable.CanUseStackFrame
                    ? NativeArgumentPlacement.Frame
                    : memory.HasPending(caller.Allocation) ? NativeArgumentPlacement.Pending
                    : arguments.ClassifyMachinePlacement(moves, caller);
                if (placement != NativeArgumentPlacement.Machine)
                {
                    parent.Activation.Work.Counters.FixedTierArgumentExit(placement);
                    moves = null;
                }
            }
            int deferredLocations = environment is not null && executable.CanUseStackFrame ? code.DeferredLocationCaptures : 0;
            if (moves is not null && deferredLocations == executable.Definition.CaptureCount
                && executable.CanReserveUnattached && code.Layout.CanRunFixedRaw && !memory.HasAnyPending
                && TryPrepareUnattached(parent, executable, selected, environment, arguments, moves, code, entry, pc, elided,
                    deferredLocations, reused, in callee))
                return true;
            child = EnterGuest(selected, executable, environment, moves is null ? arguments : arguments.WithMachinePlacement(),
                parent.Activation.Work, deferWork: true, deferredLocationCaptures: deferredLocations);
            child.Invocation.Arguments = arguments;
            child.Invocation.Started = true;
            NativeAllocation allocation = child.Frame.Allocation;
            ref NativeStorage.Data storage = ref allocation.Storage;
            bool hasPending = memory.HasAnyPending;
            if (!hasPending && storage.StackAddressable && code.Layout.CanRunFixedRaw)
            {
                raw = parent.FixedRaw;
                raw.ResetFixedRaw(executable, code.Layout, storage.RawAddress, storage.InitializationAddress,
                    storage.roots.GuestAddress, profile is not null);
                child.Work.Counters.TierPendingContext(global: false, relevant: false);
            }
            else
            {
                tier = RentTierInvocation();
                BindFixedTier(tier, parent, child, code, deferredLocations);
                if (profile is not null)
                    child.Work.Counters.TierPendingContext(tier.Memory.HadGlobalPending, tier.Memory.HasRelevantPending);
            }
            NativeTierMemory childMemory = raw ?? tier!.Memory;
            if (moves is not null)
            {
                // No safepoint separates the emitted stores from entry into the child.
                parent.Memory.Context->StackCursor = (nint)NativeFrameStack.Current.Cursor;
            }
            parent.Prepared = new(selected, child, tier, childMemory, code, pc, elided, moves, deferredLocations);
            parent.Memory.Context->ChildContext = (nint)childMemory.Context;
            parent.Memory.Context->ChildEntry = entry;
            parent.Activation.Work.Counters.FixedTierCodeEntry(reused);
            if (deferredLocations != 0)
                parent.Activation.Work.Counters.FixedTierCaptureBindings(deferred: deferredLocations);
            return true;
        }
        catch (Exception failure)
        {
            NativeWorkScope? previousWork = currentWork.Value;
            try
            {
                ExceptionDispatchInfo.Capture(UnwindGuestTo(child, parent.Activation, failure, parent.Token)).Throw();
                throw;
            }
            finally
            {
                raw?.ClearReferences();
                if (tier is not null) ReturnTierInvocation(tier);
                currentWork.Value = previousWork;
            }
        }
    }

    private unsafe bool TryPrepareUnattached(TierInvocation parent, NativeExecutionFunction executable, Generation selected,
        NativeEnvironment? environment, NativeArguments arguments, NativePhysicalCall moves, NativeCompiledFunction code,
        nint entry, int pc, bool elided, int deferredLocations, bool reused, in NativeAddress callee)
    {
        NativeFrameStack stack = NativeFrameStack.Current;
        NativeGuestObjectTable arena = guestHeap.References!;
        if (!stack.TryPlanReservation(executable.FixedFrameLayout, arena, out NativeFrameStack.Lease lease, out NativeOffset start))
        {
            parent.Activation.Work.Counters.FixedUnattachedReservationMiss();
            return false;
        }
        int emittedReturn = lease is { Bytes: <= 512, Roots: <= 32 } ? moves.ScalarResult switch
        {
            NativeKind.Number => TierNumberReturn,
            NativeKind.Bool => TierBoolReturn,
            _ => 0
        } : 0;
        NativeTierMemory? raw = null;
        bool published = false;
        try
        {
            raw = parent.FixedRaw;
            if (emittedReturn != 0)
                raw.ResetFixedRaw(executable, code.Layout, 0, 0, 0, profile is not null);
            else
            {
                stack.PublishReservation(lease, start, arena);
                published = true;
                NativeStackHeader* frame = stack.FramePointer;
                raw.ResetFixedRaw(executable, code.Layout, frame->Data, frame->Initialization, frame->References, profile is not null);
            }
            RuntimeProfile.NativeFrameCounters counters = profile?.EnterNative(selected.Image.Program, executable.Index) ?? default;
            if (profile is not null)
            {
                counters.StackFrame(lease.Bytes, lease.Roots, 0, nativeReferences: true, segmentReentry: false);
                counters.CallStorage(0, arguments.CopiedCount, false, default, false);
                counters.TierPendingContext(global: false, relevant: false);
            }
            RuntimeProfile.NativeFrameCounters observed = parent.Activation.Work.Counters;
            observed.FixedTierCodeEntry(reused);
            if (profile is not null) observed.FixedUnattachedReservation();
            if (deferredLocations != 0) observed.FixedTierCaptureBindings(deferred: deferredLocations);
            parent.Unattached = new(executable, environment, arguments, lease, counters);
            parent.Prepared = new(selected, null, null, raw, code, pc, elided, moves, deferredLocations);
            NativeTierContext* context = parent.Memory.Context;
            context->StackCursor = (nint)stack.Cursor;
            context->ChildContext = (nint)raw.Context;
            context->ChildEntry = entry;
            context->FixedCallStart = start;
            context->FixedReturnTop = lease.Previous;
            context->FixedRepeats = context->FixedRepeatFuel = 0;
            context->FixedRepeatPc = -1;
            if (emittedReturn != 0 && elided && context->FixedPending != 0)
                SetFixedCallableStamp(context, pc, in callee, arena);
            // The emitted prologue owns publication when the scalar continuation is armed.
            context->FixedReturnState = emittedReturn;
            return true;
        }
        catch
        {
            raw?.ClearReferences();
            if (published) stack.ReleaseReserved(lease);
            throw;
        }
    }

    private static unsafe void SetFixedCallableStamp(NativeTierContext* context, int pc, in NativeAddress callee,
        NativeGuestObjectTable arena)
    {
        ref NativeStorage.Data storage = ref callee.Allocation.Storage;
        if (!ReferenceEquals(storage.roots.ReferenceTable, arena) || storage.InitializationAddress == 0) return;
        nint* edges = (nint*)storage.roots.GuestAddress + callee.Offset.References;
        // Host leases are indexed by slot; equal markers do not imply equal values.
        if (edges[0] < 0 || edges[1] != 0) return;
        context->FixedCallableInit = storage.InitializationAddress + callee.Offset.Bytes;
        context->FixedCallableEdges = (nint)edges;
        context->FixedEnvironment = edges[0];
        context->FixedRepeatPc = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void DrainFixedNativeReturn(TierInvocation invocation)
    {
        if (invocation.Memory.Context->FixedReturnState > 0) CompleteFixedNativeReturn(invocation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void CompleteFixedNativeReturn(TierInvocation parent)
    {
        NativeTierContext* context = parent.Memory.Context;
        int status = -context->FixedReturnState;
        context->FixedReturnState = 0;
        context->ChildContext = 0;
        context->ChildEntry = 0;
        PreparedFixedTier prepared = parent.Prepared;
        parent.Prepared = default;
        RuntimeProfile.NativeFrameCounters child = parent.Unattached.Counters;
        int repeats = context->FixedRepeats;
        RecordRepeatedAdmissions(parent, in prepared, repeats);
        parent.Unattached = default;
        NativeTierCounters counters = parent.Owner.tierCounters;
        prepared.Memory.Flush(child, counters, status, repeats, context->FixedRepeatFuel);
        prepared.Memory.ClearReferences();
        RecordCompletedUnattached(parent, in prepared, child, repeats + 1);
    }

    private static void RecordRepeatedAdmissions(TierInvocation parent, in PreparedFixedTier prepared, int repeats)
    {
        if (repeats == 0) return;
        int load = parent.FixedLoadPc;
        parent.BridgeVisited += (long)parent.Executable.Instructions[load].Span * repeats;
        parent.BridgeFuel -= repeats;
        if (!parent.Observed) return;
        RuntimeProfile.NativeFrameCounters caller = parent.Activation.Work.Counters;
        caller.FixedRepeatedAdmissions(repeats);
        ref UnattachedFixedFrame frame = ref parent.Unattached;
        for (int i = 0; i < repeats; i++)
        {
            caller.Call(prepared.CallPc, frame.Executable.Index);
            parent.Executable.Visit(caller, load);
            parent.Executable.Visit(caller, prepared.CallPc);
            caller.Edge(prepared.CallPc - 1, prepared.CallPc);
            caller.FixedTierCodeEntry(reused: true);
            caller.FixedUnattachedReservation();
            caller.FixedTierCaptureBindings(deferred: prepared.DeferredLocations);
            frame.Counters.StackFrame(frame.Lease.Bytes, frame.Lease.Roots, 0, nativeReferences: true, segmentReentry: false);
            frame.Counters.CallStorage(0, frame.Arguments.CopiedCount, false, default, false);
            frame.Counters.TierPendingContext(global: false, relevant: false);
        }
    }

    private static void RecordCompletedUnattached(TierInvocation parent, in PreparedFixedTier prepared,
        RuntimeProfile.NativeFrameCounters child, int count)
    {
        parent.Owner.tierCounters.Entered(count);
        if (parent.Observed)
        {
            RuntimeProfile.NativeFrameCounters caller = parent.Activation.Work.Counters;
            NativePhysicalCall moves = prepared.MachineArguments!;
            for (int i = 0; i < count; i++)
            {
                child.TierEntry();
                child.PhysicalArguments(moves.Moves.Length, moves.Omitted);
                caller.FixedTierEntry();
                caller.FixedTierArgumentPlacement(moves.Moves.Length, moves.ReferenceWords);
                caller.FixedRawTierExit(returned: true, managed: false);
                caller.FixedTierWorklessReturn();
                caller.FixedUnattachedReturn();
                caller.FixedNativeReturn();
                caller.FixedEmittedFrame();
                if (prepared.DeferredLocations != 0)
                    caller.FixedTierCaptureBindings(avoided: prepared.DeferredLocations);
                caller.FixedGuestReturn();
                caller.FixedTierScalarReturn();
                caller.FixedTierCall();
            }
        }
        // A native RET already wrote the exact rootless result and retired the pending-free child.
        RecordFrameCall(parent, prepared.CallPc, fused: true, prepared.Elided, count);
    }

    private Activation AttachUnattached(TierInvocation parent, in UnattachedFixedFrame reserved)
    {
        NativeExecutionFunction executable = reserved.Executable;
        NativeWorkScope owner = parent.Activation.Work;
        if (profile is not null) owner = new(owner) { Counters = reserved.Counters };
        NativeFrame frame = executable.TakeFrameView(memory, guestHeap);
        bool reused = frame.Activation is not null;
        Activation activation = frame.Activation ??= new(executable.Index, executable.Definition, frame, executable);
        activation.Enter(owner, profile is not null);
        activation.Invocation = new()
        {
            Environment = reserved.Environment, Arguments = reserved.Arguments, Parent = parent.Activation.Work,
            Destination = -1, Started = true
        };
        reserved.Counters.CallActivation(reused);
        frame.AttachStack(reserved.Lease);
        reserved.Lease.Stack.Enter(activation);
        return activation;
    }

    private void BindFixedTier(TierInvocation tier, TierInvocation parent, Activation child,
        NativeCompiledFunction code, int deferredLocations)
    {
        NativeAllocation frame = child.Frame.Allocation;
        ref NativeStorage.Data storage = ref frame.Storage;
        tier.Reset(this, child, child.Execution, code, parent.Token, frame, storage.RawAddress,
            storage.InitializationAddress, profile is not null);
        tier.FixedEntry = true;
        tier.FixedCaptureLocations = deferredLocations;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int TierFixedReturn(nint handle, int status, ulong bits)
    {
        TierInvocation parent = TierState(handle);
        try { return FinishPreparedFixedTier(parent, status, bits); }
        catch (Exception failure) { parent.Failure = failure; return TierInvocation.ErrorExit; }
    }

    private static unsafe int FinishPreparedFixedTier(TierInvocation parent, int status, ulong bits)
    {
        NativeInterpreter owner = parent.Owner;
        PreparedFixedTier prepared = parent.Prepared;
        parent.Prepared = default;
        bool emittedFrame = parent.Memory.Context->FixedReturnState < 0;
        int repeats = emittedFrame ? parent.Memory.Context->FixedRepeats : 0;
        int repeatFuel = emittedFrame ? parent.Memory.Context->FixedRepeatFuel : 0;
        parent.Memory.Context->FixedReturnState = 0;
        parent.Memory.Context->ChildContext = 0;
        parent.Memory.Context->ChildEntry = 0;

        Activation? child = prepared.Child;
        Activation? current = child;
        TierInvocation? tier = prepared.Tier;
        bool raw = tier is null;
        bool ownsReservation = child is null;
        ref UnattachedFixedFrame reserved = ref parent.Unattached;
        RuntimeProfile.NativeFrameCounters childCounters = child is null ? reserved.Counters : child.Work.Counters;
        FixedWorkBinding work = default;
        try
        {
            if (tier is not null) DrainFixedNativeReturn(tier);
            RecordRepeatedAdmissions(parent, in prepared, repeats);
            if (emittedFrame && parent.Observed) parent.Activation.Work.Counters.FixedEmittedFrame();
            parent.Activation.Work.Counters.FixedTierEntry();
            if (prepared.MachineArguments is { } arguments)
            {
                parent.Activation.Work.Counters.FixedTierArgumentPlacement(arguments.Moves.Length, arguments.ReferenceWords);
                childCounters.PhysicalArguments(arguments.Moves.Length, arguments.Omitted);
            }
            int destination = parent.Executable.Instructions[prepared.CallPc].Result;
            bool scalar = owner.CanPublishFixedScalar(parent, child, destination, status);
            int pc = prepared.Memory.Context->Pc;
            if (raw)
            {
                prepared.Memory.Flush(childCounters, owner.tierCounters, status, repeats, repeatFuel);
                if (repeats != 0) RecordCompletedUnattached(parent, in prepared, childCounters, repeats);
                owner.tierCounters.Entered();
                childCounters.TierEntry();
                parent.Activation.Work.Counters.FixedRawTierExit(scalar, status == TierInvocation.FixedRawExit);
            }
            if (scalar && (raw || !prepared.Tier!.FixedWorkEntered))
                parent.Activation.Work.Counters.FixedTierWorklessReturn();
            if (scalar)
            {
                if (ownsReservation)
                {
                    reserved.Lease.Stack.ReleaseReserved(reserved.Lease);
                    ownsReservation = false;
                    parent.Activation.Work.Counters.FixedUnattachedReturn();
                }
                else
                {
                    if (!raw) owner.RecordFixedTierExit(child!, prepared.Tier!, status, readResult: false);
                    child!.Invocation.Finished = true;
                    current = null;
                    owner.ReleaseGuestActivation(child, child.Invocation.Arguments);
                }
                int unbound = raw ? prepared.DeferredLocations : prepared.Tier!.FixedCaptureLocations;
                if (unbound != 0)
                    parent.Activation.Work.Counters.FixedTierCaptureBindings(avoided: unbound);
                parent.Activation.Work.Counters.FixedGuestReturn();
                parent.Activation.Work.Counters.FixedTierScalarReturn();
                parent.Memory.Context->FixedResultBits = bits;
            }
            else
            {
                if (ownsReservation)
                {
                    child = current = owner.AttachUnattached(parent, in reserved);
                    ownsReservation = false;
                    parent.Activation.Work.Counters.FixedUnattachedAttach();
                    if (repeats != 0 && parent.Observed) parent.Activation.Work.Counters.FixedRepeatedAttachment();
                }
                if (raw)
                {
                    tier = owner.RentTierInvocation();
                    owner.BindFixedTier(tier, parent, child!, prepared.Code, prepared.DeferredLocations);
                    tier.Pc = pc;
                    tier.ReturnNumber = BitConverter.UInt64BitsToDouble(bits);
                    tier.ReturnBool = bits != 0;
                }
                work = BindFixedWork(tier!, FixedWorkBoundary.Resume);
                if (!raw) owner.RecordFixedTierExit(child!, tier!, status, readResult: true);
                owner.ResumeFixedTier(child!, tier!, prepared.Code, status, parent.Token, ref tier);
                NativeDatum result = owner.FinishFixedGuest(prepared.Generation, parent.Activation, ref current, ref tier, parent.Token);
                parent.Activation.Frame.SetDatum(destination, result);
            }
            parent.Activation.Work.Counters.FixedTierCall();
            CompleteFrameCall(parent, prepared.CallPc, fused: true, prepared.Elided);
            return scalar ? 1 : 0;
        }
        catch (Exception failure)
        {
            NativeWorkScope? previous = owner.currentWork.Value;
            try { parent.Failure = owner.UnwindGuestTo(current, parent.Activation, failure, parent.Token); }
            finally { owner.currentWork.Value = previous; }
            return TierInvocation.ErrorExit;
        }
        finally
        {
            work.Restore();
            if (ownsReservation) reserved.Lease.Stack.ReleaseReserved(reserved.Lease);
            if (prepared.Child is null) reserved = default;
            if (raw) prepared.Memory.ClearReferences();
            if (tier is not null) owner.ReturnTierInvocation(tier);
        }
    }

    private bool CanPublishFixedScalar(TierInvocation parent, Activation? child, int destination, int status)
    {
        NativeKind kind = status switch
        {
            TierInvocation.ReturnNumberExit => NativeKind.Number,
            TierInvocation.ReturnBoolExit => NativeKind.Bool,
            _ => NativeKind.Null
        };
        return kind != NativeKind.Null && parent.Executable.FixedScalarResult(destination) == kind
            && (child is null || (!child.OwnsWork || !child.Work.HasWork) && !child.HasActiveIterators
                && child.Call is null && child.Tail is null)
            && !memory.HasPending(parent.Activation.Frame.Allocation);
    }

    private void RecordFixedTierExit(Activation child, TierInvocation invocation, int status, bool readResult)
    {
        try
        {
            invocation.Memory.Flush(child.Work.Counters, tierCounters, status);
            tierCounters.Entered();
            child.Work.Counters.TierEntry();
            if (readResult && status == TierInvocation.ReturnNumberExit)
                invocation.ReturnNumber = invocation.Memory.SlotNumber(child.Execution.Instructions[invocation.Pc].A);
            else if (readResult && status == TierInvocation.ReturnBoolExit)
                invocation.ReturnBool = invocation.Memory.SlotBool(child.Execution.Instructions[invocation.Pc].A);
        }
        finally
        {
            invocation.FlushBridge();
            invocation.Memory.ReleasePins();
        }
    }

    private void ResumeFixedTier(Activation child, TierInvocation invocation, NativeCompiledFunction code, int status,
        CancellationToken token, ref TierInvocation? cached)
    {
        NativeExecutionFunction executable = child.Execution;
        int pc = invocation.Pc;
        if (status == TierInvocation.FixedRawExit)
        {
            child.HeapCaller!.Work.Counters.FixedTierResume();
            RunActivationFromSync(child, pc, code, false, true, false, profile is not null, token, ref cached);
            return;
        }
        if (status == TierInvocation.PollExit)
        {
            tierCounters.Poll();
            token.ThrowIfCancellationRequested();
            if (guestHeapWorker != 0 && guestHeap.WantsActiveCollection) CollectActiveGuestHeap(child, pc);
            child.HeapCaller!.Work.Counters.FixedTierResume();
            RunActivationFromSync(child, pc, code, false, true, false, profile is not null, token, ref cached);
            return;
        }

        TierOutcome outcome = ResolveTierStatus(invocation, child, executable, code, pc, status);
        if (!outcome.Returned)
        {
            child.HeapCaller!.Work.Counters.FixedTierResume();
            RunActivationFromSync(child, outcome.Next, outcome.ExecuteBaseline ? null : code,
                false, true, false, profile is not null, token, ref cached);
        }
    }
}
