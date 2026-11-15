// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// cloneInstructionsIntoPredecessorBlockAndUpdateSSAUses.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private void CloneInstructionsIntoPredecessorBlockAndUpdateSsaUses(LlvmBasicBlock block,
            LlvmBasicBlock predecessor, IDictionary<LlvmValue, LlvmValue> values)
        {
            LlvmInstruction predecessorTerminator = predecessor.GetTerminator();
            foreach (LlvmInstruction bonus in block.Instructions)
            {
                if (bonus.IsTerminator) continue;
                // PseudoProbe and debug records are not represented. The five
                // represented call-site attributes carry no UB promise to drop.
                LlvmInstruction clone = bonus.Clone();
                predecessor.InsertBefore(clone, predecessorTerminator);
                if (clone.Source != predecessorTerminator.Source) clone.Source = null;
                LlvmValueMapper.RemapInstruction(clone, values,
                    LlvmRemapFlags.NoModuleLevelChanges | LlvmRemapFlags.IgnoreMissingLocals);
                clone.TakeName(bonus);
                byte[] oldName = new byte[clone.NameBytes.Length + 4];
                clone.NameBytes.Span.CopyTo(oldName);
                ".old"u8.CopyTo(oldName.AsSpan(clone.NameBytes.Length));
                bonus.SetName(oldName);
                values[bonus] = clone;
                foreach (LlvmUse use in bonus.Uses.ToArray())
                {
                    var user = (LlvmInstruction)use.User;
                    if (user is not LlvmPhiInstruction phi)
                    {
                        Debug.Assert(user.Parent == block && bonus.ComesBefore(user));
                        continue;
                    }
                    if (phi.GetIncomingBlock(use.OperandNo) == block) continue;
                    Debug.Assert(phi.GetIncomingBlock(use.OperandNo) == predecessor);
                    use.Set(clone);
                    Count("comparison.clone_external_phi_use");
                }
                Count("comparison.bonus_instruction_cloned");
            }
        }
    }
}
