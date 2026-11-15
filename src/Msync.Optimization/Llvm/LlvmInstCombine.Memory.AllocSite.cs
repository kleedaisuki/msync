// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp isNeverEqualToUnescapedAlloc/isAllocSiteRemovable/
// visitAllocSite. TLI library allocation families and allocation-kind attrs are
// not represented; the source alloca path is active, not a HeapAlloc adapter.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool IsNeverEqualToUnescapedAlloc(LlvmValue value) =>
            value is LlvmConstantPointerNull || value is LlvmLoadInstruction { PointerOperand: LlvmGlobalVariable };

        private LlvmModRefInfo? IsAllocSiteRemovable(LlvmInstruction allocation, List<LlvmInstruction> users, bool knowInitialValue)
        {
            var pending = new Stack<LlvmInstruction>();
            pending.Push(allocation);
            LlvmModRefInfo access = knowInitialValue ? LlvmModRefInfo.NoModRef : LlvmModRefInfo.Mod;
            do
            {
                var pointer = pending.Pop();
                foreach (var user in pointer.Users)
                {
                    Count("instcombine.memory.allocsite.users");
                    if ((uint)users.Count >= CLOpts.MaxAllocsiteRemovableUsers)
                    { Count("instcombine.memory.allocsite.user_limit"); return null; }
                    var instruction = (LlvmInstruction)user;
                    switch (instruction)
                    {
                        case LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast or LlvmOpcode.BitCast }:
                        case LlvmGetElementPtrInstruction:
                            users.Add(instruction);
                            pending.Push(instruction);
                            continue;
                        case LlvmICmpInstruction compare:
                            if (compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)) return null;
                            var other = compare.Left == pointer ? compare.Right : compare.Left;
                            if (!IsNeverEqualToUnescapedAlloc(other)) return null;
                            users.Add(instruction);
                            continue;
                        case LlvmCallBase call:
                        {
                            var descriptor = call.CalledFunction?.IntrinsicDescriptor;
                            if (descriptor is null) return null;
                            var kind = descriptor.Kind;
                            if (kind == LlvmIntrinsicKind.MemMove || kind == LlvmIntrinsicKind.MemCpy || kind == LlvmIntrinsicKind.MemSet)
                            {
                                if (MemoryIsVolatile(call)) return null;
                                var newAccess = call.GetArgument(0) == pointer ? LlvmModRefInfo.Mod : LlvmModRefInfo.Ref;
                                if ((access & ~newAccess) != LlvmModRefInfo.NoModRef) return null;
                                access |= newAccess;
                                users.Add(instruction);
                                continue;
                            }
                            if (kind == LlvmIntrinsicKind.Assume || descriptor.IsInvariantStartOrEnd || descriptor.IsLifetimeStartOrEnd)
                            { users.Add(instruction); continue; }
                            if (descriptor.IsLaunderInvariantGroup)
                            { users.Add(instruction); pending.Push(instruction); continue; }
                            // Inline transfer/set and all other intrinsic IDs are
                            // source default cases, not removable library writes.
                            return null;
                        }
                        case LlvmStoreInstruction store:
                            if (store.IsVolatile || store.PointerOperand != pointer || access.IsRefSet()) return null;
                            access |= LlvmModRefInfo.Mod;
                            users.Add(instruction);
                            continue;
                        case LlvmLoadInstruction load:
                            if (load.IsVolatile || load.PointerOperand != pointer || access.IsModSet()) return null;
                            access |= LlvmModRefInfo.Ref;
                            users.Add(instruction);
                            continue;
                        default:
                            return null;
                    }
                }
            } while (pending.Count != 0);
            return access;
        }

        private LlvmInstruction? VisitAllocSite(LlvmInstruction allocation)
        {
            Debug.Assert(allocation is LlvmAllocaInstruction);
            Count("instcombine.memory.allocsite.visits");
            var rawUsers = new List<LlvmInstruction>();
            // getInitialValueOfAllocation(AllocaInst) is undef. Sanitizer
            // function attrs have no representation in the current carrier.
            bool knowInitialUndef = allocation is LlvmAllocaInstruction;
            if (IsAllocSiteRemovable(allocation, rawUsers, knowInitialUndef) is null)
            { Count("instcombine.memory.allocsite.not_removable"); return null; }
            var users = rawUsers.Select(instruction => new LlvmWeakTrackingValueHandle(instruction)).ToArray();
            try
            {
                // Objectsize is not registered. The source transfer-to-memset
                // prepass requires zero-initialized allocation, while alloca
                // initial values are undef, so no conversion is entered here.
                foreach (var handle in users)
                {
                    if (handle.Value is not LlvmInstruction instruction) continue;
                    if (instruction is LlvmICmpInstruction compare)
                        ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Context,
                            compare.Predicate == LlvmCmpPredicate.ICmpNe));
                    else if (instruction is not LlvmStoreInstruction)
                    {
                        LlvmValue replacement = instruction is LlvmLoadInstruction
                            ? LlvmUndefValue.Get(instruction.Type) : LlvmPoisonValue.Get(instruction.Type);
                        ReplaceInstUsesWith(instruction, replacement);
                    }
                    EraseInstFromFunction(instruction);
                    Count("instcombine.memory.allocsite.users_removed");
                }
            }
            finally
            {
                foreach (var handle in users) handle.Dispose();
            }
            Count("instcombine.memory.allocsite.removed");
            return EraseInstFromFunction(allocation);
        }
    }
}
