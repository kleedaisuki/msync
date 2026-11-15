// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp LoopPromoter.
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private sealed partial class Work
    {
        private sealed class LoopPromoter : LlvmLoadAndStorePromoter
        {
            private readonly Work owner;
            private readonly LlvmValue pointer;
            private readonly IReadOnlyList<LlvmBasicBlock> exits;
            private readonly IReadOnlyList<LlvmInstruction> insertions;
            private readonly List<LlvmMemoryAccess?> memoryInsertions;
            private readonly Dictionary<LlvmBasicBlock, LlvmBasicBlock[]> predecessorCache;
            private readonly LlvmAlign alignment;
            private readonly bool atomic, insertStores;
            private readonly int valueView, pointerView;
            private readonly SourceSpan? source;

            internal LoopPromoter(Work owner, LlvmValue pointer, IReadOnlyList<LlvmInstruction> instructions,
                LlvmSsaUpdater ssa, IReadOnlyList<LlvmBasicBlock> exits, IReadOnlyList<LlvmInstruction> insertions,
                List<LlvmMemoryAccess?> memoryInsertions, Dictionary<LlvmBasicBlock, LlvmBasicBlock[]> predecessorCache,
                LlvmAlign alignment, bool atomic, bool insertStores, int valueView, int pointerView, SourceSpan? source)
                : base(instructions, ssa, trace: owner.Trace, metrics: owner.Metrics)
            {
                this.owner = owner; this.pointer = pointer; this.exits = exits; this.insertions = insertions;
                this.memoryInsertions = memoryInsertions; this.predecessorCache = predecessorCache;
                this.alignment = alignment; this.atomic = atomic; this.insertStores = insertStores;
                this.valueView = valueView; this.pointerView = pointerView; this.source = source;
            }

            private LlvmValue MaybeInsertLcssaPhi(LlvmValue value, LlvmBasicBlock block, int view)
            {
                if (!owner.LoopInfo.WouldBeOutOfLoopUseRequiringLCSSA(value, block)) return value;
                var instruction = (LlvmInstruction)value;
                if (!predecessorCache.TryGetValue(block, out var predecessors))
                {
                    predecessors = LlvmDominatorTree.Predecessors(block).ToArray();
                    predecessorCache.Add(block, predecessors);
                }
                var phi = new LlvmPhiInstruction(instruction.Type, predecessors.Length)
                { Source = instruction.Source, LogicalType = view >= 0 ? view : instruction.LogicalType };
                phi.SetName(AppendName(instruction.NameBytes.Span, ".lcssa"u8));
                block.InsertBefore(phi, block.FirstInstruction);
                foreach (LlvmBasicBlock predecessor in predecessors)
                {
                    phi.AddIncoming(instruction, predecessor);
                    phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = view;
                }
                owner.Count("promotion.exit_lcssa_phis");
                return phi;
            }

            public override void DoExtraRewritesBeforeFinalDeletion()
            {
                if (!insertStores) return;
                for (int index = 0; index < exits.Count; ++index)
                {
                    LlvmBasicBlock exit = exits[index];
                    LlvmValue value = MaybeInsertLcssaPhi(SSA.GetValueInMiddleOfBlock(exit), exit, valueView);
                    LlvmValue address = MaybeInsertLcssaPhi(pointer, exit, pointerView);
                    var store = new LlvmStoreInstruction(value, address, alignment,
                        ordering: atomic ? LlvmAtomicOrdering.Unordered : LlvmAtomicOrdering.NotAtomic) { Source = source };
                    store.GetOperandUse(0).LogicalType = valueView;
                    store.GetOperandUse(1).LogicalType = pointerView;
                    exit.InsertBefore(store, insertions[index]);
                    LlvmMemoryAccess? point = memoryInsertions[index];
                    LlvmMemoryAccess access = point is null
                        ? owner.Updater.CreateMemoryAccessInBB(store, null, exit, LlvmMemorySsaInsertionPlace.Beginning)!
                        : owner.Updater.CreateMemoryAccessAfter(store, null, point);
                    memoryInsertions[index] = access;
                    owner.Updater.InsertDef((LlvmMemoryDef)access, true);
                    owner.Count("promotion.exit_stores");
                }
            }

            public override void InstructionDeleted(LlvmInstruction instruction)
            {
                owner.Safety.RemoveInstruction(instruction);
                owner.Updater.RemoveMemoryAccess(instruction);
            }
            public override bool ShouldDelete(LlvmInstruction instruction) => instruction is not LlvmStoreInstruction || insertStores;
        }
    }
}
