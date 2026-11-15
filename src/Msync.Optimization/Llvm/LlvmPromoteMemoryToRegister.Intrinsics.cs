// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/PromoteMemoryToRegister.cpp removeIntrinsicUsers.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmPromoteMemoryToRegister
{
    private sealed partial class Promotion
    {
        private void RemoveIntrinsicUsers()
        {
            foreach (var alloca in allocas)
            {
                Debug.Assert(IsAllocaPromotable(alloca));
                LlvmType? promotedType = null;
                foreach (var user in alloca.Users)
                {
                    if (user is LlvmLoadInstruction load) { promotedType = load.Type; break; }
                    if (user is LlvmStoreInstruction store) { promotedType = store.ValueOperand.Type; break; }
                }
                // Snapshot Uses, not Users: two droppable operands must be dropped independently.
                foreach (var use in alloca.Uses.ToArray())
                {
                    // Erasing a variadic fake.use also unlinks its other alloca operands.
                    if (use.Value is null) continue;
                    var instruction = (LlvmInstruction)use.User;
                    if (instruction is LlvmLoadInstruction or LlvmStoreInstruction) continue;
                    if (instruction.IsDroppable())
                    {
                        LlvmValue.DropDroppableUse(use);
                        Count(metrics, "intrinsic_users.dropped_uses");
                        continue;
                    }
                    if (promotedType is not null && instruction.IsLifetimeStartOrEnd)
                    {
                        var reset = new LlvmStoreInstruction(LlvmUndefValue.Get(promotedType), alloca, alloca.Alignment)
                        { Source = instruction.Source };
                        instruction.Parent!.InsertBefore(reset, instruction);
                        Count(metrics, "intrinsic_users.lifetime_resets");
                    }
                    instruction.EraseFromParent();
                    Count(metrics, "intrinsic_users.removed");
                }
            }
        }
    }
}
