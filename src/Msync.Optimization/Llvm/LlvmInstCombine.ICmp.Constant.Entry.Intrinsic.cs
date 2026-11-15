// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 3956-4146, 4225-4484.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldCtpopPow2Test(LlvmICmpInstruction compare, LlvmCallInstruction call, LlvmAPInt c)
        {
            Count("icmp.constant.ctpop.pow2.queries");
            if (!call.HasOneUse) return null;
            var pred = compare.Predicate;
            if (!((ICmpIsEquality(pred) || pred == LlvmCmpPredicate.ICmpUgt) && c.IsOne || pred == LlvmCmpPredicate.ICmpUlt && c == IccInt(c, 2))) return null;
            var input = call.GetArgument(0);
            var known = ComputeKnownBits(input, compare);
            if (known.One.PopCount() != 1) return null;
            return IccCompare(pred is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUlt ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe,
                Builder.CreateAnd(input, IceConstant(input, ~known.One)), ICmpZero(input));
        }

        private LlvmInstruction? FoldICmpEqIntrinsicWithConstant(LlvmICmpInstruction compare, LlvmCallInstruction call, LlvmAPInt c)
        {
            Count("icmp.constant.intrinsic.equality.queries");
            var kind = call.CalledFunction!.IntrinsicDescriptor!.Kind;
            if (Metrics is not null) Count("icmp.constant.intrinsic.kind." + kind.Name);
            var pred = compare.Predicate; uint width = c.BitWidth;
            var a = call.GetArgument(0);
            if (kind == LlvmIntrinsicKind.Abs && (c.IsZero || c.IsMinSignedValue)) return IccCompare(pred, a, IceConstant(call, c));
            if (kind == LlvmIntrinsicKind.BSwap) return IccCompare(pred, a, IceConstant(call, c.ByteSwap()));
            if (kind == LlvmIntrinsicKind.BitReverse) return IccCompare(pred, a, IceConstant(call, c.ReverseBits()));
            if (kind == LlvmIntrinsicKind.CTLZ || kind == LlvmIntrinsicKind.CTTZ)
            {
                if (c == IccInt(c, width)) return IccCompare(pred, a, ICmpZero(call));
                uint num = (uint)c.GetLimitedValue(width);
                if (num != width && call.HasOneUse)
                {
                    bool trailing = kind == LlvmIntrinsicKind.CTTZ;
                    var mask = trailing ? LlvmAPInt.GetLowBitsSet(width, num + 1) : LlvmAPInt.GetHighBitsSet(width, num + 1);
                    var bit = LlvmAPInt.GetOneBitSet(width, trailing ? num : width - num - 1);
                    return IccCompare(pred, Builder.CreateAnd(a, IceConstant(call, mask)), IceConstant(call, bit));
                }
            }
            if (kind == LlvmIntrinsicKind.CTPop && (c.IsZero || c == IccInt(c, width))) return IccCompare(pred, a, c.IsZero ? ICmpZero(call) : ICmpOnes(call));
            if ((kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr) && a == call.GetArgument(1) && ICmpAPInt(call.GetArgument(2)) is { } amount)
            {
                // APInt rotation reduces the shift modulo the operand width.
                uint shift = (uint)amount.Value.URem(new(amount.Value.BitWidth, width)).GetZExtValue();
                var rotated = shift == 0 ? c : kind == LlvmIntrinsicKind.FShl
                    ? c.LShr(shift) | c.Shl(width - shift) : c.Shl(shift) | c.LShr(width - shift);
                return IccCompare(pred, a, IceConstant(call, rotated));
            }
            if ((kind == LlvmIntrinsicKind.UMax || kind == LlvmIntrinsicKind.UAddSat) && c.IsZero && call.HasOneUse)
                return IccCompare(pred, Builder.CreateOr(a, call.GetArgument(1)), ICmpZero(call));
            if (kind == LlvmIntrinsicKind.SSubSat && c.IsZero && width > 1) return IccCompare(pred, a, call.GetArgument(1));
            if (kind == LlvmIntrinsicKind.USubSat && c.IsZero)
                return IccCompare(pred == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUle : LlvmCmpPredicate.ICmpUgt, a, call.GetArgument(1));
            return null;
        }

        private LlvmInstruction? FoldICmpUSubSatOrUAddSatWithConstant(LlvmCmpPredicate pred, LlvmCallInstruction call, LlvmAPInt c)
        {
            Count("icmp.constant.saturation.range.queries");
            if (!call.HasOneUse || ICmpAPInt(call.GetArgument(1)) is not { } amount) return null;
            bool add = call.CalledFunction!.IntrinsicDescriptor!.Kind == LlvmIntrinsicKind.UAddSat;
            var sat = add ? LlvmAPInt.GetAllOnes(c.BitWidth) : LlvmAPInt.GetZero(c.BitWidth);
            bool satCheck = LlvmConstantRange.MakeExactICmpRegion(pred, c).Contains(sat);
            var nowrap = LlvmConstantRange.MakeExactNoWrapRegion(add ? LlvmOpcode.Add : LlvmOpcode.Sub, amount.Value, 1);
            if (satCheck) nowrap = nowrap.Inverse();
            var region = LlvmConstantRange.MakeExactICmpRegion(pred, c);
            region = add ? region.Sub(new LlvmConstantRange(amount.Value)) : region.Add(new LlvmConstantRange(amount.Value));
            var combination = satCheck ? nowrap.ExactUnionWith(region) : nowrap.ExactIntersectWith(region);
            if (combination is null) { Count("icmp.constant.saturation.range.not-exact"); return null; }
            combination.GetEquivalentICmp(out var equivalent, out var bound, out var offset);
            return IccCompare(equivalent, Builder.CreateAdd(call.GetArgument(0), IceConstant(call.GetArgument(1), offset)), IceConstant(call.GetArgument(1), bound));
        }

        private LlvmInstruction? FoldICmpOfCmpIntrinsicWithConstant(LlvmCmpPredicate pred, LlvmCallInstruction call, LlvmAPInt c)
        {
            LlvmCmpPredicate? replacement = pred switch
            {
                LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe when c.IsZero => pred,
                LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe when c.IsOne => pred == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUgt : LlvmCmpPredicate.ICmpUle,
                LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe when c.IsAllOnes => pred == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge,
                LlvmCmpPredicate.ICmpSgt when c.IsAllOnes => LlvmCmpPredicate.ICmpUge,
                LlvmCmpPredicate.ICmpSgt when c.IsZero => LlvmCmpPredicate.ICmpUgt,
                LlvmCmpPredicate.ICmpSlt when c.IsZero => LlvmCmpPredicate.ICmpUlt,
                LlvmCmpPredicate.ICmpSlt when c.IsOne => LlvmCmpPredicate.ICmpUle,
                LlvmCmpPredicate.ICmpUlt when c.Ugt(IccInt(c, 1)) => LlvmCmpPredicate.ICmpUge,
                LlvmCmpPredicate.ICmpUgt when !c.IsZero && !c.IsAllOnes => LlvmCmpPredicate.ICmpUlt,
                _ => null
            };
            if (replacement is not { } p) return null;
            if (call.CalledFunction!.IntrinsicDescriptor!.Kind == LlvmIntrinsicKind.SCmp) p = ICmpSignedPredicate(p);
            return IccCompare(p, call.GetArgument(0), call.GetArgument(1));
        }

        private LlvmInstruction? FoldICmpIntrinsicWithConstant(LlvmICmpInstruction compare, LlvmCallInstruction call, LlvmAPInt c)
        {
            Count("icmp.constant.intrinsic.dispatch.queries");
            var kind = call.CalledFunction!.IntrinsicDescriptor!.Kind;
            if (Metrics is not null) Count("icmp.constant.intrinsic.kind." + kind.Name);
            if ((kind == LlvmIntrinsicKind.UAddSat || kind == LlvmIntrinsicKind.USubSat) && FoldICmpUSubSatOrUAddSatWithConstant(compare.Predicate, call, c) is { } sat) return sat;
            if (kind == LlvmIntrinsicKind.CTPop && FoldCtpopPow2Test(compare, call, c) is { } pop) return pop;
            if ((kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp) && FoldICmpOfCmpIntrinsicWithConstant(compare.Predicate, call, c) is { } cmp) return cmp;
            if (ICmpIsEquality(compare.Predicate)) return FoldICmpEqIntrinsicWithConstant(compare, call, c);
            var pred = compare.Predicate; uint width = c.BitWidth;
            var a = call.GetArgument(0);
            if (kind == LlvmIntrinsicKind.CTPop)
            {
                if (c == IccInt(c, width - 1) && pred == LlvmCmpPredicate.ICmpUgt) return IccCompare(LlvmCmpPredicate.ICmpEq, a, ICmpOnes(call));
                if (c == IccInt(c, width) && pred == LlvmCmpPredicate.ICmpUlt) return IccCompare(LlvmCmpPredicate.ICmpNe, a, ICmpOnes(call));
            }
            if (kind == LlvmIntrinsicKind.CTLZ)
            {
                if (pred == LlvmCmpPredicate.ICmpUgt && c.Ult(IccInt(c, width)))
                    return IccCompare(LlvmCmpPredicate.ICmpUlt, a, IceConstant(call, LlvmAPInt.GetOneBitSet(width, width - (uint)c.GetLimitedValue() - 1)));
                if (pred == LlvmCmpPredicate.ICmpUlt && c.Uge(IccInt(c, 1)) && c.Ule(IccInt(c, width)))
                    return IccCompare(LlvmCmpPredicate.ICmpUgt, a, IceConstant(call, LlvmAPInt.GetLowBitsSet(width, width - (uint)c.GetLimitedValue())));
            }
            if (kind == LlvmIntrinsicKind.CTTZ && call.HasOneUse)
            {
                if (pred == LlvmCmpPredicate.ICmpUgt && c.Ult(IccInt(c, width)))
                    return IccCompare(LlvmCmpPredicate.ICmpEq, Builder.CreateAnd(a, IceConstant(call, LlvmAPInt.GetLowBitsSet(width, (uint)c.GetLimitedValue() + 1))), ICmpZero(call));
                if (pred == LlvmCmpPredicate.ICmpUlt && c.Uge(IccInt(c, 1)) && c.Ule(IccInt(c, width)))
                    return IccCompare(LlvmCmpPredicate.ICmpNe, Builder.CreateAnd(a, IceConstant(call, LlvmAPInt.GetLowBitsSet(width, (uint)c.GetLimitedValue()))), ICmpZero(call));
            }
            if (kind == LlvmIntrinsicKind.SSubSat && ICmpIsSigned(pred) && width > 1)
            {
                if (c.IsZero) return IccCompare(pred, a, call.GetArgument(1));
                if (pred == LlvmCmpPredicate.ICmpSlt && c.IsOne) return IccCompare(LlvmCmpPredicate.ICmpSle, a, call.GetArgument(1));
                if (pred == LlvmCmpPredicate.ICmpSgt && c.IsAllOnes) return IccCompare(LlvmCmpPredicate.ICmpSge, a, call.GetArgument(1));
            }
            if (kind == LlvmIntrinsicKind.Abs && call.HasOneUse)
            {
                if (pred == LlvmCmpPredicate.ICmpUgt && !c.IsNegative)
                    return IccCompare(LlvmCmpPredicate.ICmpUgt, Builder.CreateAdd(a, IceConstant(call, c)), IceConstant(call, c + c));
                if (pred == LlvmCmpPredicate.ICmpUlt && c.Sge(IccInt(c, 1)))
                {
                    var limit = c - IccInt(c, 1);
                    return IccCompare(LlvmCmpPredicate.ICmpUle, Builder.CreateAdd(a, IceConstant(call, limit)), IceConstant(call, limit + limit));
                }
            }
            return null;
        }
    }
}
