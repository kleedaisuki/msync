// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp AllocaSlices::SliceBuilder, foldPHINodeOrSelectInst,
// and AllocaSlices construction (1023–1510).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class AllocaSlices
    {
        internal AllocaSlices(LlvmDataLayout layout, LlvmAllocaInstruction alloca,
            Dictionary<string, ulong>? metrics = null)
        {
            Alloca = alloca;
            var builder = new SliceBuilder(layout, alloca, this, metrics);
            var info = builder.VisitPtr(alloca);
            if (info.IsEscaped || info.IsAborted)
            {
                PointerEscapingInstruction = info.EscapingInstruction ?? info.AbortingInstruction;
                Debug.Assert(PointerEscapingInstruction is not null);
                if (metrics is not null)
                {
                    string key = info.IsEscaped ? "sroa.slice.escaped_allocas" : "sroa.slice.aborted_allocas";
                    metrics[key] = metrics.GetValueOrDefault(key) + 1;
                }
                return;
            }
            PointerEscapingInstructionReadOnly = info.EscapedReadOnlyInstruction;
            int removed = Slices.RemoveAll(slice => slice.IsDead);
            SortSlices();
            if (metrics is not null)
            {
                metrics["sroa.slice.dead_slices_removed"] = metrics.GetValueOrDefault("sroa.slice.dead_slices_removed") + (ulong)removed;
                metrics["sroa.slice.slices_sorted"] = metrics.GetValueOrDefault("sroa.slice.slices_sorted") + (ulong)Slices.Count;
                if (info.IsEscapedReadOnly)
                    metrics["sroa.slice.readonly_allocas"] = metrics.GetValueOrDefault("sroa.slice.readonly_allocas") + 1;
            }
        }
    }

    private sealed partial class SliceBuilder : LlvmPtrUseVisitor
    {
        private readonly ulong allocationSize;
        private readonly AllocaSlices slices;
        private readonly Dictionary<LlvmInstruction, int> transferSlices = [];
        private readonly Dictionary<LlvmInstruction, ulong> phiOrSelectSizes = [];
        private readonly HashSet<LlvmInstruction> dead = [];

        internal SliceBuilder(LlvmDataLayout layout, LlvmAllocaInstruction alloca,
            AllocaSlices slices, Dictionary<string, ulong>? metrics) : base(layout, metrics)
        {
            allocationSize = alloca.GetAllocationSize(layout)!.Value.GetFixedValue();
            this.slices = slices;
        }

        private void MarkDead(LlvmInstruction instruction)
        {
            if (!dead.Add(instruction)) return;
            slices.DeadUsers.Add(instruction);
            Count("sroa.slice.dead_users");
        }

        private void InsertUse(LlvmInstruction instruction, LlvmAPInt offset, ulong size, bool splittable = false)
        {
            if (size == 0 || UnsignedAtLeast(offset, allocationSize))
            {
                Count(size == 0 ? "sroa.slice.zero_sized_use" : "sroa.slice.outside_allocation_use");
                MarkDead(instruction);
                return;
            }
            ulong begin = offset.GetZExtValue();
            ulong end = unchecked(begin + size);
            Debug.Assert(allocationSize >= begin);
            if (size > allocationSize - begin)
            {
                end = allocationSize;
                Count("sroa.slice.clamped_use");
            }
            slices.Slices.Add(new(begin, end, CurrentUse, splittable));
            Count(splittable ? "sroa.slice.splittable" : "sroa.slice.unsplittable");
            Count("sroa.slice.bytes", end - begin);
        }

        protected override void VisitBitCast(LlvmCastInstruction instruction)
        {
            if (instruction.UseEmpty) { MarkDead(instruction); return; }
            base.VisitBitCast(instruction);
        }

        protected override void VisitAddressSpaceCast(LlvmCastInstruction instruction)
        {
            if (instruction.UseEmpty) { MarkDead(instruction); return; }
            base.VisitAddressSpaceCast(instruction);
        }

        protected override void VisitGep(LlvmGetElementPtrInstruction instruction)
        {
            if (instruction.UseEmpty) { MarkDead(instruction); return; }
            base.VisitGep(instruction);
        }

        private void HandleLoadOrStore(LlvmType type, LlvmInstruction instruction, LlvmAPInt offset,
            ulong size, bool isVolatile)
        {
            bool splittable = type.IsIntegerTy() && !isVolatile && Layout.TypeSizeEqualsStoreSize(type);
            InsertUse(instruction, offset, size, splittable);
        }

        protected override void VisitLoad(LlvmLoadInstruction instruction)
        {
            Debug.Assert(!instruction.IsSimple || instruction.Type.IsSingleValueType());
            if (!IsOffsetKnown)
            {
                PointerInfo.SetEscapedReadOnly(instruction);
                Count("sroa.slice.unknown_offset_load");
                return;
            }
            var size = Layout.GetTypeStoreSize(instruction.Type);
            if (size.IsScalable)
            {
                uint scale = instruction.Parent!.Parent!.GetVScaleValue();
                if (scale == 0) { Abort(instruction, "scalable_load"); return; }
                size = LlvmTypeSize.Fixed(unchecked(size.KnownMinValue * scale));
            }
            HandleLoadOrStore(instruction.Type, instruction, Offset, size.GetFixedValue(), instruction.IsVolatile);
        }

        protected override void VisitStore(LlvmStoreInstruction instruction)
        {
            var value = instruction.ValueOperand;
            if (ReferenceEquals(value, CurrentUse.Value))
            {
                PointerInfo.SetEscapedAndAborted(instruction);
                Count("sroa.slice.stored_pointer_escape");
                return;
            }
            if (!IsOffsetKnown) { Abort(instruction, "unknown_offset_store"); return; }
            var storeSize = Layout.GetTypeStoreSize(value.Type);
            if (storeSize.IsScalable)
            {
                uint scale = instruction.Parent!.Parent!.GetVScaleValue();
                if (scale == 0) { Abort(instruction, "scalable_store"); return; }
                storeSize = LlvmTypeSize.Fixed(unchecked(storeSize.KnownMinValue * scale));
            }
            ulong size = storeSize.GetFixedValue();
            if (size > allocationSize || UnsignedGreater(Offset, allocationSize - size))
            {
                Count("sroa.slice.outside_allocation_store");
                MarkDead(instruction);
                return;
            }
            Debug.Assert(!instruction.IsSimple || value.Type.IsSingleValueType());
            HandleLoadOrStore(value.Type, instruction, Offset, size, instruction.IsVolatile);
        }

        protected override void VisitPhi(LlvmPhiInstruction instruction) => VisitPhiOrSelect(instruction);
        protected override void VisitSelect(LlvmSelectInstruction instruction) => VisitPhiOrSelect(instruction);

        private void VisitPhiOrSelect(LlvmInstruction instruction)
        {
            if (instruction.UseEmpty) { MarkDead(instruction); return; }
            if (instruction is LlvmPhiInstruction && instruction.Parent!.GetTerminator().Opcode == LlvmOpcode.CatchSwitch)
            { Abort(instruction, "phi_without_insertion_point"); return; }
            var folded = FoldPhiOrSelect(instruction);
            if (folded is not null)
            {
                if (ReferenceEquals(folded, CurrentUse.Value))
                { EnqueueUsers(instruction); Count("sroa.slice.folded_pointer_forwarded"); }
                else
                { slices.DeadOperands.Add(CurrentUse); Count("sroa.slice.folded_dead_operands"); }
                return;
            }
            if (!IsOffsetKnown) { Abort(instruction, "unknown_offset_phi_or_select"); return; }
            ulong size = phiOrSelectSizes.GetValueOrDefault(instruction);
            if (size == 0)
            {
                var unsafeInstruction = FindUnsafePhiOrSelectUse(instruction, out size);
                phiOrSelectSizes[instruction] = size;
                if (unsafeInstruction is not null) { Abort(unsafeInstruction, "phi_or_select_user"); return; }
            }
            if (UnsignedAtLeast(Offset, allocationSize))
            {
                slices.DeadOperands.Add(CurrentUse);
                Count("sroa.slice.outside_phi_or_select_operand");
                return;
            }
            InsertUse(instruction, Offset, size);
        }

        private LlvmInstruction? FindUnsafePhiOrSelectUse(LlvmInstruction root, out ulong size)
        {
            HashSet<LlvmInstruction> visited = [root];
            List<(LlvmInstruction Used, LlvmInstruction Instruction)> uses = [((LlvmInstruction)CurrentUse.Value!, root)];
            size = 0;
            while (uses.Count != 0)
            {
                var (used, instruction) = uses[^1];
                uses.RemoveAt(uses.Count - 1);
                Count("sroa.slice.phi_users_examined");
                if (instruction is LlvmLoadInstruction load)
                {
                    var loadSize = Layout.GetTypeStoreSize(load.Type);
                    if (loadSize.IsScalable) { Abort(load, "scalable_phi_load"); return null; }
                    size = Math.Max(size, loadSize.GetFixedValue());
                    continue;
                }
                if (instruction is LlvmStoreInstruction store)
                {
                    var value = store.ValueOperand;
                    if (ReferenceEquals(value, used)) return store;
                    var storeSize = Layout.GetTypeStoreSize(value.Type);
                    if (storeSize.IsScalable) { Abort(store, "scalable_phi_store"); return null; }
                    size = Math.Max(size, storeSize.GetFixedValue());
                    continue;
                }
                if (instruction is LlvmGetElementPtrInstruction gep)
                {
                    if (!gep.HasAllZeroIndices) return gep;
                }
                else if (instruction is not (LlvmPhiInstruction or LlvmSelectInstruction) &&
                    instruction is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast })
                    return instruction;
                foreach (var user in instruction.Users)
                    if (visited.Add((LlvmInstruction)user)) uses.Add((instruction, (LlvmInstruction)user));
            }
            return null;
        }

        private static LlvmValue? FoldPhiOrSelect(LlvmInstruction instruction)
        {
            if (instruction is LlvmPhiInstruction phi) return phi.HasConstantValue();
            var select = (LlvmSelectInstruction)instruction;
            if (select.Condition is LlvmConstantInt condition) return condition.IsZero ? select.FalseValue : select.TrueValue;
            return ReferenceEquals(select.TrueValue, select.FalseValue) ? select.TrueValue : null;
        }

        protected override void VisitInstruction(LlvmInstruction instruction) => Abort(instruction, "unhandled_user");

        private void Abort(LlvmInstruction instruction, string reason)
        {
            PointerInfo.SetAborted(instruction);
            if (Metrics is not null) Count("sroa.slice.abort." + reason);
        }

        private static bool UnsignedAtLeast(LlvmAPInt value, ulong other) => value.GetActiveBits() > 64 || value.GetZExtValue() >= other;
        private static bool UnsignedGreater(LlvmAPInt value, ulong other) => value.GetActiveBits() > 64 || value.GetZExtValue() > other;
    }
}
