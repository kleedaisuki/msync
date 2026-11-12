using Msync.Compiler;
using System.Threading.Channels;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private sealed class AnalysisQueue
    {
        private readonly object gate = new();
        private readonly LinkedList<AnalysisWork> foreground = [];
        private readonly LinkedList<AnalysisWork> background = [];
        private readonly Dictionary<string, LinkedListNode<AnalysisWork>> pending = new(StringComparer.Ordinal);
        private readonly Channel<bool> ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        private AnalysisWork? active;
        private bool activeDemanded;

        internal void Schedule(AnalysisWork work)
        {
            lock (gate)
            {
                if (pending.TryGetValue(work.Uri, out var node))
                {
                    node.Value.Cancellation.Dispose();
                    node.Value = work;
                    // A request for the old revision does not demand the new one.
                    if (node.List == foreground)
                    {
                        foreground.Remove(node);
                        background.AddLast(node);
                    }
                }
                else pending.Add(work.Uri, background.AddLast(work));
                if (active?.Uri == work.Uri) active.Cancellation.Cancel();
                ready.Writer.TryWrite(true);
            }
        }

        internal void Demand(string uri, long generation)
        {
            lock (gate)
            {
                if (active?.Uri == uri && active.Document.Generation == generation)
                {
                    activeDemanded = true;
                    return;
                }
                if (!pending.TryGetValue(uri, out var node) || node.Value.Document.Generation != generation) return;
                if (node.List == background)
                {
                    background.Remove(node);
                    foreground.AddLast(node);
                }
                if (active is not { Retried: false } displaced || activeDemanded || displaced.Uri == uri
                    || displaced.Cancellation.IsCancellationRequested || pending.ContainsKey(displaced.Uri)) return;

                // The retry follows already queued demands, but later demands
                // cannot starve it. The one worker owns both attempts.
                var retry = displaced with { Cancellation = new(), Retried = true };
                pending.Add(retry.Uri, foreground.AddLast(retry));
                displaced.Cancellation.Cancel();
            }
        }

        internal void Close(string uri)
        {
            lock (gate)
            {
                if (pending.Remove(uri, out var node))
                {
                    node.List!.Remove(node);
                    node.Value.Cancellation.Dispose();
                }
                if (active?.Uri == uri) active.Cancellation.Cancel();
            }
        }

        internal void Stop()
        {
            lock (gate)
            {
                active?.Cancellation.Cancel();
                foreach (var node in pending.Values) node.Value.Cancellation.Dispose();
                pending.Clear();
                foreground.Clear();
                background.Clear();
                ready.Writer.TryComplete();
            }
        }

        internal async ValueTask<bool> WaitAsync() =>
            await ready.Reader.WaitToReadAsync() && ready.Reader.TryRead(out _);

        internal AnalysisWork? Take()
        {
            lock (gate)
            {
                var next = foreground.First ?? background.First;
                if (next is null) return null;
                activeDemanded = next.List == foreground;
                next.List!.Remove(next);
                pending.Remove(next.Value.Uri);
                return active = next.Value;
            }
        }

        internal void Finish(AnalysisWork work, EditorAnalysis? result, ChannelWriter<ServerEvent> events)
        {
            lock (gate)
            {
                if (result is not null && !work.Cancellation.IsCancellationRequested)
                    events.TryWrite(new AnalysisEvent(work.Uri, work.Document, result));
                active = null;
            }
        }
    }
}
