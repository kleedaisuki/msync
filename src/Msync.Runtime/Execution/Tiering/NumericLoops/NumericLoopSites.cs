using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

internal sealed class NumericLoopSites(Module image, int functionId)
{
    private const int WarmupBackedges = 8;
    private const int WarmupTailEntries = 64;
    private readonly Dictionary<int, Site> sites = [];
    private Dictionary<int, Site>? tails;
    private int lastTailPc = -1;
    private Site? lastTail;
    private int lastPc = -1;
    private Site? lastSite;
    private ExecutionRegion? region;
    private bool imported;

    public NumericLoop? Backedge(int first, int last)
    {
        var site = last == lastPc ? lastSite! : SelectSite(last);
        if (site.Visits < WarmupBackedges && ++site.Visits == WarmupBackedges && Graph() is { } graph)
            site.Loop = NumericLoop.Import(image, graph, first, last);
        return site.Loop;
    }

    public bool RunTail<TFrame>(int first, int last, ref FrameSlots locals, IReadOnlyList<Instruction> original,
        ref OperandStack<TFrame> stack, ref TFrame frame, CancellationToken cancellationToken, out int continuation)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        continuation = 0;
        var site = first == lastTailPc ? lastTail! : SelectTail(first);
        if (site.Visits < WarmupTailEntries && ++site.Visits == WarmupTailEntries && Graph() is { } graph)
            site.Loop = NumericLoop.ImportTail(image, graph, first, last);
        if (site.Loop is not { } plan) return false;
        if (site.Skip != 0)
        {
            site.Skip--;
            return false;
        }
        if (plan.TryRunTail(ref locals, original, ref stack, ref frame, cancellationToken, out continuation))
        {
            site.Backoff = 0;
            return true;
        }
        frame.JitTailRejected();
        // Keep probing polymorphic sites, but don't repeatedly unpack an
        // unchanged cold representation on every call.
        site.Backoff = site.Backoff == 0 ? 8 : Math.Min(site.Backoff * 2, 1024);
        site.Skip = site.Backoff;
        return false;
    }

    private Site SelectTail(int first)
    {
        var map = tails ??= [];
        if (!map.TryGetValue(first, out var site)) map.Add(first, site = new());
        lastTailPc = first;
        return lastTail = site;
    }

    private ExecutionRegion? Graph()
    {
        if (!imported)
        {
            imported = true;
            ExecutionRegionImporter.TryImport(image, functionId, out region, out _);
        }
        return region;
    }

    private Site SelectSite(int pc)
    {
        if (!sites.TryGetValue(pc, out var site))
            sites.Add(pc, site = new());
        lastPc = pc;
        return lastSite = site;
    }

    private sealed class Site
    {
        public int Visits;
        public int Skip;
        public int Backoff;
        public NumericLoop? Loop;
    }
}
