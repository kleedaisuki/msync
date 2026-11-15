// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp redundant/end-of-function stores.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private bool WriteAtEnd(LlvmMemoryDef definition, LlvmMemoryLocation location)
        {
            var work = new List<LlvmMemoryAccess>();
            var visited = new HashSet<LlvmMemoryAccess>();
            PushMemoryUses(definition, work, visited);
            for (int index = 0; index < work.Count; ++index)
            {
                if (work.Count >= options.ScanLimit) { Count("end_of_function.scan_limit"); return false; }
                var access = work[index];
                if (access is LlvmMemoryPhi)
                {
                    if (!IsLoopInvariant(location.Ptr!)) return false;
                    PushMemoryUses(access, work, visited);
                    continue;
                }
                if (IsReadClobber(location, ((LlvmMemoryUseOrDef)access).MemoryInstruction!)) return false;
                if (access is LlvmMemoryDef) PushMemoryUses(access, work, visited);
            }
            return true;
        }
        private bool EliminateAtEndOfFunction()
        {
            bool changed = false;
            do
            {
                iterateEndOfFunction = false;
                Count("end_of_function.iterations");
                for (int index = definitions.Count; index-- > 0;)
                {
                    var definition = definitions[index];
                    if (skipped.Contains(definition)) continue;
                    var instruction = definition.MemoryInstruction!;
                    var location = GetWriteLocation(instruction);
                    if (location is null || !IsRemovable(instruction)) continue;
                    if (!InvisibleAfterReturn(Underlying(location.Value.Ptr!), location.Value)) continue;
                    if (!WriteAtEnd(definition, location.Value)) continue;
                    Delete(instruction);
                    Count("stores.dead_after_return");
                    changed = true;
                }
            } while (iterateEndOfFunction);
            return changed;
        }
        private bool EliminateRedundantExistingValues()
        {
            bool changed = false;
            foreach (var definition in definitions)
            {
                if (skipped.Contains(definition) || memory.IsLiveOnEntryDef(definition)) continue;
                var instruction = definition.MemoryInstruction!;
                var location = GetWriteLocation(instruction);
                if (location is null || !IsRemovable(instruction)) continue;
                var upper = (definition.IsOptimized ? definition.GetOptimized() : definition.DefiningAccess) as LlvmMemoryDef;
                if (upper is null || memory.IsLiveOnEntryDef(upper)) continue;
                // The represented memory-write instructions have no memset ID.
                if (!instruction.IsIdenticalToWhenDefined(upper.MemoryInstruction!, intersectAttrs: true)
                    || IsReadClobber(location.Value, instruction)) continue;
                Delete(instruction);
                Count("stores.redundant_existing_value");
                changed = true;
            }
            return changed;
        }
        private readonly record struct Condition(LlvmValue Pointer, LlvmValue Value, LlvmInstruction Load, LlvmBasicBlock Successor);
        private static IReadOnlyList<Condition> DominatingConditions(LlvmBasicBlock block, LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
        {
            if (block.GetTerminator() is not LlvmCondBrInstruction branch || branch.GetSuccessor(0) == branch.GetSuccessor(1)) return [];
            if (branch.Condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true, Operand: LlvmLoadInstruction load } cast)
                return [new(load.PointerOperand, LlvmConstantInt.Get((LlvmIntegerType)load.Type, 1), load, branch.GetSuccessor(0)),
                    new(load.PointerOperand, LlvmConstantInt.Get((LlvmIntegerType)load.Type, 0), load, branch.GetSuccessor(1))];
            if (branch.Condition is not LlvmICmpInstruction comparison
                || comparison.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)) return [];
            LlvmLoadInstruction? comparedLoad = comparison.Left as LlvmLoadInstruction;
            LlvmValue storedValue = comparison.Right;
            if (comparedLoad is null) { comparedLoad = comparison.Right as LlvmLoadInstruction; storedValue = comparison.Left; }
            if (comparedLoad is null) return [];
            if (storedValue.Type.IsPointerTy() && !LlvmCmpInstAnalysis.CanReplacePointersIfEqual(storedValue, comparedLoad, layout, metrics)) return [];
            return [new(comparedLoad.PointerOperand, storedValue, comparedLoad,
                branch.GetSuccessor(comparison.Predicate == LlvmCmpPredicate.ICmpEq ? 0 : 1))];
        }
        private bool EliminateViaDominatingConditions()
        {
            bool changed = false;
            var active = new Dictionary<(LlvmValue, LlvmValue), Stack<LlvmInstruction>>();
            void Visit(LlvmDominatorTreeNode node, uint depth)
            {
                if (depth > options.MaxDomConditionDepth) { Count("dom_conditions.depth_limit"); return; }
                var block = node.Block!;
                if (memory.GetBlockDefs(block) is { } accesses)
                {
                    foreach (var definition in accesses.OfType<LlvmMemoryDef>().ToArray())
                    {
                        if (definition.MemoryInstruction is not LlvmStoreInstruction { IsUnordered: true } store) continue;
                        if (!active.TryGetValue((store.PointerOperand, store.ValueOperand), out var stack) || stack.Count == 0) continue;
                        var load = stack.Peek();
                        var loadAccess = memory.GetMemoryAccess(load)!;
                        var clobber = memory.GetSkipSelfWalker().GetClobberingMemoryAccess(definition, alias, Metrics);
                        if (!memory.Dominates(clobber, loadAccess)) continue;
                        Delete(store);
                        Count("stores.dominating_condition");
                        changed = true;
                    }
                }
                var conditions = DominatingConditions(block, layout, Metrics);
                foreach (var child in node.Children)
                {
                    var scoped = new List<(LlvmValue, LlvmValue)>();
                    foreach (var condition in conditions)
                    {
                        if (!dominators.DominatesEdge(block, condition.Successor, child.Block!)) continue;
                        var key = (condition.Pointer, condition.Value);
                        if (!active.TryGetValue(key, out var stack)) active.Add(key, stack = new());
                        stack.Push(condition.Load);
                        scoped.Add(key);
                    }
                    Visit(child, depth + 1);
                    foreach (var key in scoped) active[key].Pop();
                }
            }
            Visit(dominators.RootNode!, 0);
            return changed;
        }
    }
}
