using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private async ValueTask<NativeValue> InvokeNativeWriteAsync(NativeBuiltinOperation operation,
        IReadOnlyList<NativeValue> arguments, NativeRuntimeContext origin, Task predecessor, CancellationToken token)
    {
        Msync.Core.NativeSignature signature = operation.Signature;
        TaskCompletionSource<NativeValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeFuture? previous = admittedTransfer.Value;
        NativeFuture marker = new(completion.Task, parentAdmission: previous, logicalType: TypeRef.Null);
        List<ResourceKey> reads = [], writes = [];
        List<ResourceIndex.Registration> registrations = [];
        using RuntimeTimelineScope? observation = origin.Timeline?.Begin("native_io.admit");
        observation?.SetAttribute("native.name", signature.Name);
        for (int index = 0; index < arguments.Count && index < signature.Parameters.Count; index++)
        {
            if ((signature.Parameters[index].Kind & TypeKind.Path) == 0) continue;
            NativePathAccess access = signature.AccessOfPathArgument(index);
            if (access == NativePathAccess.None || access == NativePathAccess.ReadIfPath && arguments[index].Kind != NativeKind.Path) continue;
            ResourceKey key = Key(origin.AsPath(arguments[index]), origin);
            if (access == NativePathAccess.Write) writes.Add(key); else reads.Add(key);
        }
        HashSet<Task> dependencies;
        lock (resourceGate)
        {
            dependencies = resources.Dependencies(reads, writes);
            ExcludeAdmissions(dependencies);
            if (!predecessor.IsCompletedSuccessfully) dependencies.Add(predecessor);
            foreach (ResourceKey key in reads) registrations.Add(resources.AddRead(completion.Task, key));
            foreach (ResourceKey key in writes) registrations.Add(resources.Add(completion.Task, null, key));
        }
        observation?.SetMetric("resources.reads", reads.Count);
        observation?.SetMetric("resources.writes", writes.Count);
        observation?.SetMetric("dependencies.matched", dependencies.Count);
        if (observation is not null)
        {
            origin.Timeline!.Register(completion.Task, observation);
            for (int index = 0; index < reads.Count; index++) RecordSchedulingResource(observation, "read", index, reads[index]);
            for (int index = 0; index < writes.Count; index++) RecordSchedulingResource(observation, "write", index, writes[index], true);
        }
        admittedTransfer.Value = marker;
        try
        {
            if (dependencies.Count != 0)
                await AwaitSchedulingAsync(Task.WhenAll(dependencies), token, "native.resources", "relevant", dependencies).ConfigureAwait(false);
            return await NativeBuiltins.InvokeAsync(operation, arguments, origin, InvokeAsync, token).ConfigureAwait(false);
        }
        catch (Exception error) { observation?.Fail(error); throw; }
        finally
        {
            lock (resourceGate)
                foreach (ResourceIndex.Registration registration in registrations) resources.Remove(completion.Task, registration);
            completion.TrySetResult(NativeValue.Null);
            admittedTransfer.Value = previous;
        }
    }
}
