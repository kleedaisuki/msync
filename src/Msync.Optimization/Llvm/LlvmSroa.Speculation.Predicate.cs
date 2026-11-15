// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp rewriteMemOpOfSelect.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class State
    {
        private void RewriteMemOpOfSelect(LlvmSelectInstruction select, LlvmInstruction instruction,
            SelectHandSpeculativity spec, LlvmTransformBuilder builder, LlvmDomTreeUpdater updater)
        {
            Debug.Assert(instruction is LlvmLoadInstruction or LlvmStoreInstruction);
            LlvmBasicBlock head = instruction.Parent!;
            if (spec.AreNoneSpeculatable)
            {
                LlvmBasicBlock? thenBlock = null, elseBlock = null;
                LlvmBasicBlockUtils.SplitBlockAndInsertIfThenElse(select.Condition, instruction,
                    ref thenBlock, ref elseBlock, updater: updater, metrics: Metrics);
            }
            else
            {
                LlvmBasicBlockUtils.SplitBlockAndInsertIfThen(select.Condition, instruction, updater: updater, metrics: Metrics);
                if (spec.IsSpeculatable(true)) ((LlvmCondBrInstruction)head.GetTerminator()).SwapSuccessors();
            }
            var branch = (LlvmCondBrInstruction)head.GetTerminator();
            LlvmBasicBlock tail = instruction.Parent!;
            tail.SetName(head.Name + ".cont");
            LlvmPhiInstruction? phi = null;
            if (instruction is LlvmLoadInstruction)
            {
                phi = new LlvmPhiInstruction(instruction.Type, 2) { Source = instruction.Source, LogicalType = instruction.LogicalType };
                tail.InsertBefore(phi, instruction);
            }
            foreach (LlvmBasicBlock successor in head.GetTerminator().Successors)
            {
                bool isThen = successor == branch.GetSuccessor(0);
                LlvmBasicBlock destination = successor == tail ? head : successor;
                LlvmInstruction replacement = instruction.Clone();
                if (destination != head)
                {
                    destination.SetName(head.Name + (isThen ? ".then" : ".else"));
                    Count(instruction is LlvmLoadInstruction ? "loads.predicated" : "stores.predicated");
                }
                else
                {
                    // Loads/stores have no represented UB-implying metadata.
                    Count("loads.speculated");
                }
                destination.InsertBefore(replacement, destination.GetTerminator());
                LlvmValue pointer = isThen ? select.TrueValue : select.FalseValue;
                int pointerIndex = instruction is LlvmLoadInstruction ? 0 : 1;
                builder.SetInsertPoint(replacement);
                pointer = builder.CreateCast(LlvmOpcode.AddrSpaceCast, pointer, instruction.GetOperand(pointerIndex)!.Type,
                    instruction.Name + (isThen ? ".then" : ".else") + ".cast");
                replacement.SetOperand(pointerIndex, pointer);
                if (phi is null) continue;
                replacement.SetName(instruction.Name + (isThen ? ".then" : ".else") + ".val");
                phi.AddIncoming(replacement, destination);
                phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = instruction.LogicalType;
            }
            if (phi is not null)
            {
                phi.TakeName(instruction);
                instruction.ReplaceAllUsesWith(phi);
            }
            Count("predication.select.cfg-rewrites");
        }
    }
}
