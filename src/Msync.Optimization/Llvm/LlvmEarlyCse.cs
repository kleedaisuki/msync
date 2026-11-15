// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/EarlyCSE.cpp,
// EarlyCSE::run/processNode, with optional MemorySSA generation refinement.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    public static bool Run(LlvmFunction function, CompilationTrace? trace = null, LlvmDominatorTree? tree = null)
        => Run(function, false, trace, tree);

    private sealed partial class Work
    {
        private readonly LlvmFunction function;
        private readonly LlvmDominatorTree tree;
        private readonly CompilationTrace? trace;
        private readonly ScopedTable<LlvmInstruction, LlvmValue> values = new(new SimpleComparer());
        private readonly ScopedTable<LlvmValue, LoadValue> loads = new(ReferenceEqualityComparer.Instance);
        private readonly ScopedTable<GepValue, LlvmValue> geps = new(new GepComparer());
        private uint generation;
        private bool changed;
        internal Dictionary<string, ulong>? Metrics { get; }
        internal int InstructionCount => function.BasicBlocks.Sum(block => block.Instructions.Count);
        private LlvmDataLayout Layout => function.Parent!.DataLayout;
        internal Work(LlvmFunction function, LlvmDominatorTree tree, CompilationTrace? trace)
        { this.function = function; this.tree = tree; this.trace = trace; Metrics = trace is null ? null : []; }

        private sealed class Frame(LlvmDominatorTreeNode node, uint entryGeneration, int valueMark, int loadMark, int gepMark, int callMark, int invariantMark)
        {
            internal readonly LlvmDominatorTreeNode Node = node;
            internal readonly uint EntryGeneration = entryGeneration;
            internal uint ChildGeneration = entryGeneration;
            internal bool Processed;
            internal readonly IEnumerator<LlvmDominatorTreeNode> Children = node.Children.GetEnumerator();
            internal readonly int ValueMark = valueMark, LoadMark = loadMark, GepMark = gepMark, CallMark = callMark, InvariantMark = invariantMark;
        }

        internal bool Run()
        {
            if (tree.RootNode is not { } root) return false;
            List<Frame> stack = [new(root, 0, values.Mark, loads.Mark, geps.Mark, calls.Mark, invariants.Mark)];
            while (stack.Count != 0)
            {
                var frame = stack[^1];
                generation = frame.EntryGeneration;
                if (!frame.Processed)
                {
                    ProcessNode(frame.Node.Block!);
                    frame.ChildGeneration = generation;
                    frame.Processed = true;
                    continue;
                }
                if (frame.Children.MoveNext())
                {
                    stack.Add(new(frame.Children.Current, frame.ChildGeneration, values.Mark, loads.Mark, geps.Mark, calls.Mark, invariants.Mark));
                    continue;
                }
                values.Pop(frame.ValueMark); loads.Pop(frame.LoadMark); geps.Pop(frame.GepMark); calls.Pop(frame.CallMark); invariants.Pop(frame.InvariantMark);
                frame.Children.Dispose(); stack.RemoveAt(stack.Count - 1);
                Count("scope.popped");
            }
            return changed;
        }

        private void ProcessNode(LlvmBasicBlock block)
        {
            Count("block.visited");
            var predecessor = SinglePredecessor(block);
            if (predecessor is null) AdvanceGeneration("memory.kill.multiple_predecessors");
            if (predecessor?.GetTerminatorOrNull() is LlvmCondBrInstruction branch &&
                branch.Condition is LlvmInstruction condition && CanHandleSimple(condition))
                HandleBranchCondition(condition, branch, block, predecessor);
            LlvmInstruction? lastStore = null;
            for (var instruction = block.FirstInstruction; instruction is not null;)
            {
                var next = instruction.Next;
                ProcessInstruction(instruction, ref lastStore);
                instruction = next;
            }
        }

        private void ProcessInstruction(LlvmInstruction instruction, ref LlvmInstruction? lastStore)
        {
            Count("instruction.visited");
            if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
            { Erase(instruction, "dead"); return; }
            if (ProcessSpecialIntrinsic(instruction)) return;
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, tree, Metrics) is { } simplified)
            {
                Count("instruction.simplified");
                if (!instruction.UseEmpty) { instruction.ReplaceAllUsesWith(simplified); changed = true; }
                if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
                { Erase(instruction, "simplified"); return; }
            }
            if (instruction.MayThrow()) lastStore = null;
            if (CanHandleSimple(instruction))
            {
                Count("scalar.lookup");
                if (values.Lookup(instruction) is { } available)
                {
                    CombineFlags(instruction, available);
                    instruction.ReplaceAllUsesWith(available);
                    Erase(instruction, "scalar-cse"); return;
                }
                values.Insert(instruction, instruction); Count("scalar.insert"); return;
            }
            if (instruction is LlvmLoadInstruction load)
            { ProcessLoad(load, ref lastStore); return; }
            var memory = new MemoryInst(instruction, Metrics);
            if (instruction.MayReadFromMemory() && !(memory.IsValid && !memory.MayReadFromMemory)) lastStore = null;
            if (CanHandleCall(instruction) && !memory.IsStore && !IsMemSet(instruction))
            { ProcessCall((LlvmCallInstruction)instruction); return; }
            if (instruction is LlvmCallBase)
                Count(memory.IsMemSet ? "call.memset_memory_path" : function.IsPresplitCoroutine
                    ? "call.presplit_coroutine" : "call.readwrite_memory_effects");
            if (instruction is LlvmGetElementPtrInstruction gep)
            {
                var offset = LlvmAPInt.GetZero(Layout.GetIndexTypeSizeInBits(gep.Type));
                var key = new GepValue(gep, LlvmValueTracking.AccumulateConstantOffset(gep, Layout, ref offset, Metrics)
                    ? offset.TrySExtValue() : null);
                Count("gep.lookup");
                if (geps.Lookup(key) is { } available)
                {
                    CombineFlags(gep, available); gep.ReplaceAllUsesWith(available);
                    Erase(gep, "gep-cse"); return;
                }
                geps.Insert(key, gep); Count("gep.insert"); return;
            }
            if (memory.IsValid && memory.IsStore && ProcessWriteback(memory)) return;
            if (!instruction.MayWriteToMemory()) return;
            AdvanceGeneration("memory.kill.writer");
            if (memory.IsValid && memory.IsStore) ProcessStore(memory, ref lastStore);
        }

        private void AdvanceGeneration(string cause) { ++generation; Count(cause); }
        private void Count(string key)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + 1; }
        private void Erase(LlvmInstruction instruction, string reason)
        {
            Count("instruction.erased");
            RemoveMemorySsa(instruction);
            if (trace is null) instruction.EraseFromParent();
            else
            {
                Count("erase." + reason);
                var attributes = new Dictionary<string, string>
                {
                    ["function.name"] = function.Name,
                    ["block.name"] = instruction.Parent!.Name,
                    ["instruction.name"] = instruction.Name,
                    ["instruction.opcode"] = instruction.Opcode.ToString(),
                    ["rewrite.reason"] = reason
                };
                if (instruction.Source is { } source)
                {
                    attributes["source.name"] = source.Source;
                    attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                trace.Run("optimizer.llvm-early-cse.erase", instruction, static i => i.EraseFromParent(), attributes);
            }
            changed = true;
        }
        private void CombineFlags(LlvmInstruction from, LlvmValue to)
        {
            if (to is not LlvmInstruction target) return;
            if (target.IsFloatingMathOperator || target.HasPoisonGeneratingFlags() &&
                !LlvmValueTracking.ProgramUndefinedIfPoison(target, Metrics)) target.AndIRFlags(from);
            IntersectCallAttributes(from, to);
        }
        private static LlvmBasicBlock? SinglePredecessor(LlvmBasicBlock block)
        {
            using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
            if (!predecessors.MoveNext()) return null;
            var result = predecessors.Current;
            return predecessors.MoveNext() ? null : result;
        }
    }
}
