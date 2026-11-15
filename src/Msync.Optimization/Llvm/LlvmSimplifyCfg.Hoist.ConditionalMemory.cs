// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/SimplifyCFG.cpp isSafeCheapLoadStore; conditional faulting boundary.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool IsSafeCheapLoadStore(LlvmInstruction instruction)
        {
            bool store;
            LlvmType type;
            LlvmAlign alignment;
            if (instruction is LlvmLoadInstruction load)
            {
                if (!load.IsSimple || !Tuning.HoistLoadsWithCondFaulting) return false;
                (store, type, alignment) = (false, load.Type, load.Alignment);
            }
            else if (instruction is LlvmStoreInstruction write)
            {
                if (!write.IsSimple || !Tuning.HoistStoresWithCondFaulting) return false;
                (store, type, alignment) = (true, write.ValueOperand.Type, write.Alignment);
            }
            else return false;
            Count("hoist.conditional-memory.target-queries");
            return TargetInfo.HasConditionalLoadStoreForType(type, store) && alignment.Value < (1UL << 32);
        }

        private void HoistConditionalLoadsStores(LlvmCondBrInstruction branch,
            IReadOnlyList<LlvmInstruction> instructions, bool? invert = null, LlvmInstruction? select = null)
        {
            // TargetTransformInfoImplBase reports false for conditional
            // faulting. Reaching this emitter requires a target implementing
            // masked vector memory intrinsics, which this target does not have.
            Count("hoist.conditional-memory.unsupported-emission");
            throw new NotSupportedException($"Conditional-faulting lowering in {branch.Parent!.Parent!.Name}/{branch.Parent.Name} ({instructions.Count} memory instructions) requires masked vector memory intrinsics; the generic target does not support conditional faulting.");
        }
    }
}
