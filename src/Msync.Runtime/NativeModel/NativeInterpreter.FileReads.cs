using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private NativeDatum SubmitFileRead(Msync.Core.NativeSignature signature, IReadOnlyList<NativeDatum> arguments,
        NativeRuntimeContext origin, SourceSpan? source, Task predecessor, CancellationToken token)
    {
        NativeDatum[] inputs = arguments.ToArray();
        NativeSubmission submission = new(NativeSubmissionKind.FileRead, token, source, origin.Timeline);
        TaskCompletionSource lease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ResourceIndex.Registration> registrations = [];
        HashSet<Task> dependencies = [];
        NativeFuture future = new(submission.Completion, inputs.Where(input => input.Future is not null).Select(input => input.Future!).ToArray(),
            source: source, logicalType: signature.Result, submission: submission,
            parentAdmission: admittedTransfer.Value, resourceCompletion: lease.Task);
        using RuntimeTimelineScope? admission = origin.Timeline?.Begin("file_read.submit");
        admission?.SetSource(source);
        admission?.SetAttribute("native.name", signature.Name);
        lock (resourceGate)
        {
            for (int index = 0; index < inputs.Length && index < signature.Parameters.Count; index++)
            {
                if ((signature.Parameters[index].Kind & TypeKind.Path) == 0) continue;
                NativePathAccess access = signature.AccessOfPathArgument(index);
                if (access is not (NativePathAccess.Read or NativePathAccess.ReadIfPath)) continue;
                NativeDatum value = inputs[index];
                if (access == NativePathAccess.ReadIfPath && (value.LogicalType.Kind & TypeKind.Path) == 0) continue;
                ResourceKey key = value.IsReady ? Key(origin.AsPath(value.Value), origin) : new("*", "");
                resources.Collect(key, false, dependencies);
                registrations.Add(resources.AddRead(lease.Task, key));
                if (admission is not null) RecordSchedulingResource(admission, "read", index, key);
            }
            ExcludeAdmissions(dependencies);
            dependencies.Remove(lease.Task);
            if (!predecessor.IsCompletedSuccessfully) dependencies.Add(predecessor);
        }
        admission?.SetMetric("dependencies.matched", dependencies.Count);
        admission?.SetMetric("resources.reads", registrations.Count);
        if (admission is not null) origin.Timeline!.Register(lease.Task, admission);
        (currentWork.Value ?? executionWork).Add(future);
        submission.Start(cancellation => RunFileReadAsync(signature, inputs, origin, future, lease, dependencies, registrations, cancellation));
        return new(future);
    }

    private async ValueTask<NativeValue> RunFileReadAsync(Msync.Core.NativeSignature signature, NativeDatum[] inputs,
        NativeRuntimeContext origin, NativeFuture future, TaskCompletionSource lease, HashSet<Task> dependencies,
        IReadOnlyList<ResourceIndex.Registration> registrations, CancellationToken token)
    {
        NativeFuture? previous = admittedTransfer.Value;
        admittedTransfer.Value = future;
        try
        {
            token.ThrowIfCancellationRequested();
            if (dependencies.Count != 0)
                await AwaitSchedulingAsync(Task.WhenAll(dependencies), token, "file_read.resources", "relevant", dependencies).ConfigureAwait(false);
            NativeValue[] values = await ResolveArguments(inputs, token).ConfigureAwait(false);
            if (signature.Name == "io.read_text")
            {
                using RuntimeTimelineScope? refinement = origin.Timeline?.Begin("file_read.refine");
                var path = origin.AsPath(values[0]);
                string kind = path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl }
                    ? (await NativeBuiltins.InvokeAsync("fs.status", [values[0]], origin, InvokeAsync, token).ConfigureAwait(false)).AsString()
                    : OperatingSystem.IsWindows() ? "regular" : NativeFileIdentity.Kind(origin.LocalPath(path), false);
                refinement?.SetAttribute("path", path.ToString());
                refinement?.SetAttribute("resource.kind", kind);
                refinement?.SetMetric("stream.co_start", kind is "fifo" or "character" or "socket" ? 1 : 0);
                if (kind is "fifo" or "character" or "socket")
                {
                    // A stream's writer must be admitted before its read can finish.
                    ReleaseReadLease(lease, registrations);
                }
            }
            token.ThrowIfCancellationRequested();
            return await InvokeNativeAsync(signature.Name, values, origin, token, admitted: true).ConfigureAwait(false);
        }
        finally
        {
            ReleaseReadLease(lease, registrations);
            admittedTransfer.Value = previous;
        }
    }

    private void ReleaseReadLease(TaskCompletionSource lease, IReadOnlyList<ResourceIndex.Registration> registrations)
    {
        lock (resourceGate)
            foreach (ResourceIndex.Registration registration in registrations) resources.Remove(lease.Task, registration);
        lease.TrySetResult();
    }
}
