// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// canRewriteGEPAsOffset/rewriteGEPAsOffset/transformToIndexedCompare.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool ICmpCanRewriteGepAsOffset(LlvmValue start, LlvmValue @base, ref LlvmGepNoWrapFlags flags,
            List<LlvmValue> nodes)
        {
            HashSet<LlvmValue> explored = new(ReferenceEqualityComparer.Instance) { @base };
            nodes.Add(@base);
            List<LlvmValue> work = [start];
            while (work.Count != 0)
            {
                List<LlvmPhiInstruction> phis = [];
                while (work.Count != 0)
                {
                    if (explored.Count >= 100) { Count("icmp.indexed_pointer.node_budget"); return false; }
                    var value = work[^1];
                    if (explored.Contains(value)) { work.RemoveAt(work.Count - 1); continue; }
                    if (value is not (LlvmGetElementPtrInstruction or LlvmPhiInstruction)) return false;
                    if (value is LlvmGetElementPtrInstruction gep)
                    {
                        if (!gep.NoWrapFlags.IsInBounds || gep.Indices.Count(index => index is not LlvmConstantInt) > 1) return false;
                        flags = flags.IntersectForOffsetAdd(gep.NoWrapFlags);
                        if (!explored.Contains(gep.PointerOperand)) work.Add(gep.PointerOperand);
                    }
                    if (work[^1] == value)
                    {
                        work.RemoveAt(work.Count - 1);
                        if (explored.Add(value)) nodes.Add(value);
                    }
                    if (value is LlvmPhiInstruction phi)
                    {
                        if (phi.Parent!.GetTerminator().Opcode == LlvmOpcode.CatchSwitch) return false;
                        if (explored.Add(phi)) nodes.Add(phi);
                        if (!phis.Contains(phi)) phis.Add(phi);
                    }
                }
                foreach (var phi in phis)
                    for (int i = 0; i < phi.IncomingCount; ++i)
                        if (!explored.Contains(phi.GetIncomingValue(i))) work.Add(phi.GetIncomingValue(i));
            }
            foreach (var value in nodes)
            {
                if (value is not LlvmInstruction instruction || value == @base) continue;
                foreach (var user in value.Users)
                    if (user is LlvmPhiInstruction phi && instruction != phi && explored.Contains(phi) && phi.Parent == instruction.Parent) return false;
            }
            return true;
        }

        private static void ICmpSetInsertionPoint(LlvmTransformBuilder builder, LlvmValue value, bool before = true)
        {
            if (value is LlvmPhiInstruction phi) { builder.SetInsertPoint(ICmpFirstInsertion(phi.Parent!)); return; }
            if (value is LlvmInstruction instruction) { builder.SetInsertPoint(before ? instruction : instruction.Next!); return; }
            if (value is LlvmArgument argument) builder.SetInsertPoint(ICmpFirstInsertion(argument.Parent!.BasicBlocks[0]));
        }

        private static LlvmInstruction ICmpFirstInsertion(LlvmBasicBlock block)
        {
            var instruction = block.FirstInstruction!;
            while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
            if (instruction.IsEHPad) instruction = instruction.Next!;
            return instruction;
        }

        private LlvmValue ICmpRewriteGepAsOffset(LlvmValue start, LlvmValue @base, LlvmGepNoWrapFlags flags,
            List<LlvmValue> nodes)
        {
            var indexType = DL.GetIndexType(start.Context, ((LlvmPointerType)start.Type).AddressSpace);
            Dictionary<LlvmValue, LlvmValue> replacements = new(ReferenceEqualityComparer.Instance)
                { [@base] = LlvmConstantInt.Get(indexType, 0) };
            foreach (var value in nodes)
            {
                if (value == @base || value is not LlvmPhiInstruction phi) continue;
                var replacement = new LlvmPhiInstruction(indexType, phi.IncomingCount);
                replacement.SetName(phi.Name + ".idx");
                phi.Parent!.InsertBefore(replacement, phi);
                replacements.Add(phi, replacement);
            }
            var builder = new LlvmTransformBuilder(DL, Metrics);
            foreach (var value in nodes)
            {
                if (replacements.ContainsKey(value)) continue;
                if (value is not LlvmGetElementPtrInstruction gep) continue;
                ICmpSetInsertionPoint(builder, gep);
                var parent = replacements[gep.PointerOperand];
                var offset = LlvmGepUtils.EmitGEPOffset(builder, DL, LlvmGepOperator.Get(gep), metrics: Metrics);
                replacements.Add(gep, parent is LlvmConstantInt { IsZero: true } ? offset :
                    builder.CreateAdd(parent, offset, gep.PointerOperand.Name + ".add", flags.HasNoUnsignedWrap, flags.HasNoUnsignedSignedWrap));
            }
            foreach (var value in nodes)
            {
                if (value == @base || value is not LlvmPhiInstruction phi) continue;
                var replacement = (LlvmPhiInstruction)replacements[phi];
                for (int i = 0; i < phi.IncomingCount; ++i)
                {
                    var incoming = phi.GetIncomingValue(i);
                    replacement.AddIncoming(replacements.GetValueOrDefault(incoming, incoming), phi.GetIncomingBlock(i));
                }
            }
            foreach (var value in nodes)
            {
                if (value == @base) continue;
                ICmpSetInsertionPoint(builder, value, false);
                var pointer = builder.CreateGEP(LlvmType.GetInt8Ty(value.Context), @base, [replacements[value]], value.Name + ".ptr", flags);
                if (pointer != @base && pointer is LlvmGetElementPtrInstruction instruction) instruction.LogicalType = value.LogicalType;
                ReplaceInstUsesWith((LlvmInstruction)value, pointer);
                Worklist.Add((LlvmInstruction)value);
                Count("icmp.indexed_pointer.nodes_rewritten");
            }
            return replacements[start];
        }

        private LlvmInstruction? TransformToIndexedCompare(LlvmGepOperator left, LlvmValue right, LlvmCmpPredicate predicate)
        {
            if (!left.HasAllConstantIndices) return null;
            var offset = LlvmAPInt.GetZero(DL.GetIndexTypeSizeInBits(left.Type));
            var @base = LlvmValueTracking.StripAndAccumulateConstantOffsets(left.User, DL, ref offset, false, metrics: Metrics);
            if (@base.Type != left.Type) return null;
            List<LlvmValue> nodes = [];
            var flags = left.NoWrapFlags;
            if (!ICmpCanRewriteGepAsOffset(right, @base, ref flags, nodes)) return null;
            var newRight = ICmpRewriteGepAsOffset(right, @base, flags, nodes);
            return new LlvmICmpInstruction(ICmpSignedPredicate(predicate), LlvmConstantInt.Get(left.Context, offset), newRight);
        }
    }
}
