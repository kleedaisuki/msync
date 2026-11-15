// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SCCP.cpp runSCCP, Utils/SCCPSolver.cpp constant replacement.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

// Source component: function-local solver + constant/CFG rewrites. PredicateInfo,
// attributes and the later range-driven instruction refinements are separate ports.
public static partial class LlvmSccp
{
    public static bool Run(LlvmFunction function, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        var work = new Rewrite(function, trace);
        if (trace is null) return work.Run();
        return trace.MeasureDetailed("optimizer.pass.llvm-sccp", work, static state => state.Run(),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name }, metrics: _ => work.Solver.Metrics!);
    }
    private sealed partial class Rewrite(LlvmFunction function, CompilationTrace? trace)
    {
        internal readonly LlvmSccpSolver Solver = new(function, trace);
        private LlvmBasicBlock? unreachableDefault;
        internal bool Run()
        {
            Solver.InitializeFunction();
            do { Solver.Solve(); } while (Solver.ResolvedUndefsIn());
            bool changed = false;
            var dead = new List<LlvmBasicBlock>();
            foreach (var block in function.BasicBlocks)
            {
                if (!Solver.IsBlockExecutable(block)) { dead.Add(block); Count("rewrite.dead_blocks"); changed = true; continue; }
                if (trace is null) changed |= ReplaceConstants(block);
                else changed |= trace.MeasureDetailed("optimizer.llvm-sccp.replace-constants", (Self: this, Block: block),
                    static state => state.Self.ReplaceConstants(state.Block), attributes: BlockAttributes(block));
            }
            foreach (var block in dead) ChangeToUnreachable(block);
            foreach (var block in function.BasicBlocks.ToArray()) changed |= RemoveNonFeasibleEdges(block);
            foreach (var block in dead) DeleteBlock(block);
            return changed;
        }
        private bool ReplaceConstants(LlvmBasicBlock block)
        {
            bool changed = false;
            for (var instruction = block.FirstInstruction; instruction is not null;)
            {
                var current = instruction; instruction = instruction.Next;
                if (current.Type.IsVoidTy()) continue;
                Count("rewrite.constant_candidates");
                if (Solver.GetConstantOrNull(current) is not { } constant) continue;
                if (current is LlvmCallBase call &&
                    (call is LlvmCallInstruction { TailCallKind: LlvmTailCallKind.MustTail } &&
                        !LlvmDeadCodeElimination.WouldInstructionBeTriviallyDead(call) || call.GetOperandBundle("clang.arc.attachedcall") is not null))
                { Count("rewrite.preserved_call_result_contract"); continue; }
                if (current.Type.IsPointerTy())
                    Debug.Assert(!Solver.GetLatticeValueFor(current).MayHaveDifferentProvenance,
                        "PredicateInfo provenance replacement is not part of this component");
                if (trace is null) Replace(current, constant);
                else trace.Run("optimizer.llvm-sccp.constant", (Self: this, Instruction: current, Constant: constant),
                    static state => state.Self.Replace(state.Instruction, state.Constant), InstructionAttributes(current));
                changed = true;
            }
            return changed;
        }
        private void Replace(LlvmInstruction instruction, LlvmConstant constant)
        {
            Count("rewrite.constant_uses", (ulong)instruction.GetNumUses());
            instruction.ReplaceAllUsesWith(constant); Count("rewrite.constants");
            if (!LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction))
            { Count("rewrite.effects_retained"); return; }
            instruction.EraseFromParent(); Count("rewrite.instructions_removed");
        }
        private void Count(string name, ulong amount = 1)
        {
            if (Solver.Metrics is Dictionary<string, ulong> metrics) metrics[name] = metrics.GetValueOrDefault(name) + amount;
        }
        private Dictionary<string, string> BlockAttributes(LlvmBasicBlock block) => new()
        { ["function.name"] = function.Name, ["block.name"] = block.Name };
        private Dictionary<string, string> InstructionAttributes(LlvmInstruction instruction)
        {
            var attributes = BlockAttributes(instruction.Parent!);
            attributes["instruction.name"] = instruction.Name;
            attributes["instruction.opcode"] = instruction.Opcode.ToString();
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
