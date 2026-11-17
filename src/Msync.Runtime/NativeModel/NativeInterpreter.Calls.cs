using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal readonly record struct GuestCall(int Target, NativeEnvironment? Environment,
        NativeArguments Arguments, int Destination, int Pc);

    internal readonly record struct GuestTailOrigin(NativeFunctionCode Caller, NativeInstruction Instruction, int Pc);

    internal struct GuestInvocation
    {
        internal NativeEnvironment? Environment;
        internal NativeArguments Arguments;
        internal NativeWorkScope? Parent;
        internal List<TailReturn>? Returns;
        internal GuestTailOrigin? Origin;
        internal int ResumePc, Destination, CallPc;
        internal bool Started, Finished;
    }

    private NativeDatum RunFunction(int index, NativeEnvironment? environment, NativeArguments arguments, CancellationToken token)
    {
        using NativeFrameStack? stack = NativeFrameStack.Open();
        NativeFrameStack heapFrames = NativeFrameStack.Current;
        Generation? previousGeneration = programUpdated ? activeGeneration.Value : generation;
        Generation selected = previousGeneration ?? generation;
        NativeWorkScope? previousWork = currentWork.Value;
        NativeWorkScope? installedWork = previousWork;
        bool switchGeneration = !ReferenceEquals(selected, previousGeneration);
        Activation? boundary = heapFrames.Active;
        Activation? current = null;
        int depth = 0;
        TierInvocation? tier = null;
        if (switchGeneration) activeGeneration.Value = selected;
        heapFrames.DriverDepth++;
        try
        {
            current = EnterGuest(selected, index, environment, arguments, previousWork);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Activation activation = current!;
                ref GuestInvocation invocation = ref activation.Invocation;
                if (!ReferenceEquals(installedWork, activation.Work)) currentWork.Value = installedWork = activation.Work;
                bool entering = !invocation.Started;
                invocation.Started = true;
                RunActivation(activation, token, ref tier, invocation.ResumePc, entering);
                NativeDatum result = activation.Result;
                if (activation.Call is { } call)
                {
                    activation.Call = null;
                    invocation.CallPc = call.Pc;
                    invocation.ResumePc = call.Pc + activation.Execution.Instructions[call.Pc].Span;
                    invocation.Destination = call.Destination;
                    depth++;
                    if (profile is not null) activation.Work.Counters.GuestCall(depth);
                    current = EnterGuest(selected, call.Target, call.Environment, call.Arguments, activation.Work);
                    continue;
                }
                invocation.Finished = true;
                FinishInvocation(activation, invocation.Environment, invocation.Arguments, result, token);
                NativeFunctionCode function = activation.Function;
                RuntimeProfile.NativeFrameCounters counters = activation.Work.Counters;
                List<TailReturn>? returns = invocation.Returns;
                NativeWorkScope? parent = invocation.Parent;
                Activation? caller = activation.HeapCaller;
                TailInvocation? tail = CompleteActivation(selected.Image, activation, invocation.Arguments);
                current = ReferenceEquals(caller, boundary) ? null : caller;
                if (tail is { } next)
                {
                    if (profile is not null) (returns ??= []).Add(new(counters, function, next.Pc, next.Plan));
                    current = EnterGuest(selected, next.Target, next.Environment, next.Arguments, parent);
                    current.Invocation.Returns = returns;
                    current.Invocation.Origin = new(next.Caller, next.Instruction, next.Pc);
                    continue;
                }
                if (returns is not null)
                    for (int pending = returns.Count - 1; pending >= 0; pending--) returns[pending].Complete();
                if (current is null) return result;
                depth--;
                ref GuestInvocation resume = ref current.Invocation;
                int destination = resume.Destination;
                resume.Destination = -1;
                current.Frame.SetDatum(destination, result);
                if (profile is not null)
                {
                    current.Work.Counters.GuestResume();
                    current.Work.Counters.Edge(resume.ResumePc - 1, resume.ResumePc);
                }
            }
        }
        catch (Exception failure)
        {
            Exception error = failure;
            while (current is not null && !ReferenceEquals(current, boundary))
            {
                Activation? caller = current.HeapCaller;
                error = UnwindGuest(current, error, token);
                current = caller;
            }
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            if (tier is not null) ReturnTierInvocation(tier);
            currentWork.Value = previousWork;
            if (switchGeneration) activeGeneration.Value = previousGeneration;
            heapFrames.DriverDepth--;
        }
    }

    private Activation EnterGuest(Generation selected, int index, NativeEnvironment? environment,
        NativeArguments arguments, NativeWorkScope? parent)
    {
        Activation activation = EnterActivation(selected, index, environment, arguments, parent);
        activation.Invocation = new() { Environment = environment, Arguments = arguments, Parent = parent, Destination = -1 };
        return activation;
    }

    private Activation EnterActivation(Generation selected, int index, NativeEnvironment? environment,
        NativeArguments arguments, NativeWorkScope? parent)
    {
        NativeFunctionCode function = selected.Image.Program.Functions[index];
        NativeExecutionFunction executable = ExecutionFunction(selected.Image, index);
        bool ownsWork = profile is not null || !executable.IsPureSynchronous;
        NativeWorkScope owner = ownsWork
            ? new(parent ?? executionWork) { Counters = profile?.EnterNative(selected.Image.Program, index) ?? default }
            : parent ?? executionWork;
        NativeFrame frame = executable.EnterFrame(environment, arguments, memory,
            index == selected.Image.Program.EntryPoint && selected.Image.Program.PreserveLocals ? sessionLocations : null,
            guestHeap, owner.Counters, out bool reused);
        bool reusedActivation = frame.Activation is not null;
        Activation activation = frame.Activation ??= new(index, function, frame, executable);
        activation.Enter(owner, ownsWork);
        if (profile is not null)
        {
            owner.Counters.CallActivation(reusedActivation);
            owner.Counters.CallStorage(arguments.BorrowedCount, arguments.Count - arguments.BorrowedCount,
                arguments.IsSnapshot, arguments.SnapshotAllocated, reused);
            frame.RecordHeapLocations(owner.Counters);
            if (frame.StackLease is { } lease)
                owner.Counters.StackFrame(lease.Bytes, lease.Roots, lease.NativeBytes);
        }
        NativeFrameStack.Current.Enter(activation);
        return activation;
    }

    private void RunDirectFunction(Activation caller, int index, NativeEnvironment? environment,
        NativeArguments arguments, NativeAddress destination, CancellationToken token)
    {
        Generation selected = programUpdated ? activeGeneration.Value ?? generation : generation;
        if (environment is null && selected.Image.Program.Functions[index] is { CaptureCount: 0, SsaBody: { } body }
            && TryRunSsaDirect(selected.Image, index, body, arguments, destination, token)) return;
        Activation activation = EnterActivation(selected, index, environment, arguments, caller.Work);
        activation.ReturnDestination = destination;
        TierInvocation? tier = null;
        bool ownsWork = activation.OwnsWork;
        bool finished = false;
        if (ownsWork) currentWork.Value = activation.Work;
        try
        {
            RunActivation(activation, token, ref tier);
            finished = true;
            if (ownsWork && activation.Work.HasWork)
                FinishInvocation(activation, environment, arguments, memory.Read(destination), token);
        }
        catch (Exception error)
        {
            if (!finished) error = FinishFailedInvocation(activation, environment, arguments, error, token);
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            if (tier is not null) ReturnTierInvocation(tier);
            ReleaseActivation(activation, arguments);
            if (ownsWork) currentWork.Value = caller.Work;
        }
    }

    private void ReleaseActivation(Activation activation, NativeArguments arguments)
    {
        NativeExecutionFunction executable = activation.Execution;
        NativeFrame frame = activation.Frame;
        NativeFrameStack.Current.Leave(activation);
        activation.Clear();
        executable.ReturnTailArguments(arguments.Storage, memory);
        executable.LeaveFrame(frame, memory);
    }

    private void ReleaseGuestActivation(Activation activation, NativeArguments arguments)
    {
        activation.Invocation = default;
        ReleaseActivation(activation, arguments);
    }

    private void FinishInvocation(Activation activation, NativeEnvironment? environment,
        NativeArguments arguments, NativeDatum result, CancellationToken token, bool recovering = false)
    {
        if (activation.OwnsWork && (activation.Work.HasWork || activation.HasActiveIterators))
            Demand(FinishOwnedWorkAsync(activation, environment, arguments, result, token, recovering));
    }

    private Exception FinishFailedInvocation(Activation activation, NativeEnvironment? environment,
        NativeArguments arguments, Exception error, CancellationToken token)
    {
        try { FinishInvocation(activation, environment, arguments, new(NativeValue.Null), token, recovering: true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception cleanup)
        {
            // Demand and ownership cleanup may observe the same failed job.
            if (ReferenceEquals(RootCause(error), RootCause(cleanup))) return error;
            var failures = new AggregateException("Guest execution and cleanup failed.", error, cleanup);
            return error is MsyncException primary && cleanup is MsyncException
                ? new MsyncException("Guest execution and cleanup failed.", primary.Span, failures)
                : failures;
        }
        return error;

        static Exception RootCause(Exception failure)
        {
            while (failure is MsyncException { InnerException: { } cause }) failure = cause;
            return failure;
        }
    }

    private Exception UnwindGuest(Activation activation, Exception error, CancellationToken token)
    {
        ref GuestInvocation invocation = ref activation.Invocation;
        currentWork.Value = activation.Work;
        if (invocation.Destination >= 0)
            error = WrapStepFailure(activation.Execution, activation.Function, invocation.CallPc, error);
        if (invocation.Origin is { } origin)
        {
            try { ThrowTierFailure(origin.Caller, origin.Instruction, origin.Pc, error); }
            catch (Exception wrapped) { error = wrapped; }
        }
        if (!invocation.Finished)
            error = FinishFailedInvocation(activation, invocation.Environment, invocation.Arguments, error, token);
        ReleaseGuestActivation(activation, invocation.Arguments);
        return error;
    }
}
