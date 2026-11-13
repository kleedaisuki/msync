using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private readonly NativeTierOptions tierOptions;
    private readonly NativeTierCounters tierCounters = new();
    private readonly ConditionalWeakTable<NativeImage, NativeExecutionImage> executableImages = new();
    private NativeExecutionImage? lastExecutionImage;
    private readonly ConcurrentStack<TierInvocation> tierPool = new();
    public NativeTierStatistics TierStatistics => tierCounters.Snapshot();

    private void DisposeTierPool()
    {
        while (tierPool.TryPop(out TierInvocation? item)) item.Dispose();
    }

    private TierInvocation RentTierInvocation() => tierPool.TryPop(out TierInvocation? pooled) ? pooled : new TierInvocation();

    private void ReturnTierInvocation(TierInvocation invocation)
    {
        invocation.ClearReferences();
        if (!disposed) tierPool.Push(invocation);
        else invocation.Dispose();
    }

    internal static unsafe nint TierBridgeAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&TierBridge;
    internal static unsafe nint TierNumberAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, double, int>)&TierNumber;
    internal static unsafe nint TierBoolAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int>)&TierBool;
    internal static unsafe nint TierCommitAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&TierCommit;

    private NativeExecutionFunction ExecutionFunction(NativeImage selected, int index)
    {
        NativeExecutionImage? cached = Volatile.Read(ref lastExecutionImage);
        if (cached is null || !ReferenceEquals(cached.Image, selected))
        {
            cached = executableImages.GetValue(selected, image => new(image, tierOptions));
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

    private ValueTask<TierOutcome> RunTier(Activation activation, NativeExecutionFunction executable,
        NativeCompiledFunction version, int pc, CancellationToken token)
    {
        TierInvocation invocation = RentTierInvocation();
        try
        {
            int status = RunTierSync(invocation, activation, executable, version, pc, token);
            if (status == TierInvocation.DisposedExit) return new(new TierOutcome(pc, false, true));
            if (status == TierInvocation.ReturnNumberExit)
            {
                activation.Result = new(NativeValue.FromNumber(invocation.ReturnNumber));
                return new(new TierOutcome(executable.Metadata.Length, true));
            }
            if (status == TierInvocation.ReturnBoolExit)
            {
                activation.Result = new(NativeValue.FromBool(invocation.ReturnBool));
                return new(new TierOutcome(executable.Metadata.Length, true));
            }
            if (status == TierInvocation.TailExit) return new(new TierOutcome(invocation.Pc, false, true));
            if (invocation.Failure is { } failure) ThrowTierFailure(activation, executable.Diagnostic(invocation.Pc), invocation.Pc, failure);
            if (status == TierInvocation.GuardExit)
            {
                tierCounters.GuardMiss();
                activation.Work.Counters.TierGuardMiss();
                executable.Invalidate(version);
                ObserveTier(executable, activation.Frame, invocation.Pc);
                return new(new TierOutcome(invocation.Pc, false, true));
            }
            if (status == TierInvocation.AwaitExit)
            {
                ValueTask<TierOutcome> awaited = AwaitTierAsync(invocation, activation, executable);
                invocation = null!;
                return awaited;
            }
            return new(new TierOutcome(executable.Metadata.Length, invocation.Returned));
        }
        finally
        {
            if (invocation is not null) ReturnTierInvocation(invocation);
        }
    }

    private async ValueTask<TierOutcome> AwaitTierAsync(TierInvocation invocation, Activation activation, NativeExecutionFunction executable)
    {
        try
        {
            tierCounters.Suspended();
            activation.Work.Counters.TierSuspension();
            activation.Work.Counters.Pause();
            NativeStep step;
            try { step = await invocation.Pending; }
            catch (Exception error) { ThrowTierFailure(activation, executable.Diagnostic(invocation.Pc), invocation.Pc, error); throw; }
            finally { activation.Work.Counters.Continue(); }
            if (!step.Returned) activation.Work.Counters.Edge(invocation.Pc, step.Next);
            if (step.Next <= invocation.Pc) executable.Backedge();
            return new(step.Next, step.Returned);
        }
        finally
        {
            ReturnTierInvocation(invocation);
        }
    }

    private unsafe int RunTierSync(TierInvocation invocation, Activation activation, NativeExecutionFunction executable,
        NativeCompiledFunction version, int pc, CancellationToken token)
    {
        NativeAllocation frameAllocation = activation.Frame.Allocation;
        NativeStorage storage = frameAllocation.Storage;
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
        fixed (byte* frameBytes = storage.RawBytes)
        fixed (bool* frameInit = storage.InitializedFlags)
        fixed (byte* ext0Bytes = ext0?.Storage.RawBytes)
        fixed (bool* ext0Init = ext0?.Storage.InitializedFlags)
        fixed (byte* ext1Bytes = ext1?.Storage.RawBytes)
        fixed (bool* ext1Init = ext1?.Storage.InitializedFlags)
        {
            invocation.Reset(this, activation, executable, version, token, frameAllocation, (nint)frameBytes, (nint)frameInit, profile is not null,
                ext0, (nint)ext0Bytes, (nint)ext0Init, ext1, (nint)ext1Bytes, (nint)ext1Init);
            try
            {
                while (true)
                {
                    int status;
                    try { status = version.Run(invocation.Handle, pc); }
                    catch (ObjectDisposedException) { return TierInvocation.DisposedExit; }
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



    private unsafe ValueTask<NativeStep> ExecuteLinked(Activation activation, in NativeExecutionInstruction linked,
        int pc, CancellationToken token) =>
        ((delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, ValueTask<NativeStep>>)linked.Handler)(
            this, activation, linked, pc, token);

    private static void ThrowTierFailure(Activation activation, NativeInstruction instruction, int pc, Exception error)
    {
        if (error is OperationCanceledException) ExceptionDispatchInfo.Capture(error).Throw();
        if (error is MsyncException msync)
        {
            if (msync.Span is null && instruction.Source is not null) throw new MsyncException(msync.Message, instruction.Source, msync);
            ExceptionDispatchInfo.Capture(msync).Throw();
        }
        if (error is InvalidOperationException)
            throw new MsyncException($"{error.Message} Native {instruction.Op} in function '{activation.Function.Name}', instruction {pc}, operands {instruction.A}/{instruction.B}/{instruction.C}, result {instruction.Result}.", instruction.Source, error);
        if (error is ArgumentException or IOException or UnauthorizedAccessException) throw new MsyncException(error.Message, instruction.Source, error);
        ExceptionDispatchInfo.Capture(error).Throw();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int TierBridge(nint handle, int pc)
    {
        TierInvocation state = TierState(handle);
        long visited = 0, polls = 0;
        NativeInterpreter owner = state.Owner;
        Activation activation = state.Activation;
        NativeExecutionFunction executable = state.Executable;
        NativeExecutionInstruction[] instructions = executable.Instructions;
        NativeFastOperation[] operations = state.Version.Operations;
        NativeExecutionOperand[] operands = executable.Operands;
        NativeFrame frame = activation.Frame;
        NativeMemory mem = owner.memory;
        NativeTierMemory tierMemory = state.Memory;
        bool observed = state.Observed;
        CancellationToken token = state.Token;
        try
        {
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
                if (exec.Op == NativeOp.Call && exec.Result >= 0
                    && owner.IsTailCall(activation, pc)) return TierInvocation.TailExit;
                if (observed) executable.Visit(activation.Work.Counters, pc);
                visited += exec.Span;
                int nextPc;
                bool returned = false;
                switch (exec.Op)
                {
                    case NativeOp.Jump:
                        nextPc = exec.A;
                        break;
                    case NativeOp.Return:
                        activation.Result = exec.A < 0 ? default : frame.GetDatum(exec.A);
                        state.Returned = true;
                        return TierInvocation.ReturnExit;
                    case NativeOp.AddressOffset:
                        frame.SetPointer(exec.Result, operands[pc].AddressField(frame.Pointer(exec.A)));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.ProjectAddress:
                        frame.SetPointer(exec.Result, operands[pc].Project(owner.image, frame.Pointer(exec.A)));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.EnvironmentAddress:
                        frame.SetPointer(exec.Result, owner.EnvironmentAddress(frame.GetDatum(exec.A).Value.AsFunction(), executable.Metadata[pc]));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.Copy when operands[pc].Type is { } copyType:
                        mem.Copy(copyType, frame.Location(exec.A), frame.Location(exec.Result));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.Load when exec.Handler != ProjectLoadHandler && exec.Handler != OffsetLoadHandler:
                        mem.Copy(frame.Pointer(exec.A), frame.Location(exec.Result));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.Load when exec.Handler == ProjectLoadHandler:
                        mem.Copy(operands[pc].Project(owner.image, frame.Pointer(exec.A)), frame.Location(exec.Result));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.Load when exec.Handler == OffsetLoadHandler:
                        mem.Copy(operands[pc].AddressField(frame.Pointer(exec.A)), frame.Location(exec.Result));
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.Store when exec.Handler == FrameStoreHandler:
                    {
                        NativeAddress target = frame.Location(exec.Result);
                        if (!mem.TryCopyReady(frame.Location(exec.A), target))
                            mem.Write(target, owner.CoerceStore(frame.GetDatum(exec.A), target.Type));
                        nextPc = pc + exec.Span;
                        break;
                    }
                    case NativeOp.Store when exec.Handler == OffsetStoreHandler:
                    {
                        NativeAddress target = operands[pc].AddressField(frame.Pointer(exec.A));
                        if (!mem.TryCopyReady(frame.Location(exec.B), target))
                            mem.Write(target, owner.CoerceStore(frame.GetDatum(exec.B), target.Type));
                        nextPc = pc + exec.Span;
                        break;
                    }
                    case NativeOp.Store when exec.Handler == ProjectStoreHandler:
                    {
                        NativeAddress target = operands[pc].Project(owner.image, frame.Pointer(exec.A));
                        if (!mem.TryCopyReady(frame.Location(exec.B), target))
                            mem.Write(target, owner.CoerceStore(frame.GetDatum(exec.B), target.Type));
                        nextPc = pc + exec.Span;
                        break;
                    }
                    case NativeOp.Store:
                    {
                        NativeAddress target = frame.Pointer(exec.A);
                        if (!mem.TryCopyReady(frame.Location(exec.B), target))
                            mem.Write(target, owner.CoerceStore(frame.GetDatum(exec.B), target.Type));
                        nextPc = pc + exec.Span;
                        break;
                    }
                    case NativeOp.Allocate when exec.C < 0:
                    {
                        NativeLayout allocLayout = operands[pc].Layout!;
                        if (frame.TryAllocate(pc, out NativeAddress placed))
                        {
                            activation.Work.Counters.Allocation(allocLayout, 1, framePlaced: true);
                            frame.SetPointer(exec.Result, placed);
                        }
                        else
                        {
                            NativeAllocation allocation = new(allocLayout, exec.A, 1) { AllocationSite = Math.Max(0, exec.B) };
                            activation.Work.Counters.Allocation(allocation);
                            frame.SetPointer(exec.Result, allocation.Address);
                        }
                        nextPc = pc + exec.Span;
                        break;
                    }
                    case NativeOp.BindLocation when exec.Handler != OffsetBindHandler:
                        frame.Bind(exec.A, frame.Pointer(exec.B));
                        tierMemory.Refresh(exec.A);
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.BindLocation when exec.Handler == OffsetBindHandler:
                        frame.Bind(exec.A, operands[pc].AddressField(frame.Pointer(exec.B)));
                        tierMemory.Refresh(exec.A);
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.MakeFunction:
                        owner.MakeFunction(frame, executable.Metadata[pc]);
                        nextPc = pc + exec.Span;
                        break;
                    case NativeOp.AdaptFunction:
                        owner.AdaptFunction(frame, executable.Metadata[pc]);
                        nextPc = pc + exec.Span;
                        break;
                    default:
                    {
                        ValueTask<NativeStep> pending = owner.ExecuteLinked(activation, in exec, pc, token);
                        if (!pending.IsCompleted)
                        {
                            state.Pending = pending;
                            return TierInvocation.AwaitExit;
                        }
                        NativeStep step = pending.GetAwaiter().GetResult();
                        returned = step.Returned;
                        if (returned)
                        {
                            state.Returned = true;
                            return TierInvocation.ReturnExit;
                        }
                        nextPc = step.Next;
                        break;
                    }
                }
                if (observed) activation.Work.Counters.Edge(pc + exec.Span - 1, nextPc);
                if (mem.HasAnyPending)
                    tierMemory.RefreshPending(exec.Result, exec.Op is NativeOp.Move or NativeOp.Drop ? exec.A : -1);
                pc = nextPc;
                if (polls >= 256)
                {
                    state.BridgeVisited += visited;
                    state.BridgePolls += polls;
                    visited = 0;
                    polls = 0;
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
            state.BridgeVisited += visited;
            state.BridgePolls += polls;
            if (state.BridgeVisited >= 65536) state.FlushBridge();
        }
    }

    private static unsafe TierInvocation TierState(nint context) =>
        (TierInvocation)GCHandle.FromIntPtr(((NativeTierContext*)context)->Handle).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierNumber(nint handle, int pc, double result) => StoreTierResult(handle, pc, NativeValue.FromNumber(result));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierBool(nint handle, int pc, int result) => StoreTierResult(handle, pc, NativeValue.FromBool(result != 0));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierCommit(nint handle, int pc)
    {
        TierInvocation state = TierState(handle);
        NativeRegisterRegion region = state.Version.Layout.RegionsByPc[pc];
        state.Memory.Commit(region);
        return region.End;
    }

    private static int StoreTierResult(nint context, int pc, NativeValue result)
    {
        TierInvocation state = TierState(context);
        state.Pc = pc;
        try
        {
            state.Owner.tierCounters.Poll();
            state.Token.ThrowIfCancellationRequested();
            NativeExecutionInstruction instruction = state.Executable.Instructions[pc];
            if (state.Observed) state.Executable.Visit(state.Activation.Work.Counters, pc);
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

    private sealed unsafe class TierInvocation : IDisposable
    {
        internal const int ReturnExit = -1, AwaitExit = -2, GuardExit = -3, ErrorExit = -4, PollExit = -5, TailExit = -6, DisposedExit = -7, ReturnNumberExit = -8, ReturnBoolExit = -9;
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
            BridgeFuel = 256;
            BridgeVisited = 0;
            BridgePolls = 0;
            Memory.Reset(activation.Frame, owner.memory, executable, version, pinnedFrame, pinnedBytes, pinnedFlags, observed,
                pinnedExt0, pinnedExt0Bytes, pinnedExt0Flags, pinnedExt1, pinnedExt1Bytes, pinnedExt1Flags);
        }
        internal void FlushBridge()
        {
            long visited = BridgeVisited, polls = BridgePolls;
            if (visited == 0 && polls == 0) return;
            BridgeVisited = 0;
            BridgePolls = 0;
            Owner.tierCounters.BridgeBatch(visited, polls);
            if (Observed) Activation.Work.Counters.TierBridgeBatch(visited);
        }
        internal void ClearReferences()
        {
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
        internal NativeInterpreter Owner { get; private set; } = null!;
        internal bool Observed => Owner.profile is not null;
        internal Activation Activation { get; private set; } = null!;
        internal NativeExecutionFunction Executable { get; private set; } = null!;
        internal NativeCompiledFunction Version { get; private set; } = null!;
        internal CancellationToken Token { get; private set; }
        internal ValueTask<NativeStep> Pending;
        internal Exception? Failure;
        internal int Pc { get => Memory.Context->Pc; set => Memory.Context->Pc = value; }
        internal bool Returned;
        internal int BridgeFuel;
        internal long BridgeVisited;
        internal long BridgePolls;
        internal double ReturnNumber;
        internal bool ReturnBool;
        public void Dispose() { Memory.Dispose(); handle.Free(); }
    }
}
