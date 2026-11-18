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
        bool switchGeneration = !ReferenceEquals(selected, previousGeneration);
        Activation? boundary = heapFrames.Active;
        Activation? current = null;
        TierInvocation? tier = null;
        if (switchGeneration) activeGeneration.Value = selected;
        heapFrames.DriverDepth++;
        try
        {
            current = EnterGuest(selected, index, environment, arguments, previousWork);
            return DriveGuest(selected, boundary, ref current, ref tier, previousWork, token);
        }
        catch (Exception failure)
        {
            ExceptionDispatchInfo.Capture(UnwindGuestTo(current, boundary, failure, token)).Throw();
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

    private NativeDatum DriveGuest(Generation selected, Activation? boundary, ref Activation? current,
        ref TierInvocation? tier, NativeWorkScope? installedWork, CancellationToken token, bool completedStep = false)
    {
        int depth = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Activation activation = current!;
            ref GuestInvocation invocation = ref activation.Invocation;
            if (!ReferenceEquals(installedWork, activation.Work)) currentWork.Value = installedWork = activation.Work;
            if (!completedStep)
            {
                bool entering = !invocation.Started;
                invocation.Started = true;
                RunActivation(activation, token, ref tier, invocation.ResumePc, entering);
            }
            completedStep = false;
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

    // The fixed CALL owns one physical activation. The driver takes it
    // over only if the body produces a nested or tail guest call.
    private NativeDatum RunFixedGuest(Activation caller, NativeExecutionFunction executable, NativeEnvironment? environment,
        NativeArguments arguments, CancellationToken token)
    {
        Generation selected = programUpdated ? activeGeneration.Value ?? generation : generation;
        NativeWorkScope? previousWork = currentWork.Value;
        Activation? current = null;
        TierInvocation? tier = null;
        try
        {
            Activation child = current = EnterGuest(selected, executable, environment, arguments, previousWork);
            currentWork.Value = child.Work;
            child.Invocation.Started = true;
            RunActivation(child, token, ref tier);
            return FinishFixedGuest(selected, caller, ref current, ref tier, token);
        }
        catch (Exception failure)
        {
            ExceptionDispatchInfo.Capture(UnwindGuestTo(current, caller, failure, token)).Throw();
            throw;
        }
        finally
        {
            if (tier is not null) ReturnTierInvocation(tier);
            currentWork.Value = previousWork;
        }
    }

    private NativeDatum FinishFixedGuest(Generation selected, Activation caller, ref Activation? current,
        ref TierInvocation? tier, CancellationToken token)
    {
        Activation child = current!;
        if (child.Call is not null || child.Tail is not null)
        {
            caller.Work.Counters.FixedGuestHandoff();
            return DriveGuest(selected, caller, ref current, ref tier, child.Work, token, completedStep: true);
        }
        NativeDatum result = child.Result;
        ref GuestInvocation invocation = ref child.Invocation;
        invocation.Finished = true;
        FinishInvocation(child, invocation.Environment, invocation.Arguments, result, token);
        current = null;
        ReleaseGuestActivation(child, invocation.Arguments);
        caller.Work.Counters.FixedGuestReturn();
        return result;
    }

    private Exception UnwindGuestTo(Activation? current, Activation? boundary, Exception error, CancellationToken token)
    {
        while (current is not null && !ReferenceEquals(current, boundary))
        {
            Activation? parent = current.HeapCaller;
            error = UnwindGuest(current, error, token);
            current = parent;
        }
        return error;
    }

    private Activation EnterGuest(Generation selected, int index, NativeEnvironment? environment,
        NativeArguments arguments, NativeWorkScope? parent) =>
        EnterGuest(selected, ExecutionFunction(selected.Image, index), environment, arguments, parent);

    private Activation EnterGuest(Generation selected, NativeExecutionFunction executable, NativeEnvironment? environment,
        NativeArguments arguments, NativeWorkScope? parent, bool deferWork = false, int deferredLocationCaptures = 0)
    {
        Activation activation = EnterActivation(selected.Image, executable, environment, arguments, parent, deferWork, deferredLocationCaptures);
        activation.Invocation = new() { Environment = environment, Arguments = arguments, Parent = parent, Destination = -1 };
        return activation;
    }

    private Activation EnterActivation(NativeImage selected, NativeExecutionFunction executable, NativeEnvironment? environment,
        NativeArguments arguments, NativeWorkScope? parent, bool deferWork = false, int deferredLocationCaptures = 0)
    {
        int index = executable.Index;
        NativeFunctionCode function = executable.Definition;
        bool ownsWork = profile is not null || !deferWork && !executable.IsPureSynchronous;
        NativeWorkScope owner = ownsWork
            ? new(parent ?? executionWork) { Counters = profile?.EnterNative(selected.Program, index) ?? default }
            : parent ?? executionWork;
        NativeFrame frame = executable.EnterFrame(environment, arguments, memory,
            index == selected.Program.EntryPoint && selected.Program.PreserveLocals ? sessionLocations : null,
            guestHeap, owner.Counters, out bool reused, deferredLocationCaptures);
        bool reusedActivation = frame.Activation is not null;
        Activation activation = frame.Activation ??= new(index, function, frame, executable);
        activation.Enter(owner, ownsWork);
        if (profile is not null)
        {
            owner.Counters.CallActivation(reusedActivation);
            owner.Counters.CallStorage(arguments.BorrowedCount,
                frame.StackLease?.Window == NativeWindowDisposition.Adopted ? arguments.StagedMoves?.Length ?? 0 : arguments.CopiedCount,
                arguments.IsSnapshot, arguments.SnapshotExtent, reused);
            frame.RecordHeapLocations(owner.Counters);
            if (frame.StackLease is { } lease)
                owner.Counters.StackFrame(lease.Bytes, lease.Roots, lease.NativeBytes, lease.NativeReferences, lease.SegmentReentry);
        }
        NativeFrameStack.Current.Enter(activation);
        return activation;
    }

    private void RunDirectFunction(Activation caller, NativeExecutionFunction executable, NativeEnvironment? environment,
        NativeArguments arguments, NativeAddress destination, int pc, CancellationToken token)
    {
        caller.Work.Counters.Call(pc, executable.Index);
        if (profile is not null) caller.Work.Counters.GuestDirectCall(executable.IsContextFree);
        NativeImage selected = image;
        if (environment is null && executable.Definition is { CaptureCount: 0, SsaBody: { } body }
            && TryRunSsaDirect(selected, executable.Index, body, arguments, destination, token)) return;
        Activation activation = EnterActivation(selected, executable, environment, arguments, caller.Work);
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
