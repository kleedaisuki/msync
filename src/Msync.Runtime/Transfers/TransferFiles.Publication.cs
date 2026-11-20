namespace Msync.Runtime;

internal static partial class TransferFiles
{
    internal sealed class PublicationQueue(CancellationToken token, RuntimeTimeline? timeline) : IDisposable
    {
        private const int Depth = 4;
        internal const int MaximumFileBytes = 64 * 1024;
        private readonly Queue<StagedFile> pending = new(Depth);

        internal void Reserve()
        {
            if (pending.Count == Depth) PublishNext();
            token.ThrowIfCancellationRequested();
        }

        internal StagedFile Open(string kind, string? source, string target, bool async) =>
            new(kind, source, target, async, timeline);

        internal void Submit(StagedFile file)
        {
            file.StartSync();
            pending.Enqueue(file);
        }

        internal void Finish()
        {
            while (pending.Count != 0) PublishNext();
        }

        private void PublishNext()
        {
            StagedFile file = pending.Dequeue();
            try { file.Publish(token, pending.Count + 1); }
            catch (Exception error)
            {
                file.Abandon(error);
                throw;
            }
        }

        public void Dispose()
        {
            while (pending.TryDequeue(out StagedFile? file)) file.Abandon();
        }
    }

    internal sealed class StagedFile
    {
        private readonly string stage;
        private readonly string target;
        private readonly RuntimeTimeline? timeline;
        private readonly RuntimeTimelineScope? activity;
        private Task? sync;

        internal StagedFile(string kind, string? source, string target, bool async, RuntimeTimeline? timeline)
        {
            this.target = target;
            this.timeline = timeline;
            stage = Path.Combine(Path.GetDirectoryName(target) ?? ".", $".msync-{Guid.NewGuid():N}");
            // Keep the owner alive through rename without leaving it as the
            // producer's current scope while the producer stages later files.
            using IDisposable? restore = timeline?.Attach(timeline.CurrentSpanId);
            activity = BeginFile(timeline, kind, source, target);
            activity?.SetAttribute("file.publication", "queued");
            try
            {
                Output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                    1, FileOptions.SequentialScan | (async ? FileOptions.Asynchronous : FileOptions.None));
            }
            catch (Exception error)
            {
                activity?.Fail(error);
                activity?.Dispose();
                throw;
            }
        }

        internal FileStream Output { get; }
        internal string PathOnDisk => stage;
        internal void SetSourceSize(long size)
        {
            activity?.SetAttribute("file.backend", "buffered");
            activity?.SetMetric("source.size_bytes", size);
        }

        internal void StartSync()
        {
            activity?.SetMetric("bytes.written", Output.Position);
            sync = Task.Run(Sync);
        }

        private void Sync()
        {
            using IDisposable? owner = activity is null ? null : timeline!.Attach(activity.Id);
            using (Output) Flush(Output, stage, timeline);
        }

        internal void Publish(CancellationToken token, int depth)
        {
            using IDisposable? owner = activity is null ? null : timeline!.Attach(activity.Id);
            WaitForSync(depth);
            token.ThrowIfCancellationRequested();
            TransferFiles.Publish(stage, target, timeline);
            activity?.Dispose();
        }

        private void WaitForSync(int depth)
        {
            using var wait = timeline?.Begin("file.publication.wait");
            wait?.SetMetric("queue.depth", depth);
            wait?.SetAttribute("wait.pending_on_entry", sync!.IsCompleted ? "false" : "true");
            try { sync!.GetAwaiter().GetResult(); }
            catch (Exception error) { wait?.Fail(error); throw; }
        }

        internal void Abandon(Exception? cause = null)
        {
            using IDisposable? owner = activity is null ? null : timeline!.Attach(activity.Id);
            activity?.SetAttribute("file.publication", "abandoned");
            using (var cleanup = timeline?.Begin("file.stage.cleanup"))
            {
                try
                {
                    if (sync is null) Output.Dispose();
                    else sync.GetAwaiter().GetResult();
                }
                catch (Exception error) { cause ??= error; }
                try { File.Delete(stage); }
                catch (Exception error) { cleanup?.Fail(error); cause ??= error; }
            }
            if (cause is not null) activity?.Fail(cause);
            else activity?.Complete(false);
            activity?.Dispose();
        }
    }
}
