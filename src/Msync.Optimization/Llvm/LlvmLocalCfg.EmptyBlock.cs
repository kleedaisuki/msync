// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/Local.cpp TryToSimplifyUncondBranchFromEmptyBlock and its PHI helpers.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocalCfg
{
    public static bool TryToSimplifyUncondBranchFromEmptyBlock(LlvmBasicBlock block,
        LlvmDomTreeUpdater? updater = null, Dictionary<string, ulong>? metrics = null,
        uint maxPhiEntriesIncrease = 1000)
    {
        Debug.Assert(!ReferenceEquals(block, block.Parent!.BasicBlocks.First()));
        Count(metrics, "local_cfg.empty_block.queries");
        var successor = ((LlvmUncondBrInstruction)block.GetTerminator()).Destination;
        if (ReferenceEquals(block, successor))
        { Count(metrics, "local_cfg.empty_block.self_loop"); return false; }
        var predecessors = LlvmDominatorTree.Predecessors(block).ToHashSet();
        bool shared = MayHaveSharedPredecessor(successor, predecessors);
        bool killable = !shared || CanPropagatePredecessorsForPhis(block, successor, predecessors);
        LlvmBasicBlock? commonPredecessor = null;
        bool mergeable = killable || CanRedirectPredecessors(block, successor, predecessors, out commonPredecessor);
        if ((!killable && !mergeable) || IntroducesTooManyPhiEntries(block, successor, maxPhiEntriesIncrease))
        { Count(metrics, "local_cfg.empty_block.phi_conflict_or_growth"); return false; }
        if (!HasSinglePredecessor(successor))
        {
            foreach (var phi in Phis(block))
            foreach (var use in phi.Uses)
            {
                if (use.User is not LlvmPhiInstruction user || !ReferenceEquals(user.GetIncomingBlock(use.OperandNo), block))
                { Count(metrics, "local_cfg.empty_block.live_phi_use"); return false; }
            }
        }
        List<LlvmCfgUpdate> updates = [];
        if (updater is not null)
        {
            var seen = new HashSet<LlvmBasicBlock>();
            var successorPredecessors = shared ? LlvmDominatorTree.Predecessors(successor).ToHashSet() : [];
            foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                if (!successorPredecessors.Contains(predecessor) && seen.Add(predecessor))
                    updates.Add(new(LlvmCfgUpdateKind.Insert, predecessor, successor));
            seen.Clear();
            foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                if (seen.Add(predecessor) && !ReferenceEquals(predecessor, commonPredecessor))
                    updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
            if (killable) updates.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
        }
        if (successor.FirstInstruction is LlvmPhiInstruction)
        {
            var incomingPredecessors = LlvmDominatorTree.Predecessors(block).ToArray();
            foreach (var phi in Phis(successor))
                RedirectValuesToPhi(block, incomingPredecessors, phi, commonPredecessor, shared, metrics);
        }
        if (HasSinglePredecessor(successor))
        {
            block.GetTerminator().EraseFromParent();
            var insertion = FirstNonPhi(successor);
            foreach (var instruction in block.Instructions.ToArray())
            {
                instruction.RemoveFromParent();
                successor.InsertBefore(instruction, insertion);
            }
        }
        else
        {
            while (block.FirstInstruction is LlvmPhiInstruction phi)
            {
                Debug.Assert(phi.UseEmpty);
                phi.EraseFromParent();
            }
        }
        if (killable)
        {
            if (block.HasTerminator) block.GetTerminator().EraseFromParent();
            block.ReplaceAllUsesWith(successor);
            if (!successor.HasName) successor.TakeName(block);
            block.Append(new LlvmUnreachableInstruction(block.Context));
        }
        else
        {
            foreach (var use in block.Uses.ToArray())
            {
                if (use.User is not LlvmInstruction instruction || ReferenceEquals(instruction.Parent, commonPredecessor)) continue;
                if (predecessors.Contains(instruction.Parent!)) use.Set(successor);
            }
        }
        updater?.ApplyUpdates(updates);
        if (killable) LlvmBasicBlockUtils.DeleteDeadBlock(block, updater, metrics: metrics);
        Count(metrics, killable ? "local_cfg.empty_block.removed" : "local_cfg.empty_block.predecessors_redirected");
        return true;
    }

    private static bool HasSinglePredecessor(LlvmBasicBlock block)
    {
        using var iterator = LlvmDominatorTree.Predecessors(block).GetEnumerator();
        return iterator.MoveNext() && !iterator.MoveNext();
    }

    private static bool MayHaveSharedPredecessor(LlvmBasicBlock successor, HashSet<LlvmBasicBlock> predecessors)
    {
        int budget = 16;
        foreach (var predecessor in predecessors)
        {
            var terminator = predecessor.GetTerminator();
            if (terminator.SuccessorCount > budget) return true;
            budget -= terminator.SuccessorCount;
            if (terminator.Successors.Contains(successor)) return true;
        }
        return false;
    }

    private static bool CanPropagatePredecessorsForPhis(LlvmBasicBlock block, LlvmBasicBlock successor,
        HashSet<LlvmBasicBlock> predecessors)
    {
        if (HasSinglePredecessor(successor)) return true;
        foreach (var phi in Phis(successor))
        {
            var incoming = phi.GetIncomingValue(phi.GetBasicBlockIndex(block));
            var blockPhi = incoming as LlvmPhiInstruction;
            bool localPhi = blockPhi is not null && ReferenceEquals(blockPhi.Parent, block);
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                var predecessor = phi.GetIncomingBlock(index);
                if (!predecessors.Contains(predecessor)) continue;
                var left = localPhi ? blockPhi!.GetIncomingValue(blockPhi.GetBasicBlockIndex(predecessor)) : incoming;
                var right = phi.GetIncomingValue(index);
                if (!ReferenceEquals(left, right) && left is not LlvmUndefValue && right is not LlvmUndefValue) return false;
            }
        }
        return true;
    }

    private static bool CanRedirectPredecessors(LlvmBasicBlock block, LlvmBasicBlock successor,
        HashSet<LlvmBasicBlock> predecessors, out LlvmBasicBlock? common)
    {
        common = null;
        if (block.FirstInstruction is not LlvmPhiInstruction || successor.FirstInstruction is not LlvmPhiInstruction) return false;
        if (!LlvmDominatorTree.Predecessors(block).Skip(1).Any()) return false;
        if (predecessors.Any(predecessor => predecessor.GetTerminator().Opcode == LlvmOpcode.IndirectBr)) return false;
        foreach (var predecessor in LlvmDominatorTree.Predecessors(successor))
        {
            if (!predecessors.Contains(predecessor)) continue;
            if (common is not null) return false;
            common = predecessor;
        }
        return true;
    }

    private static bool IntroducesTooManyPhiEntries(LlvmBasicBlock block, LlvmBasicBlock successor, uint maximum)
    {
        if (HasSinglePredecessor(block)) return false;
        uint predecessors = checked((uint)LlvmDominatorTree.Predecessors(block).Count());
        uint changedPhis = 0;
        foreach (var phi in Phis(successor))
        {
            if (phi.GetIncomingValue(phi.GetBasicBlockIndex(block)) is LlvmPhiInstruction incoming && ReferenceEquals(incoming.Parent, block))
                continue;
            ++changedPhis;
        }
        return unchecked((predecessors - 1) * changedPhis) > maximum;
    }

    private static void RedirectValuesToPhi(LlvmBasicBlock block, IReadOnlyList<LlvmBasicBlock> predecessors,
        LlvmPhiInstruction phi, LlvmBasicBlock? common, bool shared, Dictionary<string, ulong>? metrics)
    {
        int oldIndex = phi.GetBasicBlockIndex(block);
        int logicalType = phi.GetOperandUse(oldIndex).LogicalType;
        var oldValue = phi.RemoveIncomingValue(oldIndex, false);
        var incoming = new Dictionary<LlvmBasicBlock, LlvmValue?>();
        foreach (var predecessor in predecessors) incoming[predecessor] = null;
        if (shared) GatherIncomingValues(phi, incoming);
        List<int> newUndefined = [];
        if (oldValue is LlvmPhiInstruction oldPhi && ReferenceEquals(oldPhi.Parent, block))
        {
            for (int index = 0; index < oldPhi.IncomingCount; ++index)
            {
                var predecessor = oldPhi.GetIncomingBlock(index);
                if (ReferenceEquals(predecessor, common)) continue;
                AddIncoming(SelectIncomingValue(oldPhi.GetIncomingValue(index), predecessor, incoming), predecessor);
            }
            if (common is not null)
                AddIncoming(oldPhi.GetIncomingValue(oldPhi.GetBasicBlockIndex(common)), block, trackUndefined: false);
        }
        else
        {
            foreach (var predecessor in predecessors)
            {
                if (ReferenceEquals(predecessor, common)) continue;
                AddIncoming(SelectIncomingValue(oldValue, predecessor, incoming), predecessor);
            }
            if (common is not null) AddIncoming(oldValue, block, trackUndefined: false);
        }
        if (shared) ReplaceUndefinedValues(phi, incoming);
        else ReplaceMixedPoisonWithUndef(phi, newUndefined);
        Count(metrics, "local_cfg.empty_block.phis_redirected");

        void AddIncoming(LlvmValue value, LlvmBasicBlock predecessor, bool trackUndefined = true)
        {
            if (!shared && trackUndefined && value is LlvmUndefValue) newUndefined.Add(phi.IncomingCount);
            phi.AddIncoming(value, predecessor);
            phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = logicalType;
        }
    }

    private static LlvmValue SelectIncomingValue(LlvmValue oldValue, LlvmBasicBlock block,
        Dictionary<LlvmBasicBlock, LlvmValue?> incoming)
    {
        if (oldValue is LlvmUndefValue) return incoming.GetValueOrDefault(block) ?? oldValue;
        Debug.Assert(incoming.ContainsKey(block) && (incoming[block] is null || ReferenceEquals(incoming[block], oldValue)));
        incoming[block] = oldValue;
        return oldValue;
    }

    private static void GatherIncomingValues(LlvmPhiInstruction phi, Dictionary<LlvmBasicBlock, LlvmValue?> incoming)
    {
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            var value = phi.GetIncomingValue(index);
            if (value is LlvmUndefValue) continue;
            var predecessor = phi.GetIncomingBlock(index);
            if (incoming.ContainsKey(predecessor)) incoming[predecessor] = value;
        }
    }

    private static void ReplaceUndefinedValues(LlvmPhiInstruction phi, Dictionary<LlvmBasicBlock, LlvmValue?> incoming)
    {
        List<int> undefined = [];
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            if (phi.GetIncomingValue(index) is not LlvmUndefValue) continue;
            var predecessor = phi.GetIncomingBlock(index);
            if (!incoming.TryGetValue(predecessor, out var defined)) continue;
            if (defined is null) undefined.Add(index);
            else phi.SetIncomingValue(index, defined);
        }
        ReplaceMixedPoisonWithUndef(phi, undefined);
    }

    private static void ReplaceMixedPoisonWithUndef(LlvmPhiInstruction phi, List<int> undefined)
    {
        int poison = undefined.Count(index => phi.GetIncomingValue(index) is LlvmPoisonValue);
        if (poison == 0 || poison == undefined.Count) return;
        foreach (int index in undefined) phi.SetIncomingValue(index, LlvmUndefValue.Get(phi.Type));
    }
}
