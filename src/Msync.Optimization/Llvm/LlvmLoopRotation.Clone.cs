// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LoopRotationUtils.cpp header cloning/hoisting and RewriteUsesOfClonedInstructions.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopRotation
{
    private sealed partial class Work
    {
        private void CloneHeader(LlvmBasicBlock originalHeader, LlvmBasicBlock originalPreheader,
            Dictionary<LlvmValue, LlvmValue> values, Dictionary<LlvmValue, LlvmValue> memoryValues)
        {
            LlvmInstruction? current = originalHeader.FirstInstruction;
            while (current is LlvmPhiInstruction phi)
            {
                InsertMap(values, phi, phi.GetIncomingValue(phi.GetBasicBlockIndex(originalPreheader)));
                current = current.Next;
                Count("values.header_phis_seeded");
            }
            LlvmInstruction entryBranch = originalPreheader.GetTerminator();
            while (current is not null)
            {
                LlvmInstruction instruction = current;
                current = current.Next;
                Count("instructions.header_visited");
                if (loop.HasLoopInvariantOperands(instruction) && !instruction.MayReadFromMemory() &&
                    !instruction.MayWriteToMemory() && !instruction.IsTerminator && instruction is not LlvmAllocaInstruction &&
                    !instruction.Parent!.Parent!.IsPresplitCoroutine)
                {
                    // Source moveBefore, expressed through the existing typed
                    // intrusive-list ownership operations; value identity stays.
                    instruction.RemoveFromParent();
                    originalPreheader.InsertBefore(instruction, entryBranch);
                    Count("instructions.hoisted");
                    continue;
                }
                LlvmInstruction clone = instruction.Clone();
                originalPreheader.InsertBefore(clone, entryBranch);
                Count("instructions.cloned");
                LlvmValueMapper.RemapInstruction(clone, values,
                    LlvmRemapFlags.NoModuleLevelChanges | LlvmRemapFlags.IgnoreMissingLocals);
                LlvmValue? simplified = LlvmInstructionSimplify.SimplifyInstruction(clone, tree, Metrics, assumptions: assumptions);
                if (simplified is not null && loops.ReplacementPreservesLCSSAForm(clone, simplified))
                {
                    InsertMap(values, instruction, simplified);
                    Count("instructions.clone_simplified");
                    if (!clone.MayHaveSideEffects())
                    {
                        clone.EraseFromParent();
                        Count("instructions.simplified_clone_erased");
                        continue;
                    }
                }
                else InsertMap(values, instruction, clone);
                clone.SetName(instruction.Name);
                if (clone is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true } assume && assumptions is not null)
                {
                    assumptions.RegisterAssumption(assume);
                    Count("assumptions.cloned_registered");
                }
                if (memoryUpdater is not null) InsertMap(memoryValues, instruction, clone);
            }
        }
        private void RewriteClonedUses(LlvmBasicBlock originalHeader, LlvmBasicBlock originalPreheader,
            Dictionary<LlvmValue, LlvmValue> values, List<LlvmPhiInstruction> inserted)
        {
            for (LlvmInstruction? current = originalHeader.FirstInstruction; current is LlvmPhiInstruction phi; current = current.Next)
            {
                phi.RemoveIncomingValue(phi.GetBasicBlockIndex(originalPreheader));
                Count("phis.original_preheader_incoming_removed");
            }
            LlvmSsaUpdater updater = new(inserted, trace: trace, metrics: Metrics);
            for (LlvmInstruction? instruction = originalHeader.FirstInstruction; instruction is not null; instruction = instruction.Next)
            {
                if (instruction.UseEmpty) continue;
                LlvmValue preheaderValue = values[instruction];
                updater.Initialize(instruction.Type, instruction.Name, instruction.LogicalType);
                updater.AddAvailableValue(originalHeader, instruction);
                updater.AddAvailableValue(originalPreheader, preheaderValue);
                for (LlvmUse? use = instruction.FirstUse; use is not null;)
                {
                    LlvmUse? next = use.Next;
                    LlvmInstruction user = (LlvmInstruction)use.User;
                    if (user is not LlvmPhiInstruction)
                    {
                        if (ReferenceEquals(user.Parent, originalHeader))
                        { Count("uses.original_header_retained"); use = next; continue; }
                        if (ReferenceEquals(user.Parent, originalPreheader))
                        {
                            if (use.LogicalType < 0) use.LogicalType = instruction.LogicalType;
                            use.Set(preheaderValue);
                            Count("uses.preheader_remapped"); use = next; continue;
                        }
                    }
                    if (use.LogicalType < 0) use.LogicalType = instruction.LogicalType;
                    updater.RewriteUse(use);
                    Count("uses.ssa_rewritten");
                    use = next;
                }
            }
        }
    }
}
