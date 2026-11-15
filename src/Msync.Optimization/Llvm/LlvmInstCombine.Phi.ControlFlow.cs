// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombinePHI.cpp 1247–1395.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyPhiUsingControlFlow(LlvmPhiInstruction phi)
        {
            if (!phi.Operands.All(value => value is LlvmConstantInt) || !DT.IsReachableFromEntry(phi.Parent!)) return null;
            var block = phi.Parent!; var idom = DT.GetNode(block)!.IDom!.Block!;
            Dictionary<LlvmConstantInt,LlvmBasicBlock> successorForValue = [];
            Dictionary<LlvmBasicBlock,int> successorCount = [];
            void Add(LlvmConstantInt constant, LlvmBasicBlock successor)
            {
                successorForValue[constant] = successor; successorCount[successor] = successorCount.GetValueOrDefault(successor) + 1;
            }
            LlvmValue condition;
            if (idom.GetTerminator() is LlvmCondBrInstruction branch)
            {
                condition = branch.Condition;
                Add(LlvmConstantInt.GetTrue(phi.Context), branch.GetSuccessor(0));
                Add(LlvmConstantInt.GetFalse(phi.Context), branch.GetSuccessor(1));
            }
            else if (idom.GetTerminator() is LlvmSwitchInstruction sw)
            {
                condition = sw.Condition; successorCount[sw.DefaultDestination] = successorCount.GetValueOrDefault(sw.DefaultDestination) + 1;
                for (int i = 0; i < sw.CaseCount; ++i) Add(sw.GetCaseValue(i), sw.GetCaseDestination(i));
            }
            else return null;
            if (condition.Type != phi.Type) return null;
            bool? invert = null;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var input = (LlvmConstantInt)phi.GetIncomingValue(i); var predecessor = phi.GetIncomingBlock(i);
                bool Correct(LlvmConstantInt value) => successorForValue.TryGetValue(value, out var successor) &&
                    successorCount[successor] == 1 &&
                    (idom == predecessor && successor == block || DT.DominatesEdge(idom, successor, predecessor));
                bool needsInvert;
                if (Correct(input)) needsInvert = false;
                else if (Correct(LlvmConstantInt.Get(input.Context, ~input.Value))) needsInvert = true;
                else return null;
                if (invert is not null && invert != needsInvert) return null;
                invert = needsInvert;
            }
            Count("phi.controlflow_condition");
            if (invert == false) return condition;
            if (PhiInsertionPoint(block) is not { } point) return null;
            Builder.SetInsertPoint(point); return Builder.CreateNot(condition);
        }
        private LlvmValue? FoldDependentIVs(LlvmPhiInstruction phi)
        {
            if (phi.IncomingCount != 2) return null;
            LlvmValue? start = null; LlvmInstruction? next = null; LlvmBinaryOperator? secondNext = null;
            bool Match(LlvmValue a, LlvmValue b)
            {
                if (b is LlvmBinaryOperator binary)
                {
                    if (binary.Left == a && binary.Right is LlvmBinaryOperator right) secondNext = right;
                    else if (binary.Right == a && binary.Left is LlvmBinaryOperator left) secondNext = left;
                    else return false;
                }
                else if (b is LlvmGetElementPtrInstruction { IndexCount: 1 } gep && gep.PointerOperand == a && gep.GetIndex(0) is LlvmBinaryOperator index) secondNext = index;
                else return false;
                start = a; next = (LlvmInstruction)b; return true;
            }
            if (!Match(phi.GetIncomingValue(0), phi.GetIncomingValue(1)) && !Match(phi.GetIncomingValue(1), phi.GetIncomingValue(0))) return null;
            if (!LlvmValueTracking.MatchSimpleRecurrence(secondNext!, out var secondPhi, out var secondStart, out _) || secondPhi.Parent != phi.Parent) return null;
            var operation = next as LlvmBinaryOperator;
            var identity = operation is not null ? LlvmConstantFolding.BinOpIdentity(operation.Opcode, secondStart.Type, false) : LlvmConstant.GetNullValue(secondStart.Type);
            if (secondStart != identity) return null;
            Builder.SetInsertPoint(PhiInsertionPoint(phi.Parent!)!); Count("phi.dependent_iv_folded");
            if (operation is null)
            {
                var gep = (LlvmGetElementPtrInstruction)next!;
                return Builder.CreateGEP(gep.SourceElementType, start!, [secondPhi], noWrapFlags: gep.NoWrapFlags);
            }
            var result = Builder.CreateBinOp(operation.Opcode, secondPhi, start!);
            // The upstream cast asserts this remains an instruction.
            ((LlvmInstruction)result).CopyIRFlags(operation);
            return result;
        }
    }
}
