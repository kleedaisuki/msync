// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp
// isHoistableAndSinkableInst, canHoistLoad, canSinkOrHoistInst,
// noConflictingReadWrites, pointerInvalidatedByLoop/Block.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private sealed partial class Work
    {
        private bool CanSinkOrHoist(LlvmInstruction instruction, LlvmLoop loop, bool targetExecutesOnce,
            SinkAndHoistFlags flags)
        {
            Count("legality.queries");
            if (instruction is not (LlvmLoadInstruction or LlvmStoreInstruction or LlvmCallInstruction or
                LlvmCastInstruction or LlvmUnaryOperator or LlvmBinaryOperator or LlvmSelectInstruction or
                LlvmGetElementPtrInstruction or LlvmCmpInstruction or LlvmExtractValueInstruction or
                LlvmInsertValueInstruction or LlvmFreezeInstruction)) return Retain("unsupported_instruction_family");
            if (instruction is LlvmLoadInstruction load) return CanHoistLoad(load, loop, targetExecutesOnce, flags);
            if (instruction is LlvmCallInstruction call)
            {
                if (call.MayThrow()) return Retain("call_may_throw");
                // No convergent/presplit-coroutine attributes are represented.
                // Registered assume/invariant intrinsics are not inferred from names.
                var behavior = Alias.GetMemoryEffects(call);
                if (behavior.DoesNotAccessMemory) return true;
                if (behavior.OnlyReadsMemory)
                    return Memory.GetMemoryAccess(call) is LlvmMemoryUse use && !PointerInvalidatedByLoop(use, loop, instruction, flags, false);
                if (behavior.OnlyWritesMemory) return NoConflictingReadWrites(call, loop, flags);
                return Retain("call_modref");
            }
            if (instruction is LlvmStoreInstruction store)
            {
                if (!store.IsUnordered) return Retain("ordered_store");
                return IsOnlyMemoryAccess(store, loop) || NoConflictingReadWrites(store, loop, flags);
            }
            Debug.Assert(!instruction.MayReadFromMemory() && !instruction.MayWriteToMemory());
            return true;
        }

        private bool CanHoistLoad(LlvmLoadInstruction load, LlvmLoop loop, bool targetExecutesOnce,
            SinkAndHoistFlags flags)
        {
            if (!load.IsUnordered) return Retain("ordered_load");
            if (!Alias.GetModRefInfoMask(LlvmMemoryLocation.GetBeforeOrAfter(load.PointerOperand)).IsModSet()) return true;
            if (load.IsAtomic && !targetExecutesOnce) return Retain("atomic_load_duplication");
            // There is no invariant_load/invariant_group metadata or invariant.start
            // intrinsic identity in the represented IR.
            var use = (LlvmMemoryUse)Memory.GetMemoryAccess(load)!;
            return !PointerInvalidatedByLoop(use, loop, load, flags, false);
        }

        private bool IsOnlyMemoryAccess(LlvmInstruction instruction, LlvmLoop loop)
        {
            foreach (LlvmBasicBlock block in loop.Blocks)
            {
                if (Memory.GetBlockAccesses(block) is not { } accesses) continue;
                int nonPhi = 0;
                foreach (LlvmMemoryAccess access in accesses)
                {
                    if (access is LlvmMemoryPhi) continue;
                    if (((LlvmMemoryUseOrDef)access).MemoryInstruction != instruction || nonPhi++ == 1) return false;
                }
            }
            return true;
        }

        private LlvmMemoryAccess ClobberingAccess(LlvmBatchAAResults batch, SinkAndHoistFlags flags,
            LlvmMemoryUseOrDef access)
        {
            if (flags.TooManyClobberingCalls) { Count("clobber.cap_reached"); return access.DefiningAccess!; }
            Count("clobber.walker_queries");
            var source = Memory.GetSkipSelfWalker().GetClobberingMemoryAccess(access, batch, Metrics);
            flags.IncrementClobberingCalls();
            return source;
        }

        private bool NoConflictingReadWrites(LlvmInstruction instruction, LlvmLoop loop, SinkAndHoistFlags flags)
        {
            if (flags.TooManyMemoryAccesses) return Retain("memory_access_cap");
            var access = Memory.GetMemoryAccess(instruction)!;
            var batch = new LlvmBatchAAResults(Alias);
            var source = ClobberingAccess(batch, flags, access);
            if (!Memory.IsLiveOnEntryDef(source) && loop.Contains(source.Block)) return Retain("loop_clobber");
            foreach (LlvmBasicBlock block in loop.Blocks)
            {
                if (Memory.GetBlockAccesses(block) is not { } accesses) continue;
                foreach (LlvmMemoryAccess candidate in accesses)
                {
                    Count("conflicts.memory_accesses");
                    if (!flags.IsSink && Memory.Dominates(access, candidate)) break;
                    if (candidate is not LlvmMemoryUseOrDef useOrDef) continue;
                    if (batch.GetModRefInfo(useOrDef.MemoryInstruction!, instruction).IsNoModRef()) continue;
                    return Retain("conflicting_read_write");
                }
            }
            return true;
        }

        private bool PointerInvalidatedByLoop(LlvmMemoryUse use, LlvmLoop loop, LlvmInstruction instruction,
            SinkAndHoistFlags flags, bool invariantGroup)
        {
            if (!flags.IsSink)
            {
                var batch = new LlvmBatchAAResults(Memory.AA);
                var source = ClobberingAccess(batch, flags, use);
                return !Memory.IsLiveOnEntryDef(source) && loop.Contains(source.Block) &&
                    !(invariantGroup && source.Block == loop.Header && source is LlvmMemoryPhi);
            }
            if (flags.TooManyMemoryAccesses) return true;
            foreach (LlvmBasicBlock block in loop.Blocks)
                if (PointerInvalidatedByBlock(block, use)) return true;
            return !loop.Contains(instruction.Parent!) && PointerInvalidatedByBlock(instruction.Parent!, use);
        }

        private bool PointerInvalidatedByBlock(LlvmBasicBlock block, LlvmMemoryUse use)
        {
            if (Memory.GetBlockDefs(block) is not { } definitions) return false;
            foreach (LlvmMemoryAccess access in definitions)
                if (access is LlvmMemoryDef definition &&
                    (use.Block != definition.Block || !Memory.LocallyDominates(definition, use))) return true;
            return false;
        }
    }
}
