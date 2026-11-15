using System.Runtime.ExceptionServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal readonly record struct GuestCall(int Target, NativeEnvironment? Environment,
        NativeArguments Arguments, int Destination, int Pc);

    private struct GuestInvocation
    {
        internal Activation Activation;
        internal NativeEnvironment? Environment;
        internal NativeArguments Arguments;
        internal NativeWorkScope? Parent;
        internal List<TailReturn>? Returns;
        internal TailInvocation? Origin;
        internal int ResumePc, Destination, CallPc;
        internal bool Started, Finished, Released;
    }

    private NativeDatum RunFunction(int index, NativeEnvironment? environment, NativeArguments arguments, CancellationToken token)
    {
        Generation? previousGeneration = programUpdated ? activeGeneration.Value : generation;
        Generation selected = previousGeneration ?? generation;
        NativeWorkScope? previousWork = currentWork.Value;
        NativeWorkScope? installedWork = previousWork;
        bool switchGeneration = !ReferenceEquals(selected, previousGeneration);
        List<GuestInvocation>? callers = null;
        TierInvocation? tier = null;
        GuestInvocation current = default;
        if (switchGeneration) activeGeneration.Value = selected;
        try
        {
            current = EnterGuest(selected, index, environment, arguments, previousWork);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Activation activation = current.Activation;
                if (!ReferenceEquals(installedWork, activation.Work)) currentWork.Value = installedWork = activation.Work;
                bool entering = !current.Started;
                current.Started = true;
                RunActivation(activation, token, ref tier, current.ResumePc, entering);
                NativeDatum result = activation.Result;
                if (activation.Call is { } call)
                {
                    activation.Call = null;
                    current.CallPc = call.Pc;
                    current.ResumePc = call.Pc + activation.Execution.Instructions[call.Pc].Span;
                    current.Destination = call.Destination;
                    (callers ??= []).Add(current);
                    if (profile is not null) activation.Work.Counters.GuestCall(callers.Count);
                    current = default;
                    current = EnterGuest(selected, call.Target, call.Environment, call.Arguments, activation.Work);
                    continue;
                }
                FinishGuest(ref current, result, token);
                NativeFunctionCode function = activation.Function;
                RuntimeProfile.NativeFrameCounters counters = activation.Work.Counters;
                TailInvocation? tail = CompleteActivation(selected.Image, activation, current.Arguments);
                current.Released = true;
                if (tail is { } next)
                {
                    List<TailReturn>? returns = current.Returns;
                    if (profile is not null) (returns ??= []).Add(new(counters, function, next.Pc, next.Plan));
                    NativeWorkScope? parent = current.Parent;
                    current = EnterGuest(selected, next.Target, next.Environment, next.Arguments, parent);
                    current.Returns = returns;
                    current.Origin = next;
                    continue;
                }
                if (current.Returns is { } completed)
                    for (int pending = completed.Count - 1; pending >= 0; pending--) completed[pending].Complete();
                if (callers is not { Count: > 0 }) return result;
                current = callers[^1];
                callers.RemoveAt(callers.Count - 1);
                current.Activation.Frame.SetDatum(current.Destination, result);
                if (profile is not null)
                {
                    current.Activation.Work.Counters.GuestResume();
                    current.Activation.Work.Counters.Edge(current.ResumePc - 1, current.ResumePc);
                }
            }
        }
        catch (Exception failure)
        {
            Exception error = UnwindGuest(ref current, failure, token);
            if (callers is not null)
                for (int caller = callers.Count - 1; caller >= 0; caller--)
                {
                    current = callers[caller];
                    error = WrapStepFailure(current.Activation.Execution, current.Activation.Function, current.CallPc, error);
                    error = UnwindGuest(ref current, error, token);
                }
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            if (tier is not null) ReturnTierInvocation(tier);
            currentWork.Value = previousWork;
            if (switchGeneration) activeGeneration.Value = previousGeneration;
        }
    }

    private GuestInvocation EnterGuest(Generation selected, int index, NativeEnvironment? environment,
        NativeArguments arguments, NativeWorkScope? parent) => new()
    {
        Activation = EnterActivation(selected, index, environment, arguments, parent),
        Environment = environment, Arguments = arguments, Parent = parent
    };

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
            index == selected.Image.Program.EntryPoint && selected.Image.Program.PreserveLocals ? sessionLocations : null, out bool reused);
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
                owner.Counters.StackFrame(lease.Bytes, lease.Roots, lease.Grew);
        }
        return activation;
    }

    private void RunDirectFunction(Activation caller, int index, NativeEnvironment? environment,
        NativeArguments arguments, NativeAddress destination, CancellationToken token)
    {
        Generation selected = programUpdated ? activeGeneration.Value ?? generation : generation;
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
        activation.Clear();
        executable.ReturnTailArguments(arguments.Storage, memory);
        executable.LeaveFrame(frame, memory);
    }

    private void FinishGuest(ref GuestInvocation invocation, NativeDatum result, CancellationToken token)
    {
        invocation.Finished = true;
        FinishInvocation(invocation.Activation, invocation.Environment, invocation.Arguments, result, token);
    }

    private void FinishInvocation(Activation activation, NativeEnvironment? environment,
        NativeArguments arguments, NativeDatum result, CancellationToken token)
    {
        if (activation.OwnsWork && (activation.Work.HasWork || activation.HasActiveIterators))
            Demand(FinishOwnedWorkAsync(activation, environment, arguments, result, token));
    }

    private Exception FinishFailedInvocation(Activation activation, NativeEnvironment? environment,
        NativeArguments arguments, Exception error, CancellationToken token)
    {
        try { FinishInvocation(activation, environment, arguments, new(NativeValue.Null), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception cleanup)
        {
            // Demand and ownership cleanup may observe the same failed job.
            if (!ReferenceEquals(error, cleanup))
                return new AggregateException("Guest execution and cleanup failed.", error, cleanup);
        }
        return error;
    }

    private Exception UnwindGuest(ref GuestInvocation invocation, Exception error, CancellationToken token)
    {
        if (invocation.Activation is not { } activation || invocation.Released) return error;
        currentWork.Value = activation.Work;
        if (invocation.Origin is { } origin)
        {
            try { ThrowTierFailure(origin.Caller, origin.Instruction, origin.Pc, error); }
            catch (Exception wrapped) { error = wrapped; }
        }
        if (!invocation.Finished)
            error = FinishFailedInvocation(activation, invocation.Environment, invocation.Arguments, error, token);
        ReleaseActivation(activation, invocation.Arguments);
        invocation.Released = true;
        return error;
    }
}
