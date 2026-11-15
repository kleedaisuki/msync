// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp getPreviousDefIterative / getPreviousDef* / trivial PHIs.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    private enum ResumePoint { EnterBlock, ResumeSinglePred, RunPredLoop }
    private sealed class PreviousFrame(LlvmBasicBlock block)
    {
        internal readonly LlvmBasicBlock Block = block;
        internal readonly LlvmBasicBlock[] Preds = Predecessors(block);
        internal ResumePoint Resume;
        internal readonly List<LlvmMemoryAccess> PhiOps = [];
        internal int PredIndex;
        internal bool PendingIncoming;
        internal bool UniqueIncoming = true;
        internal LlvmMemoryAccess? SingleAccess;
        internal void Incorporate(LlvmMemoryAccess incoming)
        {
            if (SingleAccess is null) SingleAccess = incoming;
            else if (!ReferenceEquals(SingleAccess, incoming)) UniqueIncoming = false;
            PhiOps.Add(incoming);
        }
    }

    private LlvmMemoryAccess? LocalDefFromEnd(LlvmBasicBlock block, Dictionary<LlvmBasicBlock, LlvmMemoryAccess> cache)
    {
        if (MemorySsa.GetBlockDefs(block)?.Last is not { } result) return null;
        cache.TryAdd(block, result); return result;
    }

    private LlvmMemoryAccess GetPreviousDefIterative(LlvmBasicBlock block, Dictionary<LlvmBasicBlock, LlvmMemoryAccess> cache)
    {
        var stack = new List<PreviousFrame> { new(block) };
        LlvmMemoryAccess? returned = null;
        while (stack.Count != 0)
        {
            PreviousFrame frame = stack[^1];
            block = frame.Block;
            Count("previous.frames");
            switch (frame.Resume)
            {
                case ResumePoint.EnterBlock:
                    if (cache.TryGetValue(block, out var cached))
                    { returned = Track(cached)!; stack.RemoveAt(stack.Count - 1); Count("previous.cache_hits"); continue; }
                    if (!MemorySsa.DominatorTree.IsReachableFromEntry(block))
                    { returned = MemorySsa.LiveOnEntryDef; stack.RemoveAt(stack.Count - 1); continue; }
                    if (frame.Preds.Length != 0 && frame.Preds.All(pred => ReferenceEquals(pred, frame.Preds[0])))
                    {
                        visitedBlocks.Add(block);
                        var predecessor = frame.Preds[0];
                        if (LocalDefFromEnd(predecessor, cache) is { } local)
                        { cache.TryAdd(block, local); returned = local; stack.RemoveAt(stack.Count - 1); continue; }
                        frame.Resume = ResumePoint.ResumeSinglePred;
                        stack.Add(new(predecessor)); continue;
                    }
                    if (visitedBlocks.Contains(block))
                    {
                        returned = MemorySsa.CreateMemoryPhi(block); cache.TryAdd(block, returned);
                        stack.RemoveAt(stack.Count - 1); Count("previous.cycle_phis"); continue;
                    }
                    visitedBlocks.Add(block); frame.Resume = ResumePoint.RunPredLoop; continue;
                case ResumePoint.ResumeSinglePred:
                    cache.TryAdd(block, returned!); stack.RemoveAt(stack.Count - 1); continue;
                case ResumePoint.RunPredLoop:
                    if (frame.PendingIncoming)
                    { frame.Incorporate(returned!); frame.PendingIncoming = false; ++frame.PredIndex; }
                    bool suspended = false;
                    while (frame.PredIndex < frame.Preds.Length)
                    {
                        var predecessor = frame.Preds[frame.PredIndex];
                        Count("previous.predecessor_edges");
                        if (MemorySsa.DominatorTree.IsReachableFromEntry(predecessor))
                        {
                            if (LocalDefFromEnd(predecessor, cache) is { } local) frame.Incorporate(local);
                            else
                            { frame.PendingIncoming = true; stack.Add(new(predecessor)); suspended = true; break; }
                        }
                        else frame.PhiOps.Add(MemorySsa.LiveOnEntryDef);
                        ++frame.PredIndex;
                    }
                    if (suspended) continue;
                    LlvmMemoryPhi? phi = MemorySsa.GetMemoryAccess(block);
                    var result = TryRemoveTrivialPhi(phi, frame.PhiOps);
                    if (ReferenceEquals(result, phi) && frame.UniqueIncoming && frame.SingleAccess is not null)
                    {
                        if (phi is not null)
                        {
                            System.Diagnostics.Debug.Assert(phi.IncomingCount == 0);
                            phi.ReplaceTrackedUsesWith(frame.SingleAccess); RemoveMemoryAccess(phi);
                        }
                        result = frame.SingleAccess;
                    }
                    else if (ReferenceEquals(result, phi) && !(frame.UniqueIncoming && frame.SingleAccess is not null))
                    {
                        phi ??= MemorySsa.CreateMemoryPhi(block);
                        if (phi.IncomingCount != 0)
                        {
                            bool same = phi.IncomingCount == frame.PhiOps.Count;
                            for (int index = 0; same && index < phi.IncomingCount; ++index)
                                same = ReferenceEquals(phi.GetIncomingValue(index), Track(frame.PhiOps[index]));
                            if (!same)
                                for (int index = 0; index < phi.IncomingCount; ++index)
                                { phi.SetIncomingValue(index, Track(frame.PhiOps[index])!); phi.SetIncomingBlock(index, frame.Preds[index]); }
                        }
                        else
                        {
                            for (int index = 0; index < frame.Preds.Length; ++index)
                                phi.AddIncoming(Track(frame.PhiOps[index])!, frame.Preds[index]);
                            insertedPhis.Add(phi);
                        }
                        result = phi;
                    }
                    visitedBlocks.Remove(block); cache.TryAdd(block, result!); returned = result;
                    stack.RemoveAt(stack.Count - 1); continue;
            }
        }
        return returned!;
    }

    private LlvmMemoryAccess GetPreviousDef(LlvmMemoryAccess access) => GetPreviousDefInBlock(access)
        ?? GetPreviousDefIterative(access.Block, []);

    private LlvmMemoryAccess? GetPreviousDefInBlock(LlvmMemoryAccess access)
    {
        if (MemorySsa.GetBlockDefs(access.Block) is null) return null;
        if (access is not LlvmMemoryUse) return access.PreviousDef;
        for (var previous = access.Previous; previous is not null; previous = previous.Previous)
            if (previous is not LlvmMemoryUse) return previous;
        return null;
    }

    private LlvmMemoryAccess GetPreviousDefFromEnd(LlvmBasicBlock block, Dictionary<LlvmBasicBlock, LlvmMemoryAccess> cache) =>
        LocalDefFromEnd(block, cache) ?? GetPreviousDefIterative(block, cache);

    private LlvmMemoryAccess? RecursePhi(LlvmMemoryAccess? access)
    {
        if (access is null) return null;
        var users = access.Users.Cast<LlvmMemoryAccess>().ToArray();
        foreach (var user in users)
            if (Track(user) is LlvmMemoryPhi phi) TryRemoveTrivialPhi(phi);
        return Track(access);
    }

    private LlvmMemoryAccess TryRemoveTrivialPhi(LlvmMemoryPhi phi) =>
        TryRemoveTrivialPhi(phi, phi.Operands.Cast<LlvmMemoryAccess>().ToArray())!;

    private LlvmMemoryAccess? TryRemoveTrivialPhi(LlvmMemoryPhi? phi, IReadOnlyList<LlvmMemoryAccess> operands)
    {
        if (phi is not null && nonOptPhis.Contains(phi)) return phi;
        LlvmMemoryAccess? same = null;
        foreach (var operand in operands)
        {
            var value = Track(operand)!;
            if (ReferenceEquals(value, phi) || ReferenceEquals(value, same)) continue;
            if (same is not null) return phi;
            same = value;
        }
        if (same is null) return MemorySsa.LiveOnEntryDef;
        if (phi is not null)
        { phi.ReplaceTrackedUsesWith(same); RemoveMemoryAccess(phi); Count("phis.trivial_removed"); }
        return RecursePhi(same);
    }

    private void TryRemoveTrivialPhis(IEnumerable<LlvmMemoryPhi> phis)
    {
        foreach (var weak in phis)
            if (WeakPhi(weak) is { } phi) TryRemoveTrivialPhi(phi);
    }
}
