// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 4655-4966.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool IsMaskOrZero(LlvmValue value, bool not, LlvmInstructionSimplify.BinaryQuery query, uint depth = 0)
        {
            Count("icmp.binop.mask-proof.nodes");
            if (ICmpConstantMatches(value, bits => not ? bits.IsZero || (-bits).IsPowerOf2 :
                (bits + new LlvmAPInt(bits.BitWidth, 1)).IsZero || (bits + new LlvmAPInt(bits.BitWidth, 1)).IsPowerOf2)) return true;
            if (value.Type.GetScalarSizeInBits() == 1) return true;
            if (depth++ >= 6) { Count("icmp.binop.mask-proof.depth-limit"); return false; }
            if (value is LlvmCastInstruction cast)
            {
                if (cast.Opcode == LlvmOpcode.ZExt) return !not && IsMaskOrZero(cast.Operand, not, query, depth);
                if (cast.Opcode == LlvmOpcode.SExt) return IsMaskOrZero(cast.Operand, not, query, depth);
            }
            if (value is LlvmSelectInstruction select)
                return IsMaskOrZero(select.TrueValue, not, query, depth) && IsMaskOrZero(select.FalseValue, not, query, depth);
            if (value is LlvmBinaryOperator binary)
            {
                switch (binary.Opcode)
                {
                    case LlvmOpcode.And: case LlvmOpcode.Or:
                        return IsMaskOrZero(binary.Right, not, query, depth) && IsMaskOrZero(binary.Left, not, query, depth);
                    case LlvmOpcode.Xor:
                        if (ICmpMatchNot(value) is { } inverse) return IsMaskOrZero(inverse, !not, query, depth);
                        for (int side = 0; side < 2; side++)
                        {
                            var x = side == 0 ? binary.Left : binary.Right; var y = side == 0 ? binary.Right : binary.Left;
                            if (not && y is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub && sub.Right == x && ICmpConstantMatches(sub.Left, bits => bits.IsZero)) return true;
                            if (!not && BinMatchCommon(y, LlvmOpcode.Add, x, out var minus) && ICmpConstantMatches(minus, bits => bits.IsAllOnes)) return true;
                        }
                        return false;
                    case LlvmOpcode.Shl: return not && IsMaskOrZero(binary.Left, not, query, depth);
                    case LlvmOpcode.LShr: return !not && IsMaskOrZero(binary.Left, not, query, depth);
                    case LlvmOpcode.AShr: return IsMaskOrZero(binary.Left, not, query, depth);
                    case LlvmOpcode.Add:
                        return !not && ICmpConstantMatches(binary.Right, bits => bits.IsAllOnes) && IsKnownToBeAPowerOfTwo(binary.Left, true, query.Context, depth);
                    case LlvmOpcode.Sub:
                        return not && ICmpConstantMatches(binary.Left, bits => bits.IsZero) && IsKnownToBeAPowerOfTwo(binary.Right, true, query.Context, depth);
                }
            }
            if (value is LlvmCallInstruction call)
            {
                if (CallsIntrinsic(call, LlvmIntrinsicKind.UMax, out _) || CallsIntrinsic(call, LlvmIntrinsicKind.SMax, out _) || CallsIntrinsic(call, LlvmIntrinsicKind.UMin, out _) || CallsIntrinsic(call, LlvmIntrinsicKind.SMin, out _))
                    return IsMaskOrZero(call.GetArgument(1), not, query, depth) && IsMaskOrZero(call.GetArgument(0), not, query, depth);
                if (CallsIntrinsic(call, LlvmIntrinsicKind.BitReverse, out _)) return IsMaskOrZero(call.GetArgument(0), !not, query, depth);
            }
            return false;
        }
        private LlvmValue? FoldICmpWithLowBitMaskedVal(LlvmCmpPredicate pred, LlvmValue op0, LlvmValue op1, LlvmInstructionSimplify.BinaryQuery query)
        {
            Count("icmp.binop.low-mask.queries");
            var dst = pred switch
            {
                LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUle,
                LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUgt,
                LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSgt, LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSle,
                _ => (LlvmCmpPredicate)0
            };
            if (dst == 0) return null;
            if (BinMatchCommon(op0, LlvmOpcode.And, op1, out var mask))
            {
                if (IsMaskOrZero(mask, false, query) && (!ICmpIsSigned(pred) || IsKnownNonNegative(mask, query.Context)) ||
                    IsMaskOrZero(op1, true, query) && (!ICmpIsSigned(pred) || IsKnownNonZero(op1, query.Context)))
                { Count("icmp.binop.low-mask.self"); return Builder.CreateICmp(dst, op1, mask); }
                return null;
            }
            if (!ICmpIsEquality(pred) || !op0.HasOneUse || op0 is not LlvmBinaryOperator binary) return null;
            bool or = binary.Opcode == LlvmOpcode.Or && ICmpConstantMatches(op1, bits => bits.IsAllOnes);
            bool and = binary.Opcode == LlvmOpcode.And && ICmpConstantMatches(op1, bits => bits.IsZero);
            if (!or && !and) return null;
            for (int side = 0; side < 2; side++)
            {
                var x = side == 0 ? binary.Left : binary.Right; var m = side == 0 ? binary.Right : binary.Left;
                if (!IsMaskOrZero(m, and, query)) continue;
                var inverted = GetFreelyInverted(or ? x : m, (or ? x : m).HasOneUse, Builder);
                if (inverted is null) continue;
                Count(or ? "icmp.binop.low-mask.or" : "icmp.binop.low-mask.and");
                return Builder.CreateICmp(dst, or ? inverted : x, and ? inverted : m);
            }
            return null;
        }
        private LlvmValue? FoldICmpWithTruncSignExtendedVal(LlvmICmpInstruction compare)
        {
            if (!ICmpIsEquality(compare.Predicate)) return null;
            for (int side = 0; side < 2; side++)
            {
                var value = side == 0 ? compare.Left : compare.Right; var x = side == 0 ? compare.Right : compare.Left;
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, HasOneUse: true, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shl } ashr || shl.Left != x) continue;
                var c0 = ICmpAPInt(shl.Right); var c1 = ICmpAPInt(ashr.Right);
                if (c0 is null || c1 is null || c0.Value != c1.Value) continue;
                uint width = x.Type.GetScalarSizeInBits(); uint kept = width - (uint)c0.Value.GetLimitedValue(width);
                var bound = new LlvmAPInt(width, 1).Shl(kept);
                Count("icmp.binop.signed-trunc-range");
                return Builder.CreateICmp(compare.Predicate == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge,
                    Builder.CreateAdd(x, BinConstant(x.Type, bound.LShr(1))), BinConstant(x.Type, bound));
            }
            return null;
        }
    }
}
