// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// simplifyBranchOnICmpChain, including source ExtraCase freeze and range branch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool SimplifyBranchOnICmpChain(LlvmCondBrInstruction branch) =>
            SimplifyBranchOnICmpChain(branch, new LlvmTransformBuilder(branch, Metrics));

        private bool SimplifyBranchOnICmpChain(LlvmCondBrInstruction branch, LlvmTransformBuilder builder)
        {
            if (branch.Condition is not LlvmInstruction condition) return false;
            Count("conditional.icmp_chain.queries");
            var gathered = new ConstantComparesGatherer(condition, Layout, Metrics);
            var values = gathered.Vals;
            var compared = gathered.CompValue;
            var extra = gathered.Extra;
            if (compared is null || gathered.UsedICmps <= 1) return false;
            values.Sort(static (left, right) => right.Value.Compare(left.Value));
            int write = 0;
            for (int read = 0; read < values.Count; ++read)
            {
                var value = values[read];
                if (write == 0 || value != values[write - 1]) values[write++] = value;
            }
            if (write != values.Count) values.RemoveRange(write, values.Count - write);
            if (extra is not null && values.Count < 2) return false;
            var defaultBlock = gathered.IsEq ? branch.GetSuccessor(1) : branch.GetSuccessor(0);
            var edgeBlock = gathered.IsEq ? branch.GetSuccessor(0) : branch.GetSuccessor(1);
            var block = branch.Parent!;
            List<LlvmCfgUpdate> updates = [];
            if (extra is not null)
            {
                var split = LlvmBasicBlockUtils.SplitBlock(block, branch, DominatorUpdater, name: "switch.early.test", metrics: Metrics);
                var oldTerm = block.GetTerminator();
                builder.SetInsertPoint(oldTerm);
                if (!LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(extra, contextInstruction: branch, metrics: Metrics))
                { extra = builder.CreateFreeze(extra); Count("conditional.icmp_chain.extra_frozen"); }
                builder.CreateCondBr(extra, gathered.IsEq ? edgeBlock : split, gathered.IsEq ? split : edgeBlock);
                oldTerm.EraseFromParent();
                updates.Add(new(LlvmCfgUpdateKind.Insert, block, edgeBlock));
                AddPredecessorToBlock(edgeBlock, block, split);
                block = split;
            }
            builder.SetInsertPoint(branch);
            if (compared.Type is LlvmPointerType pointer)
                compared = builder.CreatePtrToInt(compared, Layout.GetIntPtrType(compared.Context, pointer.AddressSpace), "magicptr");
            var span = values[0].Value - values[^1].Value;
            if (span == new LlvmAPInt(span.BitWidth, (ulong)values.Count - 1))
            {
                var region = LlvmConstantRange.GetNonEmpty(values[^1].Value, values[0].Value + new LlvmAPInt(span.BitWidth, 1));
                region.GetEquivalentICmp(out var predicate, out var rhs, out var offset);
                var value = compared;
                if (!offset.IsZero) value = builder.CreateAdd(value, LlvmConstantInt.Get(value.Context, offset));
                var test = builder.CreateICmp(predicate, value, LlvmConstantInt.Get(value.Context, rhs));
                builder.CreateCondBr(test, edgeBlock, defaultBlock);
                Count("conditional.icmp_chain.range_branch");
            }
            else
            {
                var instruction = builder.CreateSwitch(compared, defaultBlock, values.Count);
                foreach (var value in values) instruction.AddCase(value, edgeBlock);
                for (var first = edgeBlock.FirstInstruction; first is LlvmPhiInstruction phi; first = first.Next)
                {
                    int index = phi.GetBasicBlockIndex(block);
                    var incoming = phi.GetIncomingValue(index);
                    int logicalType = phi.GetOperandUse(index).LogicalType;
                    for (int i = 0; i < values.Count - 1; ++i)
                    {
                        phi.AddIncoming(incoming, block);
                        phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = logicalType;
                    }
                }
                Count("conditional.icmp_chain.switch");
            }
            EraseTerminatorAndDceCondition(branch);
            DominatorUpdater?.ApplyUpdates(updates);
            Count("conditional.icmp_chain.rewritten");
            return true;
        }
    }
}
