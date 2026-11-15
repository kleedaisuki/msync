using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private readonly Dictionary<LlvmPhiInstruction, int[]> aggregateIncoming = [];
        private readonly record struct AggregatePhiCopy(int Target, LlvmUse Source, int Leaf, SourceSpan? Origin);
        private readonly Dictionary<LlvmBasicBlock, List<AggregatePhiCopy>> aggregatePhiSources = [];

        private bool TryPrepareAggregatePhi(LlvmPhiInstruction phi)
        {
            if (!IsLeafAggregate(phi.Type)) return false;
            var shape = AggregateShapeFor(phi.Type);
            int[] targets = new int[shape.Leaves.Length], inputs = new int[shape.Leaves.Length];
            for (int index = 0; index < targets.Length; ++index)
            {
                int type = shape.Leaves[index].NativeType;
                targets[index] = Register(type);
                inputs[index] = Register(type);
                if (shape.Leaves[index].FunctionComponent == 2) CopyFunctionOrigin(targets[index], inputs[index]);
            }
            aggregateSlots.Add(phi, new(shape, targets));
            aggregateIncoming.Add(phi, inputs);
            HashSet<LlvmBasicBlock> inserted = [];
            for (int incomingIndex = phi.IncomingCount - 1; incomingIndex >= 0; --incomingIndex)
            {
                var predecessor = phi.GetIncomingBlock(incomingIndex);
                if (!inserted.Add(predecessor)) continue;
                if (!aggregatePhiSources.TryGetValue(predecessor, out var copies))
                    aggregatePhiSources.Add(predecessor, copies = []);
                for (int leaf = 0; leaf < inputs.Length; ++leaf)
                    copies.Add(new(inputs[leaf], phi.GetOperandUse(incomingIndex), leaf, phi.Source));
            }
            return true;
        }

        private bool TryEmitAggregatePhi(LlvmPhiInstruction phi)
        {
            if (!aggregateIncoming.TryGetValue(phi, out var inputs)) return false;
            var result = aggregateSlots[phi];
            for (int index = 0; index < inputs.Length; ++index)
            {
                PhiCopy(result.Slots[index], inputs[index], phi.Source);
                ++aggregateLeafCopies;
            }
            return true;
        }

        private void EmitAggregatePhiSources(LlvmBasicBlock block)
        {
            if (!aggregatePhiSources.TryGetValue(block, out var copies)) return;
            foreach (var copy in copies)
            {
                var source = AggregateLeaves(copy.Source.Value!);
                PhiCopy(copy.Target, source.Slots[copy.Leaf], copy.Origin);
                if (source.Shape.Leaves[copy.Leaf].FunctionComponent == 2)
                    CopyFunctionOrigin(copy.Target, source.Slots[copy.Leaf]);
                ++aggregateLeafCopies;
            }
        }
    }
}
