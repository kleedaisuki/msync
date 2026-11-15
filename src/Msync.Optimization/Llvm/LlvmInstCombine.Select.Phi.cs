// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectToPhi[Impl].
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectToPhiImpl(LlvmSelectInstruction select, LlvmBasicBlock block)
        {
            LlvmDominatorTreeNode? node = DT.GetNode(block)!.IDom;
            if (node is null) return null;
            LlvmBasicBlock dominator = node.Block!;
            if (dominator.GetTerminator() is not LlvmCondBrInstruction branch) return null;
            LlvmValue whenTrue, whenFalse;
            if (ReferenceEquals(branch.Condition, select.Condition)) { whenTrue = select.TrueValue; whenFalse = select.FalseValue; }
            else if (SelectMatchNot(branch.Condition, out LlvmValue uninverted) && ReferenceEquals(uninverted, select.Condition))
            { whenTrue = select.FalseValue; whenFalse = select.TrueValue; }
            else return null;
            LlvmBasicBlock trueSuccessor = branch.GetSuccessor(0), falseSuccessor = branch.GetSuccessor(1);
            if (ReferenceEquals(trueSuccessor, falseSuccessor)) return null;
            Dictionary<LlvmBasicBlock, LlvmValue> inputs = [];
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block))
            {
                LlvmValue value;
                if (DT.DominatesEdge(dominator, trueSuccessor, predecessor, block)) value = whenTrue.DoPHITranslation(block, predecessor);
                else if (DT.DominatesEdge(dominator, falseSuccessor, predecessor, block)) value = whenFalse.DoPHITranslation(block, predecessor);
                else return null;
                inputs[predecessor] = value;
                if (value is LlvmInstruction instruction && !DT.Dominates(instruction, predecessor.GetTerminator())) return null;
            }
            Builder.SetInsertPoint(block, block.FirstInstruction);
            LlvmPhiInstruction phi = Builder.CreatePHI(select.Type, inputs.Count);
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block)) phi.AddIncoming(inputs[predecessor], predecessor);
            phi.TakeName(select); phi.LogicalType = select.LogicalType; phi.Source = select.Source;
            Count("select.to_phi.rewrites");
            return phi;
        }
        private LlvmInstruction? FoldSelectToPhi(LlvmSelectInstruction select)
        {
            List<LlvmBasicBlock> candidates = [select.Parent!];
            foreach (LlvmValue? operand in select.Operands)
                if (operand is LlvmInstruction instruction && !candidates.Contains(instruction.Parent!)) candidates.Add(instruction.Parent!);
            foreach (LlvmBasicBlock candidate in candidates)
                if (FoldSelectToPhiImpl(select, candidate) is { } phi) return phi;
            return null;
        }
    }
}
