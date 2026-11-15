// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// isSafeToSpeculateStore, including its writable/capture/dereferenceability queries.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private LlvmValue? IsSafeToSpeculateStore(LlvmInstruction instruction, LlvmBasicBlock branchBlock,
            LlvmBasicBlock storeBlock, LlvmBasicBlock endBlock, ref LlvmInstruction? previousAccess)
        {
            Count("store_speculation.queries");
            if (instruction is not LlvmStoreInstruction { IsSimple: true } store) return null;
            var pointer = store.PointerOperand;
            var type = store.ValueOperand.Type;
            uint remaining = 9;
            for (var current = branchBlock.LastInstruction; current is not null && remaining != 0; current = current.Previous)
            {
                --remaining; Count("store_speculation.access_scan");
                // PseudoProbe intrinsic instructions are not represented.
                if (current.MayWriteToMemory() && current is not LlvmStoreInstruction)
                { Count("store_speculation.intervening_writer"); return null; }
                if (current is LlvmStoreInstruction priorStore)
                {
                    if (priorStore.PointerOperand == pointer && priorStore.ValueOperand.Type == type && priorStore.IsSimple &&
                        priorStore.Alignment >= store.Alignment)
                    {
                        previousAccess = priorStore; Count("store_speculation.matching_store");
                        return priorStore.ValueOperand;
                    }
                    Count("store_speculation.unknown_store"); return null;
                }
                if (current is not LlvmLoadInstruction load || load.PointerOperand != pointer || load.Type != type ||
                    !load.IsSimple || load.Alignment < store.Alignment) continue;
                var value = LlvmValueTracking.GetUnderlyingObject(pointer, metrics: Metrics);
                if (!LlvmLicmObjectFacts.IsWritableObject(value, out bool explicitOnly, Metrics)) continue;
                var capture = LlvmCaptureTracking.PointerMayBeCaptured(value, LlvmCaptureComponents.Provenance, metrics: Metrics);
                if (!capture.WithoutRet.CapturesNothing()) continue;
                if (explicitOnly && !LlvmValueTracking.IsDereferenceableAndAlignedPointer(pointer, type, new(1), Layout,
                    ignoreFree: true, metrics: Metrics)) continue;
                previousAccess = load; Count("store_speculation.matching_load"); return load;
            }
            Count("store_speculation.no_previous_access");
            return null;
        }
    }
}
