using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private static bool IsStreamReduction(string name) => name is
        "ranges.front" or "ranges.first" or "ranges.back" or "ranges.last" or "ranges.at"
        or "ranges.size" or "ranges.empty" or "ranges.contains" or "ranges.find" or "ranges.find_if"
        or "ranges.any" or "ranges.any_of" or "ranges.all_of" or "ranges.none" or "ranges.none_of";

    private bool DatumExecutionMayDefer(NativeDatum value)
    {
        if (DatumMayDefer(value)) return true;
        if (value.Value.Kind != NativeKind.Function) return false;
        NativeFunction function = value.Value.AsFunction();
        if (function.NativeName is { } name)
        {
            Msync.Core.NativeSignature signature = NativeSignatures.Get(name);
            return signature.RequiresWorkScope;
        }
        if (!ExecutionFunction(image, function.Function).IsPureSynchronous) return true;
        if (function.Environment is null) return false;
        NativeReachable captured = new(memory, GetRangeRoots, image);
        captured.Add(value);
        return captured.Futures.Any(static future => !future.Completion.IsCompletedSuccessfully);
    }

    private NativeDatum SubmitStreamReduction(Msync.Core.NativeSignature signature,
        IReadOnlyList<NativeDatum> arguments, NativeRuntimeContext origin, SourceSpan? source,
        Task predecessor, CancellationToken token, NativeType? resultPhysical)
    {
        NativeDatum[] inputs = arguments.ToArray();
        NativeWorkScope owner = currentWork.Value ?? executionWork;
        NativeReachable roots = new(memory, GetRangeRoots, image);
        foreach (NativeDatum input in inputs) roots.Add(input);
        NativeSubmission submission = new(NativeSubmissionKind.Stream, token, source, origin.Timeline);
        TypeRef logicalType = signature.Result;
        NativeType? physicalType = null;
        if (signature.ResultRule == NativeResultRule.RangeItem)
        {
            logicalType = inputs[0].LogicalType.Element ?? TypeRef.Any;
            if (inputs[0].IsReady)
            {
                NativeRange range = inputs[0].Value.AsRange();
                logicalType = range.ItemType;
                physicalType = range.PhysicalItemType;
            }
        }
        NativeFuture future = new(submission.Completion, roots.Futures.ToArray(), source: source,
            processes: roots.Processes.ToArray(), logicalType: logicalType, physicalType: physicalType, submission: submission);
        NativeWorkFrontier frontier = owner.CaptureFrontier();
        owner.Add(future);
        using RuntimeTimelineScope? observation = origin.Timeline?.Begin("stream.submit");
        observation?.SetSource(source);
        observation?.SetAttribute("native.name", signature.Name);
        observation?.SetMetric("dependencies", roots.Futures.Count);
        if (observation is not null)
        {
            origin.Timeline!.Register(submission.Completion, observation);
            observation.Link("ordering", predecessor);
        }
        submission.Start(cancellation => CompleteStreamReductionAsync(signature, inputs, origin, future,
            owner, frontier, predecessor, cancellation, resultPhysical));
        return new(future);
    }

    private async ValueTask<NativeValue> CompleteStreamReductionAsync(Msync.Core.NativeSignature signature,
        NativeDatum[] inputs, NativeRuntimeContext origin, NativeFuture future, NativeWorkScope parent,
        NativeWorkFrontier frontier, Task predecessor, CancellationToken token, NativeType? resultPhysical)
    {
        using RuntimeTimelineScope? operation = origin.Timeline?.Begin("stream.execute");
        operation?.SetSource(future.Source);
        operation?.SetAttribute("native.name", signature.Name);
        operation?.SetMetric("inherited.futures", frontier.Futures.Length);
        using DirectoryScope directory = EnterDeferredDirectory(origin);
        NativeWorkScope? previous = currentWork.Value;
        NativeWorkScope work = new(parent, frontier, future);
        currentWork.Value = work;
        NativeValue result = default;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            await predecessor.WaitAsync(token).ConfigureAwait(false);
            NativeDatum? value = await TryRangeDatumAsync(signature, inputs, token, resultPhysical);
            result = value is { } datum ? await datum.ResolveAsync(token)
                : await InvokeNativeAsync(signature.Name, await ResolveArguments(inputs, token), origin, token);
        }
        catch (Exception error) { failure = error; }
        try
        {
            NativeReachable reachable = new(memory, GetRangeRoots, image);
            if (failure is null) reachable.Add(new NativeDatum(result));
            foreach (NativeDatum input in inputs) reachable.Add(input);
            await work.FinishAsync(reachable, () => Interlocked.Increment(ref failedTransfers), token).ConfigureAwait(false);
        }
        catch (Exception cleanup)
        {
            if (failure is null) failure = cleanup;
            else if (!ReferenceEquals(failure, cleanup) && !(failure is OperationCanceledException && cleanup is OperationCanceledException))
                failure = new AggregateException("Native stream reduction and callback cleanup failed.", failure, cleanup);
        }
        finally { currentWork.Value = previous; }
        operation?.SetMetric("callback.work_finished", 1);
        if (failure is not null)
        {
            operation?.Fail(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }

}
