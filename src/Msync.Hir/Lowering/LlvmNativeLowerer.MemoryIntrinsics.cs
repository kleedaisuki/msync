// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// CodeGen/SelectionDAG/SelectionDAGBuilder.cpp launder/invariant/lifetime lowering.
using Msync.Core;
using Msync.Core.Llvm;
namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private bool MemoryMarkerIntrinsic(LlvmCallInstruction call, LlvmIntrinsicDescriptor descriptor)
        {
            if (descriptor.IsLaunderInvariantGroup)
            {
                // The optimizer barrier ends here; source codegen forwards the actual pointer.
                values[call] = ArgumentSlot(call, 0);
                trace?.Metric("intrinsic.launder.pointer_forwarded", 1);
                return true;
            }
            if (descriptor.Kind == LlvmIntrinsicKind.InvariantStart)
            {
                // SelectionDAGBuilder chooses undef for the descriptor, not the region address.
                int tokenType = call.LogicalType >= 0 ? call.LogicalType : owner.Pointer(owner.Primitive(TypeKind.Null));
                values[call] = ConstantSlot(LlvmUndefValue.Get(call.Type), tokenType);
                trace?.Metric("intrinsic.invariant_start.undef_descriptor", 1);
                return true;
            }
            if (!descriptor.IsLifetimeStartOrEnd && !descriptor.IsInvariantStartOrEnd && !descriptor.IsFakeUse)
                return false;
            trace?.Metric("intrinsic." + descriptor.Kind.Name + ".marker_elided", 1);
            return true;
        }
    }
}
