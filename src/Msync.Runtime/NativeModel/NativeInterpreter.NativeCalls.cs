using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private Task inputOrdering = Task.CompletedTask;
    private async ValueTask<NativeDatum> InvokeNativeDatumAsync(string name, IReadOnlyList<NativeDatum> arguments,
        NativeRuntimeContext origin, SourceSpan? source, CancellationToken token, NativeType? rangePhysicalType = null, Msync.Core.NativeSignature? signature = null)
    {
        Task predecessor = ordering;
        List<NativeFuture>? dependencies = null;
        foreach (NativeDatum argument in arguments)
            if (argument.Future is { } dependency) (dependencies ??= []).Add(dependency);
        if (signature is null) NativeSignatures.TryGet(name, out signature);
        if (name is "io.read_line" or "io.read_stdin")
            return SubmitInput(name, arguments, origin, source, signature!, predecessor, token);
        if (signature is not null && signature.ReturnsFileValue)
            return SubmitFileRead(signature, arguments, origin, source, predecessor, token);
        if (signature is not null && IsStreamReduction(name))
        {
            if (arguments.Any(DatumExecutionMayDefer))
                return SubmitStreamReduction(signature, arguments, origin, source, predecessor, token, rangePhysicalType);
        }
        if (signature is not null && await TryRangeDatumAsync(signature, arguments, token, rangePhysicalType) is { } rangeResult) return rangeResult;
        bool scalarView = signature is not null && (signature.Behavior.GetValueOrDefault() & NativeBehavior.DeferredScalar) != 0;
        if ((dependencies is not null || scalarView) && DefersScalarNative(signature, arguments))
        {
            List<NativeProcess>? processes = null;
            foreach (NativeDatum argument in arguments)
                if (argument.IsReady && argument.Value.Kind == NativeKind.Process)
                    (processes ??= []).Add(argument.Value.AsProcess());
            return Track(new(new NativeFuture(InvokeDeferredNativeAsync(name, arguments, origin, predecessor, token), dependencies,
                source: source, processes: processes, logicalType: signature!.Result)));
        }
        return new(await InvokeNativeAsync(name, await ResolveArguments(arguments, token), origin, token, predecessor));
    }

    private NativeDatum SubmitInput(string name, IReadOnlyList<NativeDatum> arguments, NativeRuntimeContext origin,
        SourceSpan? source, Msync.Core.NativeSignature signature, Task predecessor, CancellationToken token)
    {
        NativeSubmission submission = new(NativeSubmissionKind.Input, token, source, origin.Timeline);
        Task previousInput = Interlocked.Exchange(ref inputOrdering, submission.Completion);
        NativeFuture future = new(submission.Completion, source: source, logicalType: signature.Result, submission: submission);
        (currentWork.Value ?? executionWork).Add(future);
        using RuntimeTimelineScope? observation = origin.Timeline?.Begin("input.submit");
        observation?.SetSource(source);
        observation?.SetAttribute("native.name", name);
        if (observation is not null)
        {
            origin.Timeline!.Register(submission.Completion, observation);
            observation.Link("input.order", previousInput);
            observation.Link("ordering", predecessor);
        }
        submission.Start(cancellation => ReadInputAsync(name, arguments, origin, previousInput, predecessor, cancellation));
        return new(future);
    }

    private async ValueTask<NativeValue> ReadInputAsync(string name, IReadOnlyList<NativeDatum> arguments,
        NativeRuntimeContext origin, Task previousInput, Task predecessor, CancellationToken token)
    {
        // Admission order waits for completion, not for another reader's successful result.
        if (!previousInput.IsCompleted) await Task.WhenAny(previousInput).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await InvokeNativeAsync(name, await ResolveArguments(arguments, token).ConfigureAwait(false), origin, token, predecessor).ConfigureAwait(false);
    }

    private static bool DefersScalarNative(Msync.Core.NativeSignature? signature, IReadOnlyList<NativeDatum> arguments)
    {
        if (signature is null || (signature.Effect != NativeEffect.Pure
                && (signature.Behavior.GetValueOrDefault() & NativeBehavior.DeferredScalar) == 0)
            || arguments.Count > signature.Parameters.Count
            || (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.LocalMemory)) != 0)
            return false;
        const TypeKind mutableOrInvoked = TypeKind.Range | TypeKind.Function | TypeKind.Scope;
        return signature.Parameters.All(static parameter => (parameter.Kind & mutableOrInvoked) == 0)
            && arguments.All(static argument => !argument.IsReady || argument.Value.Kind is not (NativeKind.Range or NativeKind.Function or NativeKind.Address));
    }

    private async Task<NativeValue> InvokeDeferredNativeAsync(string name, IReadOnlyList<NativeDatum> arguments,
        NativeRuntimeContext origin, Task predecessor, CancellationToken token) =>
        await InvokeNativeAsync(name, await ResolveArguments(arguments, token), origin, token, predecessor);

    internal bool TryFastUnaryMath(NativeWorkScope work, NativeFrame frame, in NativeInstruction instruction)
    {
        if (instruction.C >= 0 || instruction.Inputs is not { Count: 1 } inputs) return false;
        string name = instruction.Symbol!;
        if (!name.StartsWith("math.", StringComparison.Ordinal)) return false;
        NativeAddress source = frame.Location(inputs[0]);
        if (!frame.IsReady(inputs[0], source)) return false;
        NativeType srcType = source.Type;
        if (srcType.IsUnion)
        {
            if (source.Allocation.Storage.ReadKind(source.Offset) != NativeKind.Number) return false;
        }
        else if (srcType.Kind != NativeKind.Number) return false;
        double value = NativeStorage.LoadReadyNumber(source, srcType.Representation.PayloadOffset);
        double result;
        switch (name)
        {
            case "math.floor": result = Math.Floor(value); break;
            case "math.ceil": result = Math.Ceiling(value); break;
            case "math.trunc": result = Math.Truncate(value); break;
            case "math.abs": result = Math.Abs(value); break;
            case "math.sqrt": result = Math.Sqrt(value); break;
            case "math.round": result = Math.Round(value, MidpointRounding.AwayFromZero); break;
            case "math.round_even": result = Math.Round(value, MidpointRounding.ToEven); break;
            default: return false;
        }
        work.Counters.Native(name);
        NativeAddress target = frame.Location(instruction.Result);
        if (!target.Type.IsUnion && target.Type.Kind == NativeKind.Number)
        {
            memory.RemovePending(target);
            NativeStorage.StoreNumberCompiled(target, result);
            if (profile is not null) memory.RecordScalarWrite(target, NativeKind.Number);
        }
        else frame.Set(instruction.Result, NativeValue.FromNumber(result));
        return true;
    }

}
