// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: MemorySSA.cpp
// ClobberWalker::getBlockingAccess / tryOptimizePhi / findClobber.
using System.Runtime.InteropServices;

namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmMemorySsaClobberWalker
{
    private TerminatedPath? GetBlockingAccess(LlvmMemoryAccess stopWhere, List<int> paused,
        List<int> newPaused, List<TerminatedPath> terminated)
    {
        while (paused.Count != 0)
        {
            int pathIndex = Pop(paused);
            ref DefPath node = ref CollectionsMarshal.AsSpan(paths)[pathIndex];
            if (!visitedPhis.Add(new(node.Last, node.Location, node.MayBeCrossIteration)))
            { Count("memoryssa.walker.phi_path_revisited"); continue; }
            LlvmMemoryAccess? skipStop = query.SkipSelfAccess && node.Location == query.StartingLocation ? query.OriginalAccess : null;
            WalkResult result = WalkToPhiOrClobber(ref node, stopWhere, skipStop);
            if (result.IsKnownClobber)
            {
                TerminatedPath path = new(result.Access, pathIndex);
                if (!memorySsa.Dominates(result.Access, stopWhere)) return path;
                terminated.Add(path);
                continue;
            }
            if (ReferenceEquals(result.Access, stopWhere) || ReferenceEquals(result.Access, skipStop))
            {
                if (!ReferenceEquals(result.Access, skipStop)) newPaused.Add(pathIndex);
                continue;
            }
            AddSearches((LlvmMemoryPhi)result.Access, paused, pathIndex, node.MayBeCrossIteration);
        }
        return null;
    }

    private void MoveDominatedPathToEnd(List<TerminatedPath> terminated)
    {
        int dominated = 0;
        for (int index = 1; index < terminated.Count; ++index)
            if (!memorySsa.Dominates(terminated[index].Clobber, terminated[dominated].Clobber)) dominated = index;
        int last = terminated.Count - 1;
        if (last != dominated) (terminated[last], terminated[dominated]) = (terminated[dominated], terminated[last]);
    }

    private OptimizationResult TryOptimizePhi(LlvmMemoryPhi phi, LlvmMemoryAccess start, LlvmMemoryLocation location)
    {
        paths.Add(new(location, start, phi, false, null));
        int priorPathsSize = paths.Count;
        List<int> paused = new(16), newPaused = new(8);
        List<TerminatedPath> terminated = new(4);
        AddSearches(phi, paused, 0, false);
        LlvmMemoryPhi current = phi;
        while (true)
        {
            Count("memoryssa.walker.phi_optimization_rounds");
            LlvmMemoryAccess target = GetWalkTarget(current);
            if (GetBlockingAccess(target, paused, newPaused, terminated) is { } blocker)
            {
                int node = blocker.LastNode;
                while (node >= priorPathsSize) node = paths[node].Previous!.Value;
                Count("memoryssa.walker.phi_blocked");
                return new(new(paths[node].Last, node), []);
            }
            if (newPaused.Count == 0)
            {
                MoveDominatedPathToEnd(terminated);
                TerminatedPath result = Pop(terminated);
                Count("memoryssa.walker.phi_resolved");
                return new(result, terminated);
            }
            LlvmMemoryAccess? chainEnd = null;
            List<TerminatedPath> clobbers = new(4);
            foreach (int pausedPath in newPaused)
            {
                WalkResult result = WalkToPhiOrClobber(ref CollectionsMarshal.AsSpan(paths)[pausedPath]);
                if (result.IsKnownClobber) clobbers.Add(new(result.Access, pausedPath));
                else chainEnd = result.Access;
            }
            if (terminated.Count != 0)
            {
                if (chainEnd is null)
                    for (LlvmMemoryAccess? access = target; access is not null; access =
                        access is LlvmMemoryUseOrDef useOrDef ? useOrDef.DefiningAccess : null)
                        chainEnd = access;
                foreach (TerminatedPath path in terminated)
                    if (memorySsa.DominatorTree.Dominates(chainEnd!.Block, path.Clobber.Block)) clobbers.Add(path);
            }
            if (clobbers.Count != 0)
            {
                MoveDominatedPathToEnd(clobbers);
                TerminatedPath result = Pop(clobbers);
                Count("memoryssa.walker.phi_resolved");
                return new(result, clobbers);
            }
            var chainPhi = (LlvmMemoryPhi)chainEnd!;
            priorPathsSize = paths.Count;
            paused.Clear();
            foreach (int pausedPath in newPaused) AddSearches(chainPhi, paused, pausedPath, paths[pausedPath].MayBeCrossIteration);
            newPaused.Clear();
            current = chainPhi;
        }
    }

    internal LlvmMemoryAccess FindClobber(LlvmBatchAAResults batchAA, LlvmMemoryAccess start,
        LlvmUpwardsMemoryQuery upwardQuery, ref uint walkLimit, Dictionary<string, ulong>? workMetrics)
    {
        aa = batchAA;
        query = upwardQuery;
        upwardWalkLimit = walkLimit == 0 ? 1 : walkLimit;
        metrics = workMetrics;
        Count("memoryssa.walker.searches");
        LlvmMemoryAccess current = start is LlvmMemoryUse use ? use.DefiningAccess! : start;
        DefPath first = new(query.StartingLocation, current, current, false, null);
        WalkResult walk = WalkToPhiOrClobber(ref first);
        LlvmMemoryAccess result;
        if (walk.IsKnownClobber) result = walk.Access;
        else
        {
            var optimized = TryOptimizePhi((LlvmMemoryPhi)first.Last, current, query.StartingLocation);
            result = optimized.PrimaryClobber.Clobber;
            paths.Clear();
            visitedPhis.Clear();
        }
        walkLimit = upwardWalkLimit;
        // These are borrowed query inputs, not graph-lifetime AA caches.
        aa = null!;
        query = null!;
        metrics = null;
        return result;
    }

    private static T Pop<T>(List<T> values)
    {
        int last = values.Count - 1;
        T result = values[last];
        values.RemoveAt(last);
        return result;
    }
}
