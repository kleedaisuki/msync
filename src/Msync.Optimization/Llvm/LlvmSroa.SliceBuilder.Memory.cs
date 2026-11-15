// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SROA.cpp SliceBuilder memory intrinsic/lifetime/call visitors (1205–1324, 1472–1487).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private sealed partial class SliceBuilder
    {
        protected override void VisitIntrinsic(LlvmCallBase instruction)
        {
            if (LlvmMemIntrinsic.TryGet(instruction, out var memory))
            {
                if (memory.IsMemSet) VisitMemSet(memory);
                else VisitMemTransfer(memory);
                return;
            }
            if (instruction.IsDroppable())
            {
                slices.DeadUseIfPromotable.Add(CurrentUse);
                Count("sroa.slice.droppable_operands");
                return;
            }
            if (!IsOffsetKnown) { Abort(instruction, "unknown_offset_intrinsic"); return; }
            if (instruction.CalledFunction!.IntrinsicDescriptor!.IsLifetimeStartOrEnd)
            {
                InsertUse(instruction, Offset, allocationSize, true);
                Count("sroa.slice.lifetime_uses");
                return;
            }
            base.VisitIntrinsic(instruction);
        }

        private void VisitMemSet(LlvmMemIntrinsic memory)
        {
            var instruction = memory.Call;
            Debug.Assert(ReferenceEquals(memory.RawDest, CurrentUse.Value));
            var length = memory.Length as LlvmConstantInt;
            if (length is { IsZero: true } || IsOffsetKnown && UnsignedAtLeast(Offset, allocationSize))
            { MarkDead(instruction); Count("sroa.slice.dead_memset"); return; }
            if (!IsOffsetKnown) { Abort(instruction, "unknown_offset_memset"); return; }
            InsertUse(instruction, Offset, length is not null ? length.Value.GetLimitedValue() : allocationSize - Offset.GetLimitedValue(),
                length is not null);
            Count("sroa.slice.memset_uses");
        }

        private void VisitMemTransfer(LlvmMemIntrinsic memory)
        {
            var instruction = memory.Call;
            var length = memory.Length as LlvmConstantInt;
            if (length is { IsZero: true })
            { MarkDead(instruction); Count("sroa.slice.zero_length_transfer"); return; }
            if (dead.Contains(instruction)) return;
            if (!IsOffsetKnown) { Abort(instruction, "unknown_offset_transfer"); return; }
            if (UnsignedAtLeast(Offset, allocationSize))
            {
                if (transferSlices.TryGetValue(instruction, out int index)) slices.Slices[index].Kill();
                MarkDead(instruction);
                Count("sroa.slice.outside_transfer");
                return;
            }
            ulong rawOffset = Offset.GetLimitedValue();
            ulong size = length is not null ? length.Value.GetLimitedValue() : allocationSize - rawOffset;
            if (ReferenceEquals(CurrentUse.Value, memory.RawDest) && ReferenceEquals(CurrentUse.Value, memory.RawSource))
            {
                if (!memory.IsVolatile) { MarkDead(instruction); Count("sroa.slice.identity_transfer"); return; }
                InsertUse(instruction, Offset, size);
                return;
            }
            bool inserted = transferSlices.TryAdd(instruction, slices.Slices.Count);
            int previousIndex = transferSlices[instruction];
            if (!inserted)
            {
                var previous = slices.Slices[previousIndex];
                if (!memory.IsVolatile && previous.BeginOffset == rawOffset)
                {
                    previous.Kill();
                    MarkDead(instruction);
                    Count("sroa.slice.same_offset_transfer");
                    return;
                }
                previous.MakeUnsplittable();
                Count("sroa.slice.overlapping_transfer");
            }
            InsertUse(instruction, Offset, size, inserted && length is not null);
            Debug.Assert(ReferenceEquals(slices.Slices[previousIndex].Use!.User, instruction));
            Count("sroa.slice.transfer_uses");
        }

        protected override void VisitCall(LlvmCallBase instruction)
        {
            if (instruction.IsDataOperand(CurrentUse) &&
                !instruction.GetCaptureInfo(CurrentUse.OperandNo).CapturesFullProvenance &&
                instruction.OnlyReadsMemory(CurrentUse.OperandNo))
            {
                PointerInfo.SetEscapedReadOnly(instruction);
                Count("sroa.slice.readonly_call_escape");
                return;
            }
            Count("sroa.slice.call_escape");
            base.VisitCall(instruction);
        }
    }
}
