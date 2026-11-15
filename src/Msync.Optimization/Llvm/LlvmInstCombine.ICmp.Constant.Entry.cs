// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 3678-3955, 4147-4224.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmConstant IceConstant(LlvmValue value, LlvmAPInt integer) => LlvmConstantInt.Get(value.Type, integer);
        private LlvmInstruction? FoldICmpInstWithConstant(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.dispatch.queries");
            if (ICmpAPInt(compare.Right) is { } constant)
            {
                var c = constant.Value;
                if (compare.Left is LlvmBinaryOperator binary && FoldICmpBinOpWithConstant(compare, binary, c) is { } b) return b;
                if (compare.Left is LlvmSelectInstruction select && compare.Right is LlvmConstantInt { Type: LlvmIntegerType } scalar &&
                    FoldICmpSelectConstant(compare, select, scalar) is { } s) return s;
                if (compare.Left is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc && FoldICmpTruncConstant(compare, trunc, c) is { } t) return t;
                if (compare.Left is LlvmCallInstruction intrinsic && intrinsic.CalledFunction?.IntrinsicDescriptor is not null &&
                    FoldICmpIntrinsicWithConstant(compare, intrinsic, c) is { } i) return i;
                if (FoldICmpFrexpConstant(compare, c) is { } exponent) return exponent;
                if (c.IsZero && ICmpIsEquality(compare.Predicate) && compare.Left is LlvmExtractValueInstruction { Indices: [0], HasOneUse: true } extract &&
                    (CallsIntrinsic(extract.AggregateOperand, LlvmIntrinsicKind.SSubWithOverflow, out var sub) ||
                     CallsIntrinsic(extract.AggregateOperand, LlvmIntrinsicKind.USubWithOverflow, out sub)))
                    return IccCompare(compare.Predicate, sub.GetArgument(0), sub.GetArgument(1));
            }
            return ICmpAPInt(compare.Right, allowPoison: true) is { } poison
                ? FoldICmpInstWithConstantAllowPoison(compare, poison.Value) : null;
        }

        private LlvmInstruction? FoldICmpFrexpConstant(LlvmICmpInstruction compare, LlvmAPInt c)
        {
            if (compare.Left is not LlvmExtractValueInstruction { Indices: [1], HasOneUse: true } extract ||
                !CallsIntrinsic(extract.AggregateOperand, LlvmIntrinsicKind.Frexp, out var call) || !call.HasOneUse) return null;
            Count("icmp.constant.frexp.queries");
            var pred = compare.Predicate;
            if (pred is not (LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSgt)) return null;
            var exponent = pred == LlvmCmpPredicate.ICmpSlt ? c - IccInt(c, 1) : c;
            var input = call.GetArgument(0);
            var semantics = input.Type.GetScalarType().GetFltSemantics();
            int maximum = LlvmAPFloat.SemanticsMaxExponent(semantics);
            if (exponent.IsNegative || !exponent.Sle(IccInt(c, (ulong)maximum + 1))) return null;
            var known = LlvmKnownFPClasses.Compute(input, LlvmFPClassTest.NaN | LlvmFPClassTest.Infinity,
                compare, DT, Metrics, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC, condContext: SQ.CondContext);
            if (!known.IsKnownNeverInfOrNaN()) { Count("icmp.constant.frexp.nonfinite-unproved"); return null; }
            var bound = LlvmAPFloat.GetOne(semantics).Scalbn((int)exponent.GetSExtValue(), LlvmRoundingMode.NearestTiesToEven).Value;
            Count("icmp.constant.frexp.rewrite");
            return new LlvmFCmpInstruction(pred == LlvmCmpPredicate.ICmpSlt ? LlvmCmpPredicate.FCmpOlt : LlvmCmpPredicate.FCmpOge,
                Builder.CreateFAbs(input), LlvmConstantFP.Get(input.Type, bound));
        }

        private LlvmInstruction? FoldICmpInstWithConstantAllowPoison(LlvmICmpInstruction compare, LlvmAPInt c)
        {
            Count("icmp.constant.allow-poison.queries");
            if (compare.Left is not LlvmCallInstruction call) return null;
            var kind = call.CalledFunction?.IntrinsicDescriptor?.Kind;
            if ((kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr) && ICmpIsEquality(compare.Predicate) &&
                call.GetArgument(0) == call.GetArgument(1) && (c.IsZero || c.IsAllOnes))
                return IccCompare(compare.Predicate, call.GetArgument(0), compare.Right);
            return null;
        }

        private LlvmInstruction? FoldICmpBinOpWithConstant(LlvmICmpInstruction compare, LlvmBinaryOperator binary, LlvmAPInt c)
        {
            Count("icmp.constant.binary.dispatch.queries");
            if (Metrics is not null) Count("icmp.constant.binary.dispatch." + binary.Opcode);
            LlvmInstruction? folded = binary.Opcode switch
            {
                LlvmOpcode.Xor => FoldICmpXorConstant(compare, binary, c),
                LlvmOpcode.And => FoldICmpAndConstant(compare, binary, c),
                LlvmOpcode.Or => FoldICmpOrConstant(compare, binary, c),
                LlvmOpcode.Mul => FoldICmpMulConstant(compare, binary, c),
                LlvmOpcode.Shl => FoldICmpShlConstant(compare, binary, c),
                LlvmOpcode.LShr or LlvmOpcode.AShr => FoldICmpShrConstant(compare, binary, c),
                LlvmOpcode.SRem => FoldICmpSRemConstant(compare, binary, c),
                LlvmOpcode.UDiv => FoldICmpUDivConstant(compare, binary, c) ?? FoldICmpDivConstant(compare, binary, c),
                LlvmOpcode.SDiv => FoldICmpDivConstant(compare, binary, c),
                LlvmOpcode.Sub => FoldICmpSubConstant(compare, binary, c),
                LlvmOpcode.Add => FoldICmpAddConstant(compare, binary, c),
                _ => null
            };
            return folded ?? FoldICmpBinOpEqualityWithConstant(compare, binary, c) ?? FoldICmpBinOpWithConstantViaTruthTable(compare, binary, c);
        }

        private LlvmInstruction? FoldICmpBinOpEqualityWithConstant(LlvmICmpInstruction compare, LlvmBinaryOperator binary, LlvmAPInt c)
        {
            Count("icmp.constant.binary.equality.queries");
            if (!ICmpIsEquality(compare.Predicate)) return null;
            var pred = compare.Predicate;
            var a = binary.Left; var b = binary.Right; var rhs = (LlvmConstant)compare.Right;
            switch (binary.Opcode)
            {
                case LlvmOpcode.SRem:
                    if (c.IsZero && binary.HasOneUse && ICmpAPInt(b) is { } rem && rem.Value.Sgt(IccInt(c, 1)) && rem.Value.IsPowerOf2)
                        return IccCompare(pred, Builder.CreateBinOp(LlvmOpcode.URem, a, b, binary.Name), ICmpZero(binary));
                    break;
                case LlvmOpcode.Add:
                    if (b is LlvmConstant addConstant)
                    {
                        if (binary.HasOneUse) return IccCompare(pred, a, LlvmConstantExpr.GetSub(rhs, addConstant));
                    }
                    else if (c.IsZero)
                    {
                        if (DynCastNegVal(b) is { } nb) return IccCompare(pred, a, nb);
                        if (DynCastNegVal(a) is { } na) return IccCompare(pred, na, b);
                        if (binary.HasOneUse)
                        {
                            if (binary.HasNoUnsignedWrap) return IccCompare(pred, Builder.CreateOr(a, b), ICmpZero(binary));
                            var neg = Builder.CreateNeg(b); neg.TakeName(binary);
                            return IccCompare(pred, a, neg);
                        }
                    }
                    break;
                case LlvmOpcode.Xor:
                    if (b is LlvmConstant xorConstant) return IccCompare(pred, a, LlvmConstantExpr.GetXor(rhs, xorConstant));
                    if (c.IsZero) return IccCompare(pred, a, b);
                    break;
                case LlvmOpcode.Or:
                    if (ICmpAPInt(b) is not null && binary.HasOneUse && ICmpConstantMatches(rhs, x => x.IsAllOnes))
                    {
                        var mask = LlvmConstantExpr.GetNot((LlvmConstant)b);
                        return IccCompare(pred, Builder.CreateAnd(a, mask), mask);
                    }
                    if (c.IsZero && binary.HasOneUse)
                    {
                        LlvmSelectInstruction? select = a as LlvmSelectInstruction ?? b as LlvmSelectInstruction;
                        if (select is null || select.Condition.Type != compare.Type) break;
                        var other = a == select ? b : a;
                        bool eq = pred == LlvmCmpPredicate.ICmpEq;
                        bool easy = eq ? ICmpConstantMatches(select.TrueValue, x => x.IsZero) && IsKnownNonZero(select.FalseValue, compare)
                            : ICmpConstantMatches(select.FalseValue, x => x.IsZero) && IsKnownNonZero(select.TrueValue, compare);
                        bool hard = eq ? ICmpConstantMatches(select.FalseValue, x => x.IsZero) && IsKnownNonZero(select.TrueValue, compare)
                            : ICmpConstantMatches(select.TrueValue, x => x.IsZero) && IsKnownNonZero(select.FalseValue, compare);
                        if (!easy && !(select.HasOneUse && hard)) break;
                        var condition = easy ? select.Condition : Builder.CreateNot(select.Condition);
                        var test = Builder.CreateICmp(pred, other, ICmpZero(other));
                        Count("icmp.constant.binary.or-select.rewrite");
                        return new LlvmBinaryOperator(eq ? LlvmOpcode.And : LlvmOpcode.Or, test, condition);
                    }
                    break;
                case LlvmOpcode.UDiv:
                case LlvmOpcode.SDiv:
                    if (binary.IsExact)
                    {
                        if (c.IsZero) return IccCompare(pred, a, ICmpZero(binary));
                        if (c.IsOne) return IccCompare(pred, a, b);
                        if (binary.HasOneUse && ComputeOverflow(LlvmOpcode.Mul, binary.Opcode == LlvmOpcode.SDiv, b, rhs, binary) == LlvmOverflowResult.NeverOverflows)
                            return IccCompare(pred, Builder.CreateMul(b, IceConstant(binary, c)), a);
                    }
                    if (binary.Opcode == LlvmOpcode.UDiv && c.IsZero)
                        return IccCompare(pred == LlvmCmpPredicate.ICmpNe ? LlvmCmpPredicate.ICmpUle : LlvmCmpPredicate.ICmpUgt, b, a);
                    break;
            }
            return null;
        }
    }
}
