// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp object visibility and memory predicates.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private LlvmMemoryLocation? GetWriteLocation(LlvmInstruction instruction)
        {
            if (!instruction.MayWriteToMemory()) return null;
            // TLI disables all C-library routines, and no memory-write/lifetime
            // intrinsic identities or initializes attributes are represented.
            if (instruction is LlvmCallBase) return null;
            return LlvmMemoryLocation.GetOrNone(instruction, layout);
        }
        private static bool IsRemovable(LlvmInstruction instruction) => instruction is LlvmStoreInstruction { IsUnordered: true };
        private bool InvisibleOnUnwind(LlvmValue value)
        {
            if (!LlvmLicmObjectFacts.IsNotVisibleOnUnwind(value, out bool needsCapture, Metrics)) return false;
            if (!needsCapture) return true;
            if (!capturedBeforeReturn.TryGetValue(value, out bool captured))
            {
                captured = LlvmCaptureTracking.PointerMayBeCaptured(value, LlvmCaptureComponents.Provenance, metrics: Metrics).WithoutRet.CapturesAnything();
                capturedBeforeReturn.Add(value, captured);
            }
            return !captured;
        }
        private bool InvisibleAfterReturn(LlvmValue value, LlvmMemoryLocation location)
        {
            if (value is LlvmAllocaInstruction) return true;
            // No byval/dead_on_return bounded argument attributes are represented.
            if (!invisibleAfterReturn.TryGetValue(value, out bool invisible))
            {
                invisible = InvisibleOnUnwind(value) && value is LlvmHeapAllocInst
                    && LlvmCaptureTracking.PointerMayBeCaptured(value, LlvmCaptureComponents.Provenance, metrics: Metrics).WithRet.CapturesNothing();
                invisibleAfterReturn.Add(value, invisible);
            }
            return invisible;
        }
        private bool IsReadClobber(LlvmMemoryLocation location, LlvmInstruction instruction)
        {
            if (instruction is LlvmStoreInstruction store) return StrongerThanMonotonic(store.Ordering);
            if (!instruction.MayReadFromMemory()) return false;
            if (instruction is LlvmCallBase && alias.GetMemoryEffects(instruction).OnlyAccessesInaccessibleMem) return false;
            Count("read_clobber.queries");
            return alias.GetModRefInfo(instruction, location).IsRefSet();
        }
        private bool IsLoopIndependent(LlvmInstruction current, LlvmInstruction killing, LlvmMemoryLocation location)
        {
            if (ReferenceEquals(current.Parent, killing.Parent)) return true;
            var cycle = cycles.GetCycle(current.Parent!);
            if (cycle is not null && ReferenceEquals(cycle, cycles.GetCycle(killing.Parent!))) return true;
            return IsLoopInvariant(location.Ptr!);
        }
        private bool IsLoopInvariant(LlvmValue pointer)
        {
            pointer = StripCasts(pointer);
            if (pointer is LlvmGetElementPtrInstruction gep && gep.Indices.All(index => index is LlvmConstantInt))
                pointer = StripCasts(gep.PointerOperand);
            return pointer is not LlvmInstruction instruction || ReferenceEquals(instruction.Parent, function.BasicBlocks[0])
                || cycles.GetCycle(instruction.Parent!) is null;
        }
        private bool MayThrowBetween(LlvmInstruction killing, LlvmInstruction dead, LlvmValue underlying)
        {
            if (InvisibleOnUnwind(underlying)) return false;
            return ReferenceEquals(killing.Parent, dead.Parent) ? throwingBlocks.Contains(killing.Parent!) : throwingBlocks.Count != 0;
        }
        private bool IsBarrier(LlvmValue underlying, LlvmInstruction instruction)
        {
            if (instruction.MayThrow() && !InvisibleOnUnwind(underlying)) return true;
            return instruction switch
            {
                LlvmLoadInstruction load => StrongerThanMonotonic(load.Ordering),
                LlvmStoreInstruction store => StrongerThanMonotonic(store.Ordering),
                _ => false
            };
        }
        private bool CanSkipDef(LlvmMemoryDef definition, bool visibleToCaller)
        {
            var instruction = definition.MemoryInstruction!;
            if (instruction is LlvmCallBase && alias.GetMemoryEffects(instruction).OnlyAccessesInaccessibleMem) return true;
            if (instruction.MayThrow() && !visibleToCaller) return true;
            return instruction.Opcode == LlvmOpcode.Fence;
        }
        private bool CompleteOverwrite(LlvmMemoryLocation deadLocation, LlvmInstruction dead, LlvmInstruction use)
        {
            if (!use.MayWriteToMemory()) return false;
            if (use is LlvmCallBase && alias.GetMemoryEffects(use).OnlyAccessesInaccessibleMem) return false;
            return GetWriteLocation(use) is { } location && IsOverwrite(use, dead, location, deadLocation, out _, out _) == Overwrite.Complete;
        }
        private static bool StrongerThanMonotonic(LlvmAtomicOrdering ordering) => ordering is not
            (LlvmAtomicOrdering.NotAtomic or LlvmAtomicOrdering.Unordered or LlvmAtomicOrdering.Monotonic);
        private static void PushMemoryUses(LlvmMemoryAccess access, List<LlvmMemoryAccess> work, HashSet<LlvmMemoryAccess> visited)
        {
            foreach (var use in access.Uses)
                if (visited.Add((LlvmMemoryAccess)use.User)) work.Add((LlvmMemoryAccess)use.User);
        }
    }
}
