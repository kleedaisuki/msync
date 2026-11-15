// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp AggLoadStoreRewriter unfoldGEPSelect / unfoldGEPPhi.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private sealed partial class AggLoadStoreRewriter
    {
        private bool RewriteGep(LlvmGetElementPtrInstruction gep)
        {
            if (UnfoldGepSelect(gep) || UnfoldGepPhi(gep)) return true;
            EnqueueUsers(gep);
            return false;
        }

        private bool UnfoldGepSelect(LlvmGetElementPtrInstruction gep)
        {
            LlvmInstruction? selected = gep.PointerOperand as LlvmSelectInstruction;
            foreach (LlvmValue index in gep.Indices)
            {
                if (index is LlvmSelectInstruction select)
                {
                    if (selected is not null) return false;
                    selected = select;
                    if (select.TrueValue is not LlvmConstantInt || select.FalseValue is not LlvmConstantInt) return false;
                    continue;
                }
                if (index is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extend)
                {
                    if (selected is not null) return false;
                    selected = extend;
                    if (!extend.Operand.Type.IsIntegerTy(1)) return false;
                    continue;
                }
                if (index is not LlvmConstantInt) return false;
            }
            if (selected is null) return false;
            LlvmValue condition, whenTrue, whenFalse;
            if (selected is LlvmSelectInstruction selectInstruction)
                (condition, whenTrue, whenFalse) = (selectInstruction.Condition, selectInstruction.TrueValue, selectInstruction.FalseValue);
            else
            {
                condition = selected.GetOperand(0)!;
                whenTrue = LlvmConstantInt.Get((LlvmIntegerType)selected.Type, 1);
                whenFalse = LlvmConstantInt.Get((LlvmIntegerType)selected.Type, 0);
            }
            LlvmValue[] trueOperands = SubstituteGepOperand(gep, selected, whenTrue);
            LlvmValue[] falseOperands = SubstituteGepOperand(gep, selected, whenFalse);
            builder.SetInsertPoint(gep);
            LlvmValue trueGep = builder.CreateGep(gep.SourceElementType, trueOperands[0], trueOperands[1..],
                whenTrue.Name + ".sroa.gep", gep.NoWrapFlags);
            LlvmValue falseGep = builder.CreateGep(gep.SourceElementType, falseOperands[0], falseOperands[1..],
                whenFalse.Name + ".sroa.gep", gep.NoWrapFlags);
            LlvmValue replacement = builder.CreateSelect(condition, trueGep, falseGep, selected.Name + ".sroa.sel");
            var instruction = (LlvmInstruction)replacement;
            instruction.LogicalType = gep.LogicalType;
            if (instruction is LlvmSelectInstruction merged)
            {
                merged.GetOperandUse(1).LogicalType = gep.LogicalType;
                merged.GetOperandUse(2).LogicalType = gep.LogicalType;
            }
            visited.Remove(gep);
            gep.ReplaceAllUsesWith(replacement);
            gep.EraseFromParent();
            visited.Add(instruction);
            EnqueueUsers(instruction);
            Count("aggregate.gep-select.unfolded");
            return true;
        }

        private bool UnfoldGepPhi(LlvmGetElementPtrInstruction gep)
        {
            var phi = gep.PointerOperand as LlvmPhiInstruction;
            if (phi is not null)
            {
                if (phi.Operands.Any(static value => InvalidUnfoldedPointer(value!))) return false;
            }
            else if (InvalidUnfoldedPointer(gep.PointerOperand)) return false;
            foreach (LlvmValue index in gep.Indices)
            {
                if (index is LlvmPhiInstruction indexPhi)
                {
                    if (phi is not null) return false;
                    phi = indexPhi;
                    if (phi.Operands.Any(static value => value is not LlvmConstantInt)) return false;
                    continue;
                }
                if (index is not LlvmConstantInt) return false;
            }
            if (phi is null) return false;
            builder.SetInsertPoint(phi);
            LlvmPhiInstruction replacement = builder.CreatePhi(gep.Type, phi.IncomingCount, phi.Name + ".sroa.phi");
            replacement.LogicalType = gep.LogicalType;
            builder.SetInsertPoint(gep.Parent!.Parent!.BasicBlocks[0].GetTerminator());
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                LlvmValue value = phi.GetIncomingValue(index);
                LlvmBasicBlock predecessor = phi.GetIncomingBlock(index);
                int existing = replacement.GetBasicBlockIndex(predecessor);
                LlvmValue incoming;
                if (existing >= 0) incoming = replacement.GetIncomingValue(existing);
                else
                {
                    LlvmValue[] operands = SubstituteGepOperand(gep, phi, value);
                    incoming = builder.CreateGep(gep.SourceElementType, operands[0], operands[1..],
                        phi.Name + ".sroa.gep", gep.NoWrapFlags);
                    if (incoming is LlvmInstruction incomingInstruction) incomingInstruction.LogicalType = gep.LogicalType;
                }
                replacement.AddIncoming(incoming, predecessor);
                replacement.GetOperandUse(replacement.IncomingCount - 1).LogicalType = gep.LogicalType;
            }
            visited.Remove(gep);
            gep.ReplaceAllUsesWith(replacement);
            gep.EraseFromParent();
            visited.Add(replacement);
            EnqueueUsers(replacement);
            Count("aggregate.gep-phi.unfolded");
            return true;
        }

        private static bool InvalidUnfoldedPointer(LlvmValue value) =>
            value is LlvmInstruction && value is not LlvmAllocaInstruction { IsStaticAlloca: true };

        private static LlvmValue[] SubstituteGepOperand(LlvmGetElementPtrInstruction gep, LlvmValue original, LlvmValue replacement)
        {
            LlvmValue[] operands = new LlvmValue[gep.OperandCount];
            for (int index = 0; index < operands.Length; ++index)
            {
                LlvmValue value = gep.GetOperand(index)!;
                operands[index] = value == original ? replacement : value;
            }
            return operands;
        }
    }
}
