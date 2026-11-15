// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/SROA.cpp 6313–6401.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private sealed class BasicLoadAndStorePromoter(IReadOnlyList<LlvmInstruction> instructions,
        LlvmSsaUpdater updater, LlvmType zeroType, CompilationTrace? trace,
        Dictionary<string, ulong>? metrics) : LlvmLoadAndStorePromoter(instructions, updater, trace: trace, metrics: metrics)
    {
        public override bool ShouldDelete(LlvmInstruction instruction) => instruction is not (LlvmStoreInstruction or LlvmAllocaInstruction);
        public override LlvmValue GetValueToUseForAlloca(LlvmInstruction alloca) => LlvmUndefValue.Get(zeroType);
    }

    internal sealed partial class State
    {
        private bool PropagateStoredValuesToLoads(LlvmAllocaInstruction alloca, AllocaSlices slices)
        {
            bool allSameAndValid = true;
            LlvmType? partitionType = null;
            List<LlvmInstruction> instructions = [];
            ulong beginOffset = 0, endOffset = 0;
            foreach (var slice in slices.Slices)
            {
                var user = (LlvmInstruction)slice.Use!.User;
                if (user is LlvmCallBase { CalledFunction.IntrinsicDescriptor.IsAssumeLikeIntrinsic: true })
                { Count("sroa.readonly.assume_like_ignored"); continue; }
                if (slice.BeginOffset >= endOffset)
                {
                    Flush();
                    beginOffset = slice.BeginOffset;
                    endOffset = slice.EndOffset;
                }
                else if (slice.BeginOffset != beginOffset || slice.EndOffset != endOffset)
                {
                    allSameAndValid = false;
                    endOffset = Math.Max(endOffset, slice.EndOffset);
                    Count("sroa.readonly.overlapping_ranges");
                    continue;
                }
                if (user is LlvmLoadInstruction load)
                {
                    if (!load.IsSimple || partitionType is not null && partitionType != load.Type) allSameAndValid = false;
                    partitionType = load.Type;
                    instructions.Add(user);
                }
                else if (user is LlvmStoreInstruction store)
                {
                    var type = store.ValueOperand.Type;
                    if (!store.IsSimple || partitionType is not null && partitionType != type) allSameAndValid = false;
                    partitionType = type;
                    instructions.Add(user);
                }
                else allSameAndValid = false;
            }
            Flush();
            return true;

            void Flush()
            {
                if (allSameAndValid && instructions.Count != 0)
                {
                    List<LlvmPhiInstruction> phis = [];
                    var updater = new LlvmSsaUpdater(phis, Trace, Metrics);
                    instructions.Add(alloca);
                    new BasicLoadAndStorePromoter(instructions, updater, partitionType!, Trace, Metrics).Run(instructions);
                    Count("sroa.readonly.partitions.propagated");
                    Count("sroa.readonly.phis.inserted", (ulong)phis.Count);
                }
                else if (instructions.Count != 0) Count("sroa.readonly.partitions.unpropagated");
                allSameAndValid = true;
                partitionType = null;
                instructions.Clear();
            }
        }
    }
}
