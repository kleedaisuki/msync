using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private sealed record ByteWrite(long Id, ByteInput Input, int Job, HpirWorkSite Site);
    private Dictionary<long, ByteWrite>? byteWrites;
    private long nextByteWrite;

    internal (long Id, int Capacity) OpenFileBytes(NativePathValue destination, bool append,
        ExecutionOptions? options, BindingContext? binding, HpirWorkSite site, CancellationToken token)
    {
        FileTransfers owner = fileTransfers ??= new(options, binding);
        var input = new ByteInput();
        Task? ordering = Ordering;
        string name = HpirFileByteImports.NativeName(append);
        var result = Submit<bool>(HpirWorkKind.FileWrite,
            cancellation => WriteFileBytes(owner, destination, input, append, name, ordering, site, cancellation), site);
        if (result.Ready) { input.Dispose(); return (0, 0); }

        using var trace = site.Begin("hpir.file_bytes.open");
        long id = checked(++nextByteWrite);
        var write = new ByteWrite(id, input, result.JobId, site);
        (byteWrites ??= []).Add(id, write);
        int capacity = ByteWriteCapacity(write, token, trace);
        trace?.SetMetric("source.chunk_capacity", capacity);
        return (id, capacity);
    }

    internal int FileByteChunk(long id, ReadOnlySpan<byte> bytes, bool final, CancellationToken token)
    {
        ByteWrite write = byteWrites![id];
        using var trace = write.Site.Begin("hpir.file_bytes.chunk");
        trace?.SetMetric("source.bytes", bytes.Length);
        trace?.SetAttribute("source.final", final ? "true" : "false");
        try
        {
            int remaining = write.Input.Supply(bytes, final);
            if (!final) return remaining > 0 ? remaining : ByteWriteCapacity(write, token, trace);
            FinishFileBytes(write, token, trace);
            return 0;
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private int ByteWriteCapacity(ByteWrite write, CancellationToken token, RuntimeTimelineScope? trace)
    {
        Task completion = jobs[write.Job].Completion;
        bool pull = write.Input.TryRequest(completion, Guests, token);
        trace?.Link("file_bytes.writer", completion);
        if (pull) return write.Input.Capacity;
        FinishFileBytes(write, token, trace);
        return 0;
    }

    private void FinishFileBytes(ByteWrite write, CancellationToken token, RuntimeTimelineScope? trace)
    {
        try { _ = Demand<bool>(write.Job, token, trace); }
        finally
        {
            byteWrites!.Remove(write.Id);
            write.Input.Dispose();
        }
    }

    private int AbortFileBytes()
    {
        if (byteWrites is not { Count: > 0 } active) return 0;
        int abandoned = active.Count;
        foreach (ByteWrite write in active.Values) write.Input.Dispose();
        active.Clear();
        return abandoned;
    }

    private static async ValueTask<bool> WriteFileBytes(FileTransfers owner, NativePathValue destination, ByteInput input,
        bool append, string name, Task? ordering, HpirWorkSite site, CancellationToken token)
    {
        FileResources frontier = owner.NeedResources();
        FileAdmission admission = frontier.Admit(null, FileKeysFor(destination, owner.Context), sharedTransfer: false);
        try
        {
            await AwaitFileWrite(admission, destination, name, ordering, site, token).ConfigureAwait(false);
            using var trace = site.Begin("hpir.file_bytes.write");
            trace?.SetAttribute("file.operation", name);
            trace?.SetAttribute("file.action", owner.Context.DryRun ? "simulate" : "write");
            try
            {
                token.ThrowIfCancellationRequested();
                if (!owner.Context.DryRun)
                    await NativeBuiltins.WriteFile(destination, input, append, owner.Context, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) { trace?.Fail(error); throw; }
            finally
            {
                trace?.SetMetric("source.read_requests", input.ReadRequests);
                trace?.SetMetric("source.bytes_consumed", input.BytesConsumed);
            }
        }
        finally { admission.Release(); }
    }

    private sealed class ByteInput : Stream
    {
        private sealed record Request(Memory<byte> Buffer, TaskCompletionSource<int> Answer);
        private TaskCompletionSource<Request> requested = NewRequest();
        private Request? activeRead;
        private Request? current;
        private int used;
        private volatile bool ended, abandoned;

        private static TaskCompletionSource<Request> NewRequest() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ReadRequests { get; private set; }
        internal long BytesConsumed { get; private set; }
        internal int Capacity => Math.Min(HpirFileByteImports.ChunkBytes, current!.Buffer.Length - used);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (ended || buffer.IsEmpty) return 0;
            token.ThrowIfCancellationRequested();
            var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new Request(buffer, answer);
            Volatile.Write(ref activeRead, pending);
            try
            {
                if (abandoned) throw new OperationCanceledException();
                ++ReadRequests;
                requested.SetResult(pending);
                // Only the mutator can release a published rental, either by
                // filling it or by abandoning the entire staged writer.
                int count = await answer.Task.ConfigureAwait(false);
                BytesConsumed += count;
                return count;
            }
            finally { Volatile.Write(ref activeRead, null); }
        }

        internal bool TryRequest(Task completion, HpirExecutor.GuestChildren? guests, CancellationToken token)
        {
            Task<Request> next = requested.Task;
            if (!next.IsCompleted)
            {
                Task<Task> ready = Task.WhenAny(next, completion);
                if (!ready.IsCompleted) guests?.PumpHostDemand(ready, token);
                if (ready.WaitAsync(token).GetAwaiter().GetResult() != next) return false;
            }
            current = next.GetAwaiter().GetResult();
            return true;
        }

        internal int Supply(ReadOnlySpan<byte> bytes, bool final)
        {
            var request = current!;
            bytes.CopyTo(request.Buffer.Span[used..]);
            used += bytes.Length;
            if (!final && used < request.Buffer.Length) return Capacity;
            ended = final;
            current = null;
            if (!final) requested = NewRequest();
            request.Answer.SetResult(used);
            used = 0;
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                abandoned = true;
                Volatile.Read(ref activeRead)?.Answer.TrySetCanceled();
            }
            base.Dispose(disposing);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
