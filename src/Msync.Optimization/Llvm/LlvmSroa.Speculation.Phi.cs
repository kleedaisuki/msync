// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp PHI load speculation and PHI store predication.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class State
    {
        internal bool IsSafePHIToSpeculate(LlvmPhiInstruction phi)
        {
            Count("speculation.phi.queries");
            LlvmBasicBlock block = phi.Parent!;
            LlvmAlign maxAlignment = new(1);
            LlvmType? loadType = null;
            foreach (LlvmUser user in phi.Users)
            {
                Count("speculation.phi.users-examined");
                if (user is not LlvmLoadInstruction { IsSimple: true } load)
                    return PhiRetained("non-simple-load-user");
                if (load.Parent != block) return PhiRetained("cross-block-load");
                if (loadType is not null && loadType != load.Type) return PhiRetained("different-load-types");
                loadType = load.Type;
                for (LlvmInstruction current = phi; current != load; current = current.Next!)
                {
                    Count("speculation.phi.prefix-instructions-examined");
                    if (current.MayWriteToMemory()) return PhiRetained("intervening-write");
                }
                maxAlignment = LlvmAlign.Max(maxAlignment, load.Alignment);
            }
            if (loadType is null) return PhiRetained("no-load-users");
            var loadSize = new LlvmAPInt(DL.GetIndexTypeSizeInBits(phi.Type), DL.GetTypeStoreSize(loadType).GetFixedValue());
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                Count("speculation.phi.predecessor-edges-examined");
                LlvmInstruction terminator = phi.GetIncomingBlock(index).GetTerminator();
                LlvmValue incoming = phi.GetIncomingValue(index);
                if (terminator == incoming || terminator.MayHaveSideEffects()) return PhiRetained("predecessor-terminator-effects");
                if (terminator.SuccessorCount == 1) continue;
                Count("speculation.phi.unconditional-load-queries");
                if (!LlvmLoads.IsSafeToLoadUnconditionally(incoming, maxAlignment, loadSize, terminator, metrics: Metrics))
                    return PhiRetained("critical-edge-load");
            }
            Count("speculation.phi.eligible");
            return true;
        }

        internal LlvmStoreInstruction? GetPHIStoreToRewrite(LlvmPhiInstruction phi, bool preserveCfg)
        {
            Count("predication.phi-store.queries");
            if (!phi.HasOneUse || phi.Users.First() is not LlvmStoreInstruction store || store.PointerOperand != phi || store.IsVolatile)
                return PhiStoreRetained("non-single-or-volatile-store");
            LlvmBasicBlock block = phi.Parent!;
            if (FirstNonPhiForSroa(block) != store) return PhiStoreRetained("store-not-first-nonphi");
            HashSet<LlvmBasicBlock> seen = [];
            LlvmDominatorTree tree = DTU.GetTree();
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                LlvmBasicBlock predecessor = phi.GetIncomingBlock(index);
                if (!seen.Add(predecessor)) continue;
                LlvmInstruction terminator = predecessor.GetTerminator();
                if (terminator is not (LlvmCondBrInstruction or LlvmUncondBrInstruction) || predecessor == block)
                    return PhiStoreRetained("terminator-or-self-predecessor");
                if (!tree.Dominates(store.ValueOperand, terminator)) return PhiStoreRetained("stored-value-does-not-dominate");
                if (terminator.SuccessorCount == 1) continue;
                if (preserveCfg || FirstNonPhiForSroa(block).IsEHPad) return PhiStoreRetained("predecessor-cannot-split");
            }
            Count("predication.phi-store.eligible");
            return store;
        }

        internal void SpeculatePHINodeLoads(LlvmTransformBuilder builder, LlvmPhiInstruction phi)
        {
            var someLoad = (LlvmLoadInstruction)phi.Users.First();
            LlvmType loadType = someLoad.Type;
            LlvmAlign alignment = someLoad.Alignment;
            builder.SetInsertPoint(phi);
            LlvmPhiInstruction replacement = builder.CreatePhi(loadType, phi.IncomingCount, phi.Name + ".sroa.speculated");
            replacement.LogicalType = someLoad.LogicalType;
            while (!phi.UseEmpty)
            {
                var load = (LlvmLoadInstruction)phi.Users.First();
                load.ReplaceAllUsesWith(replacement);
                load.EraseFromParent();
                Count("speculation.phi.loads-replaced");
            }
            Dictionary<LlvmBasicBlock, LlvmLoadInstruction> injected = [];
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                LlvmBasicBlock predecessor = phi.GetIncomingBlock(index);
                if (!injected.TryGetValue(predecessor, out LlvmLoadInstruction? load))
                {
                    builder.SetInsertPoint(predecessor.GetTerminator());
                    load = builder.CreateAlignedLoad(loadType, phi.GetIncomingValue(index), alignment,
                        phi.Name + ".sroa.speculate.load." + predecessor.Name);
                    load.LogicalType = replacement.LogicalType;
                    load.GetOperandUse(0).LogicalType = phi.GetOperandUse(index).LogicalType;
                    injected.Add(predecessor, load);
                    Count("loads.speculated");
                }
                else Count("speculation.phi.duplicate-edge-loads-reused");
                replacement.AddIncoming(load, predecessor);
                replacement.GetOperandUse(replacement.IncomingCount - 1).LogicalType = replacement.LogicalType;
            }
            phi.EraseFromParent();
        }

        internal bool RewritePHINodeStore(LlvmPhiInstruction phi, LlvmStoreInstruction store,
            LlvmDomTreeUpdater updater, OrderedSet<LlvmAllocaInstruction> worklist)
        {
            List<(LlvmBasicBlock Predecessor, LlvmValue Pointer, int LogicalType)> incoming = [];
            HashSet<LlvmBasicBlock> seen = [];
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                LlvmBasicBlock predecessor = phi.GetIncomingBlock(index);
                if (!seen.Add(predecessor)) continue;
                LlvmValue pointer = phi.GetIncomingValue(index);
                incoming.Add((predecessor, pointer, phi.GetOperandUse(index).LogicalType));
                if (LlvmValueTracking.GetUnderlyingObject(pointer, metrics: Metrics) is LlvmAllocaInstruction alloca) worklist.Add(alloca);
            }
            bool cfgChanged = false;
            LlvmBasicBlock block = phi.Parent!;
            foreach (var item in incoming)
            {
                LlvmBasicBlock storeBlock = item.Predecessor;
                if (storeBlock.GetTerminator().SuccessorCount != 1)
                {
                    storeBlock = LlvmBasicBlockUtils.SplitBlockPredecessors(block, [item.Predecessor], ".sroa.store", updater,
                        metrics: Metrics);
                    cfgChanged = true;
                    Count("predication.phi-store.edges-split");
                }
                var replacement = (LlvmStoreInstruction)store.Clone();
                replacement.PointerOperand = item.Pointer;
                replacement.GetOperandUse(1).LogicalType = item.LogicalType;
                storeBlock.InsertBefore(replacement, storeBlock.GetTerminator());
                Count("stores.predicated");
            }
            store.EraseFromParent();
            phi.EraseFromParent();
            return cfgChanged;
        }

        private LlvmStoreInstruction? PhiStoreRetained(string cause)
        {
            if (Metrics is not null) Count("predication.phi-store.retained." + cause);
            return null;
        }

        private bool PhiRetained(string cause)
        {
            if (Metrics is not null) Count("speculation.phi.retained." + cause);
            return false;
        }
    }

    private static LlvmInstruction FirstNonPhiForSroa(LlvmBasicBlock block)
    {
        LlvmInstruction instruction = block.FirstInstruction!;
        while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
        return instruction;
    }
}
