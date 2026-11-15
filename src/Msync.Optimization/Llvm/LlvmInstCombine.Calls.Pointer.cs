// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// ptrmask visitor; Value.h MaxAlignmentExponent=63.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldPtrMask(LlvmCallInstruction call)
        {
            Count("calls.ptrmask.visited");
            uint width = DL.GetPointerTypeSizeInBits(call.Type);
            LlvmKnownBits known = new(width);
            if (SimplifyDemandedInstructionBits(call, ref known)) return call;
            bool changed = false;
            if (CallsIntrinsic(call.GetArgument(0), LlvmIntrinsicKind.PtrMask, out var inner) && inner.HasOneUse)
            {
                LlvmValue mask = Builder.CreateAnd(call.GetArgument(1), inner.GetArgument(1));
                ReplaceOperand(call, 0, inner.GetArgument(0)); ReplaceOperand(call, 1, mask);
                Count("calls.ptrmask.nested_folded"); changed = true;
            }
            if (!call.HasRetAttr(LlvmAttributeKind.NonNull) && (known.IsNonZero || IsKnownNonZero(call, call)))
            {
                call.ReturnAttributes = call.ReturnAttributes.Add(LlvmAttributeKind.NonNull);
                Count("calls.ptrmask.nonnull_added"); changed = true;
            }
            uint alignmentLog = Math.Min(63u, Math.Min(width - 1, known.CountMinTrailingZeros()));
            if (alignmentLog > BitOperations.Log2(call.GetRetAlign()?.Value ?? 1))
            {
                call.ReturnAttributes = call.ReturnAttributes with { Alignment = new LlvmAlign(1UL << (int)alignmentLog) };
                Count("calls.ptrmask.alignment_added"); changed = true;
            }
            return changed ? call : null;
        }
    }
}
