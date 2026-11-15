// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Scalar/InstSimplifyPass.cpp runImpl;
// Transforms/Utils/Local.cpp RecursivelyDeleteTriviallyDeadInstructions.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static class LlvmInstSimplifyPass
{
    public static bool Run(LlvmFunction function, CompilationTrace? trace = null, LlvmDominatorTree? tree = null)
    {
        trace = trace?.Detail;
        tree ??= new LlvmDominatorTree(function, trace);
        var work = new Work(function, tree, trace);
        return trace is null ? work.Run() : trace.MeasureDetailed("optimizer.pass.llvm-instsimplify", work,
            static state => state.Run(), input: new CompilationSize(1, work.InstructionCount, 0),
            size: _ => new CompilationSize(1, work.InstructionCount, 0),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name },
            metrics: _ => work.Metrics!);
    }

    private sealed class Work(LlvmFunction function, LlvmDominatorTree tree, CompilationTrace? trace)
    {
        private HashSet<LlvmInstruction> toSimplify = [];
        private HashSet<LlvmInstruction> next = [];
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        internal int InstructionCount => function.BasicBlocks.Sum(block => block.Instructions.Count);
        private bool changed;

        internal bool Run()
        {
            do
            {
                Count("iterations");
                if (trace is null) Scan();
                else trace.Run("optimizer.llvm-instsimplify.iteration", this, static state => state.Scan());
                (toSimplify, next) = (next, toSimplify);
                next.Clear();
            } while (toSimplify.Count != 0);
            return changed;
        }

        private void Scan()
        {
            foreach (LlvmBasicBlock block in function.BasicBlocks)
            {
                Count("blocks.visited");
                if (!tree.IsReachableFromEntry(block)) { Count("blocks.unreachable"); continue; }
                List<LlvmInstruction> dead = [];
                foreach (LlvmInstruction instruction in block.Instructions)
                {
                    Count("instructions.visited");
                    if (toSimplify.Count != 0 && !toSimplify.Contains(instruction))
                    { Count("instructions.not_queued"); continue; }
                    if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
                    { dead.Add(instruction); changed = true; Count("instructions.initially_dead"); }
                    else if (!instruction.UseEmpty) Simplify(instruction, dead);
                }
                if (trace is null) DeleteDead(dead);
                else trace.Run("optimizer.llvm-instsimplify.dead-cleanup", (Work: this, Dead: dead),
                    static state => state.Work.DeleteDead(state.Dead),
                    new Dictionary<string, string> { ["block.name"] = block.Name });
            }
        }

        private void Simplify(LlvmInstruction instruction, List<LlvmInstruction> dead)
        {
            Count("instructions.simplify_queries");
            LlvmValue? value = LlvmInstructionSimplify.SimplifyInstruction(instruction, tree, Metrics);
            if (value is null) { Count("instructions.no_fold"); return; }
            foreach (LlvmUser user in instruction.Users)
            {
                if (next.Add((LlvmInstruction)user)) Count("users.queued");
                else Count("users.duplicate");
            }
            if (trace is null) Replace(instruction, value);
            else trace.Run("optimizer.llvm-instsimplify.rewrite", (Instruction: instruction, Value: value),
                static state => state.Instruction.ReplaceAllUsesWith(state.Value), SourceAttributes(instruction, value));
            Count("instructions.simplified");
            changed = true;
            if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics)) dead.Add(instruction);
        }

        private static void Replace(LlvmInstruction instruction, LlvmValue value) => instruction.ReplaceAllUsesWith(value);

        private void DeleteDead(List<LlvmInstruction> dead)
        {
            while (dead.Count != 0)
            {
                LlvmInstruction instruction = dead[^1];
                dead.RemoveAt(dead.Count - 1);
                Count("dead.popped");
                // Parent=null represents an erased WeakTrackingVH entry in this
                // component; queued dead instructions have no users to RAUW.
                if (instruction.Parent is null) { Count("dead.already_erased"); continue; }
                Debug.Assert(LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction));
                Debug.Assert(instruction.UseEmpty);
                foreach (LlvmUse use in instruction.OperandUses)
                {
                    LlvmValue operand = use.Value!;
                    use.Set(null);
                    Count("dead.operands_detached");
                    if (!operand.UseEmpty) continue;
                    if (operand is LlvmInstruction producer && LlvmDeadCodeElimination.IsInstructionTriviallyDead(producer, Metrics))
                    { dead.Add(producer); Count("dead.producers_queued"); }
                }
                instruction.EraseFromParent();
                Count("dead.erased");
            }
        }

        private void Count(string key)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + 1; }

        private Dictionary<string, string> SourceAttributes(LlvmInstruction instruction, LlvmValue replacement)
        {
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["block.name"] = instruction.Parent!.Name,
                ["instruction.opcode"] = instruction.Opcode.ToString(),
                ["replacement.kind"] = replacement.GetType().Name
            };
            if (instruction.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
