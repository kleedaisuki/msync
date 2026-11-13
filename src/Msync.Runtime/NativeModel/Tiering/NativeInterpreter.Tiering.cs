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
    public NativeTierStatistics TierStatistics => tierCounters.Snapshot();

    internal static unsafe nint TierBridgeAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&TierBridge;
    internal static unsafe nint TierNumberAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, double, int>)&TierNumber;
    internal static unsafe nint TierBoolAddress => (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int>)&TierBool;

    private NativeExecutionFunction ExecutionFunction(NativeImage selected, int index) =>
        executableImages.GetValue(selected, image => new(image, tierOptions)).Function(index);

    private NativeSiteShape? ObserveShape(NativeImage image, NativeFrame frame, NativeInstruction instruction)
    {
        if (instruction.Op is not (NativeOp.Call or NativeOp.ProjectAddress or NativeOp.Binary or NativeOp.Unary)) return null;
        NativeDatum first = frame.GetDatum(instruction.A);
        if (!first.IsReady) return null;
        if (instruction.Op == NativeOp.Call)
        {
            if (first.Value.Kind != NativeKind.Function) return null;
            NativeFunction callable = first.Value.AsFunction();
            return new(NativeKind.Function, Function: callable.Function, NativeName: callable.NativeName);
        }
        if (instruction.Op == NativeOp.ProjectAddress)
        {
            if (first.Value.Kind != NativeKind.Address) return null;
            NativeAddress address = first.Value.AsAddress();
            foreach (NativeProjectionCase item in image.Program.Projections[instruction.B].Cases)
                if (image.HasLayout(item.Layout, address)) return new(NativeKind.Address, Layout: item.Layout);
            return null;
        }
        if (instruction.Op == NativeOp.Unary) return new(first.Value.Kind);
        NativeDatum second = frame.GetDatum(instruction.B);
        return second.IsReady ? new(first.Value.Kind, second.Value.Kind) : null;
    }

    private void ObserveTier(NativeExecutionFunction executable, NativeFrame frame, int pc)
    {
        if (!executable.NeedsProfiling) return;
        NativeInstruction instruction = executable.Metadata[pc];
        if (instruction.Op == NativeOp.Unary) return;
        if (instruction.Op == NativeOp.ProjectAddress && image.Program.Projections[instruction.B].Cases.Count == 1) return;
        if (instruction.Op == NativeOp.Binary)
        {
            NativeCodeType first = image.Program.Types[executable.Definition.Slots[instruction.A].Type];
            NativeCodeType second = image.Program.Types[executable.Definition.Slots[instruction.B].Type];
            if (first.Kind == TypeKind.Number && second.Kind == TypeKind.Number) return;
        }
        NativeSiteShape? shape = ObserveShape(image, frame, instruction);
        if (shape is not null) executable.Observe(pc, shape);
    }

    private async ValueTask<TierOutcome> RunTierAsync(Activation activation, NativeExecutionFunction executable,
        NativeCompiledFunction version, int pc, CancellationToken token)
    {
        using TierInvocation invocation = new(this, activation, executable, version, token);
        int status;
        try { status = version.Run(invocation.Handle, pc); }
        catch (ObjectDisposedException) { return new(pc, false, true); }
        invocation.Memory.Flush(activation.Work.Counters, tierCounters);
        tierCounters.Entered();
        activation.Work.Counters.TierEntry();
        if (status == TierInvocation.TailExit) return new(invocation.Pc, false, true);
        if (status == TierInvocation.PollExit)
        {
            tierCounters.Poll();
            token.ThrowIfCancellationRequested();
            return new(invocation.Pc, false);
        }
        if (invocation.Failure is { } failure) ThrowTierFailure(activation, executable.Diagnostic(invocation.Pc), invocation.Pc, failure);
        if (status == TierInvocation.GuardExit)
        {
            tierCounters.GuardMiss();
            activation.Work.Counters.TierGuardMiss();
            executable.Invalidate(version);
            ObserveTier(executable, activation.Frame, invocation.Pc);
            return new(invocation.Pc, false, true);
        }
        if (status == TierInvocation.AwaitExit)
        {
            invocation.Memory.ReleasePins();
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
        return new(executable.Metadata.Length, invocation.Returned);
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
        try
        {
            do
            {
                state.Pc = pc;
                polls++;
                state.Token.ThrowIfCancellationRequested();
                NativeInstruction instruction = state.Executable.Metadata[pc];
                if (instruction.Op == NativeOp.Call && instruction.Result >= 0
                    && state.Owner.IsTailCall(state.Activation, pc)) return TierInvocation.TailExit;
                NativeSiteShape? guard = state.Version.Guards[pc];
                if (guard is not null && guard != state.Owner.ObserveShape(state.Owner.image, state.Activation.Frame, instruction)) return TierInvocation.GuardExit;
                if (state.Observed) state.Executable.Visit(state.Activation.Work.Counters, pc);
                visited += state.Executable.Instructions[pc].Span;
                ValueTask<NativeStep> pending = state.Owner.ExecuteLinked(state.Activation, state.Executable.Instructions[pc], pc, state.Token);
                if (!pending.IsCompleted)
                {
                    state.Pending = pending;
                    return TierInvocation.AwaitExit;
                }
                NativeStep step = pending.GetAwaiter().GetResult();
                state.Returned = step.Returned;
                if (step.Returned) return TierInvocation.ReturnExit;
                if (state.Observed) state.Activation.Work.Counters.Edge(pc + state.Executable.Instructions[pc].Span - 1, step.Next);
                if (step.Next <= pc) state.Executable.Backedge();
                if (instruction.Op == NativeOp.BindLocation) state.Memory.Refresh(instruction.A);
                state.Memory.RefreshPending(state.Executable.Instructions[pc].Result,
                    instruction.Op is NativeOp.Move or NativeOp.Drop ? instruction.A : -1);
                pc = step.Next;
                if (polls >= 256) FlushBridge(state, ref visited, ref polls);
            } while ((uint)pc < (uint)state.Executable.Instructions.Length
                && state.Version.Operations[pc] == NativeFastOperation.Bridge);
            return pc;
        }
        catch (Exception error) { state.Failure = error; return TierInvocation.ErrorExit; }
        finally
        {
            FlushBridge(state, ref visited, ref polls);
        }
    }

    private static void FlushBridge(TierInvocation state, ref long visited, ref long polls)
    {
        if (polls == 0) return;
        state.Owner.tierCounters.BridgeBatch(visited, polls);
        if (state.Observed) state.Activation.Work.Counters.TierBridgeBatch(visited);
        (visited, polls) = (0, 0);
    }

    private static unsafe TierInvocation TierState(nint context) =>
        (TierInvocation)GCHandle.FromIntPtr(((NativeTierContext*)context)->Handle).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierNumber(nint handle, int pc, double result) => StoreTierResult(handle, pc, NativeValue.FromNumber(result));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TierBool(nint handle, int pc, int result) => StoreTierResult(handle, pc, NativeValue.FromBool(result != 0));

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
        internal const int ReturnExit = -1, AwaitExit = -2, GuardExit = -3, ErrorExit = -4, PollExit = -5, TailExit = -6;
        private readonly GCHandle handle;
        internal TierInvocation(NativeInterpreter owner, Activation activation, NativeExecutionFunction executable,
            NativeCompiledFunction version, CancellationToken token)
        {
            (Owner, Activation, Executable, Version, Token) = (owner, activation, executable, version, token);
            handle = GCHandle.Alloc(this);
            try { Memory = new(activation.Frame, owner.memory, executable, GCHandle.ToIntPtr(handle), version); }
            catch { handle.Free(); throw; }
        }
        internal nint Handle => (nint)Memory.Context;
        internal NativeTierMemory Memory { get; }
        internal NativeInterpreter Owner { get; }
        internal bool Observed => Owner.profile is not null;
        internal Activation Activation { get; }
        internal NativeExecutionFunction Executable { get; }
        internal NativeCompiledFunction Version { get; }
        internal CancellationToken Token { get; }
        internal ValueTask<NativeStep> Pending;
        internal Exception? Failure;
        internal int Pc { get => Memory.Context->Pc; set => Memory.Context->Pc = value; }
        internal bool Returned;
        public void Dispose() { Memory.Dispose(); handle.Free(); }
    }
}
