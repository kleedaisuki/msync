// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp deleteDeadInstruction.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private void Delete(LlvmInstruction instruction, HashSet<LlvmMemoryAccess>? deleted = null)
        {
            if (trace is null) { DeleteCore(instruction, deleted); return; }
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["block.name"] = instruction.Parent!.Name,
                ["instruction.opcode"] = instruction.Opcode.ToString()
            };
            if (instruction.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            trace.Run("optimizer.llvm-dse.delete", (State: this, Instruction: instruction, Deleted: deleted),
                static work => work.State.DeleteCore(work.Instruction, work.Deleted), attributes);
        }
        private void DeleteCore(LlvmInstruction instruction, HashSet<LlvmMemoryAccess>? deleted)
        {
            var work = new List<LlvmInstruction> { instruction };
            while (work.Count != 0)
            {
                var dead = work[^1];
                work.RemoveAt(work.Count - 1);
                var access = memory.GetMemoryAccess(dead);
                bool isDefinition = access is LlvmMemoryDef;
                if (access is not null)
                {
                    if (isDefinition)
                    {
                        skipped.Add(access);
                        deleted?.Add(access);
                        if (dead is LlvmStoreInstruction store && store.ValueOperand.Type.IsPointerTy())
                        {
                            var underlying = Underlying(store.ValueOperand);
                            if (capturedBeforeReturn.Remove(underlying)) iterateEndOfFunction = true;
                            invisibleAfterReturn.Remove(underlying);
                        }
                    }
                    updater.RemoveMemoryAccess(access);
                }
                RemoveIntervals(dead);
                InvalidateAddressFacts();
                foreach (var operand in dead.OperandUses)
                {
                    if (operand.Value is not LlvmInstruction producer) continue;
                    operand.Set(LlvmPoisonValue.Get(producer.Type));
                    if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(producer, Metrics)) work.Add(producer);
                }
                escapes.RemoveInstruction(dead);
                if (isDefinition && dead.Type.IsVoidTy()) dead.EraseFromParent();
                else toRemove.Add(dead);
                Count("instructions.deleted");
            }
        }
    }
}
