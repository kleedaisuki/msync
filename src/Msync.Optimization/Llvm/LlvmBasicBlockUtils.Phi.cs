// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Local.cpp
// EliminateDuplicatePHINodes{Naive,SetBased}Impl and default threshold 32.
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmBasicBlockUtils
{
    public static bool EliminateDuplicatePHINodes(LlvmBasicBlock block, CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null)
    {
        HashSet<LlvmPhiInstruction> removed = [];
        bool changed = EliminateDuplicatePHINodes(block, removed, trace, metrics);
        foreach (var phi in removed) phi.EraseFromParent();
        return changed;
    }
    public static bool EliminateDuplicatePHINodes(LlvmBasicBlock block, HashSet<LlvmPhiInstruction> toRemove,
        CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        int count = 0;
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction; instruction = instruction.Next)
            if (++count > 32) return EliminateDuplicateSet(block, toRemove, trace, metrics);
        return EliminateDuplicateNaive(block, toRemove, trace, metrics);
    }
    private static bool EliminateDuplicateNaive(LlvmBasicBlock block, HashSet<LlvmPhiInstruction> removed,
        CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        bool changed = false;
        var instruction = block.FirstInstruction;
        while (instruction is LlvmPhiInstruction phi)
        {
            instruction = instruction.Next;
            for (var candidate = instruction; candidate is LlvmPhiInstruction duplicate; candidate = candidate.Next)
            {
                if (removed.Contains(duplicate)) continue;
                Count(trace, metrics, "cfg.phicse.comparisons");
                if (!duplicate.IsIdenticalToWhenDefined(phi)) continue;
                phi.AndIRFlags(duplicate);
                duplicate.ReplaceAllUsesWith(phi);
                removed.Add(duplicate);
                changed = true;
                instruction = block.FirstInstruction;
                Count(trace, metrics, "cfg.phicse.removed");
                break;
            }
        }
        return changed;
    }
    private static bool EliminateDuplicateSet(LlvmBasicBlock block, HashSet<LlvmPhiInstruction> removed,
        CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        var comparer = new PhiComparer(trace, metrics);
        HashSet<LlvmPhiInstruction> phis = new(128, comparer);
        bool changed = false;
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi;)
        {
            instruction = instruction.Next;
            if (removed.Contains(phi)) continue;
            if (!phis.TryGetValue(phi, out var prior)) { phis.Add(phi); continue; }
            prior.AndIRFlags(phi);
            phi.ReplaceAllUsesWith(prior);
            removed.Add(phi);
            changed = true;
            phis.Clear();
            instruction = block.FirstInstruction;
            Count(trace, metrics, "cfg.phicse.removed");
        }
        return changed;
    }
    private sealed class PhiComparer(CompilationTrace? trace, Dictionary<string, ulong>? metrics) : IEqualityComparer<LlvmPhiInstruction>
    {
        public bool Equals(LlvmPhiInstruction? left, LlvmPhiInstruction? right)
        {
            Count(trace, metrics, "cfg.phicse.comparisons");
            return ReferenceEquals(left, right) || left is not null && right is not null && left.IsIdenticalToWhenDefined(right);
        }
        public int GetHashCode(LlvmPhiInstruction phi)
        {
            var hash = new HashCode();
            foreach (var operand in phi.Operands) hash.Add(operand is null ? 0 : RuntimeHelpers.GetHashCode(operand));
            for (int i = 0; i < phi.IncomingCount; ++i) hash.Add(RuntimeHelpers.GetHashCode(phi.GetIncomingBlock(i)));
            Count(trace, metrics, "cfg.phicse.hashes");
            return hash.ToHashCode();
        }
    }
}
