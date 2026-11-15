// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// abs/scmp/ucmp, overflow-common and scalar saturating visitors.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldAbs(LlvmCallInstruction call)
        {
            Count("calls.abs.visited");
            LlvmValue value = call.GetArgument(0);
            bool poison = CallsIsOne(call.GetArgument(1));
            if (CallsNeg(value, out var operand))
                return CallsNewIntrinsic(call, LlvmIntrinsicKind.Abs, [operand,
                    LlvmConstantInt.GetBool(call.Context, poison || ((LlvmBinaryOperator)value).HasNoSignedWrap)]);
            if (value is LlvmSelectInstruction select
                && (CallsNeg(select.TrueValue, out var trueNeg) && ReferenceEquals(trueNeg, select.FalseValue)
                    || CallsNeg(select.FalseValue, out trueNeg) && ReferenceEquals(trueNeg, select.TrueValue)))
                return CallsNewIntrinsic(call, LlvmIntrinsicKind.Abs, [trueNeg, call.GetArgument(1)]);
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true } mul)
            {
                LlvmValue? other = null, inner = null;
                if (CallsIntrinsic(mul.Left, LlvmIntrinsicKind.Abs, out var abs)) { other = mul.Right; inner = abs.GetArgument(0); }
                else if (CallsIntrinsic(mul.Right, LlvmIntrinsicKind.Abs, out abs)) { other = mul.Left; inner = abs.GetArgument(0); }
                if (other is not null)
                    return CallsNewIntrinsic(call, LlvmIntrinsicKind.Abs, [Builder.CreateMul(other, inner!, nsw: mul.HasNoSignedWrap && poison), call.GetArgument(1)]);
            }
            if (CallsKnownSign(value, call, orZero: true) is { } negative)
            {
                if (!negative) return ReplaceInstUsesWith(call, value);
                return new LlvmBinaryOperator(LlvmOpcode.Sub, CallsInt(call.Type, 0), value) { HasNoSignedWrap = poison };
            }
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } extension)
                return new LlvmCastInstruction(LlvmOpcode.ZExt,
                    Builder.CreateIntrinsic(LlvmIntrinsicKind.Abs, [extension.Operand, LlvmConstantInt.GetFalse(call.Context)]), call.Type);
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.SRem, Right: LlvmConstantInt constant } rem && constant.EqualsInt(2))
                return new LlvmBinaryOperator(LlvmOpcode.And, rem.Left, CallsInt(call.Type, 1));
            return null;
        }
        private LlvmInstruction? FoldCompareIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.cmp.visited");
            if (FoldCmpIntrinsicOfExtended(call, kind) is { } extended) return ReplaceInstUsesWith(call, extended);
            LlvmValue first = call.GetArgument(0), second = call.GetArgument(1);
            LlvmCmpPredicate predicate = kind == LlvmIntrinsicKind.SCmp ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt;
            if (MatchCommonBinOpOperands(first, second, predicate, out var left, out var right, Query(call)))
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(kind, [left, right], (LlvmIntegerType)call.Type));
            if (kind != LlvmIntrinsicKind.SCmp) return null;
            if (CallsIsZero(second))
            {
                var query = Query(call);
                LlvmConstantRange range = LlvmConstantRangeAnalysis.Compute(first, true, context: call, tree: query.Tree,
                    metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions,
                    allowEphemerals: query.AllowEphemerals, conditions: query.DC);
                uint width = range.BitWidth;
                if (range.GetSignedMin().Sge(LlvmAPInt.GetAllOnes(width)) && range.GetSignedMax().Sle(new(width, 1)))
                    return ReplaceInstUsesWith(call, Builder.CreateSExtOrTrunc(first, call.Type));
                if (first is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } sub)
                    return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(kind, [sub.Left, sub.Right], (LlvmIntegerType)call.Type));
            }
            return null;
        }
        private LlvmValue? FoldCmpIntrinsicOfExtended(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            int index = 0;
            LlvmCastInstruction? extension = call.GetArgument(0) as LlvmCastInstruction;
            if (extension?.Opcode is not (LlvmOpcode.ZExt or LlvmOpcode.SExt))
            { index = 1; extension = call.GetArgument(1) as LlvmCastInstruction; }
            if (extension?.Opcode is not (LlvmOpcode.ZExt or LlvmOpcode.SExt)) return null;
            LlvmValue first = extension.Operand, second;
            LlvmValue other = call.GetArgument(1 - index);
            if (other is LlvmCastInstruction cast && cast.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt)
            {
                if (cast.Opcode != extension.Opcode || !ReferenceEquals(cast.Operand.Type, first.Type)) return null;
                second = cast.Operand;
            }
            else if (other is LlvmConstantInt constant && CallsLosslessTrunc(constant, first.Type, extension.Opcode == LlvmOpcode.SExt) is { } narrowed)
                second = narrowed;
            else return null;
            LlvmIntrinsicKind replacement = kind == LlvmIntrinsicKind.SCmp && extension.Opcode == LlvmOpcode.SExt ? LlvmIntrinsicKind.SCmp : LlvmIntrinsicKind.UCmp;
            if (index != 0) (first, second) = (second, first);
            return Builder.CreateIntrinsic(replacement, [first, second], (LlvmIntegerType)call.Type);
        }
        private static LlvmInsertValueInstruction CreateOverflowTuple(LlvmCallInstruction call, LlvmValue result, LlvmConstant overflow)
        {
            LlvmConstant tuple = LlvmConstantStruct.Get((LlvmStructType)call.Type, [LlvmPoisonValue.Get(result.Type), overflow]);
            return new LlvmInsertValueInstruction(tuple, result, [0]);
        }
        private LlvmInstruction? FoldIntrinsicWithOverflowCommon(LlvmCallInstruction call, LlvmOpcode opcode, bool signed)
        {
            if (OptimizeOverflowCheck(opcode, signed, call.GetArgument(0), call.GetArgument(1), call, out var result, out var overflow))
                return CreateOverflowTuple(call, result, overflow);
            foreach (LlvmUser user in call.Users)
            {
                if (user is not LlvmExtractValueInstruction { IndexCount: 1 } extract || extract.Indices[0] != 1) continue;
                foreach (var entry in AC.AssumptionsFor(extract))
                {
                    if (entry.Assume.Value is not LlvmCallInstruction assumption || !CallsNot(assumption.GetArgument(0), out var condition)
                        || !ReferenceEquals(condition, extract)
                        || !LlvmValueTracking.IsValidAssumeForContext(assumption, call, allowEphemerals: true, metrics: Metrics)) continue;
                    LlvmValue operation = Builder.CreateBinary(opcode, call.GetArgument(0), call.GetArgument(1), nuw: !signed, nsw: signed);
                    operation.TakeName(call);
                    return CreateOverflowTuple(call, operation, LlvmConstantInt.GetFalse(call.Context));
                }
            }
            return null;
        }
        private LlvmInstruction? FoldOverflowIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.overflow.visited");
            bool signed = kind == LlvmIntrinsicKind.SAddWithOverflow || kind == LlvmIntrinsicKind.SSubWithOverflow || kind == LlvmIntrinsicKind.SMulWithOverflow;
            LlvmOpcode opcode = kind == LlvmIntrinsicKind.SAddWithOverflow || kind == LlvmIntrinsicKind.UAddWithOverflow ? LlvmOpcode.Add
                : kind == LlvmIntrinsicKind.SSubWithOverflow || kind == LlvmIntrinsicKind.USubWithOverflow ? LlvmOpcode.Sub : LlvmOpcode.Mul;
            if (FoldIntrinsicWithOverflowCommon(call, opcode, signed) is { } common) return common;
            if (opcode == LlvmOpcode.Add && call.GetArgument(0) is LlvmBinaryOperator { Right: LlvmConstantInt first } add
                && (add.Opcode == LlvmOpcode.Add && (signed ? add.HasNoSignedWrap : add.HasNoUnsignedWrap)
                    || add.Opcode == LlvmOpcode.Or && add.IsDisjoint) && call.GetArgument(1) is LlvmConstantInt second)
            {
                bool overflows;
                LlvmAPInt sum = signed ? second.Value.SAddOverflow(first.Value, out overflows) : second.Value.UAddOverflow(first.Value, out overflows);
                if (!overflows) return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(kind, [add.Left, CallsInt(add.Type, sum)]));
            }
            if (kind == LlvmIntrinsicKind.SSubWithOverflow && call.GetArgument(1) is LlvmConstantInt constant && !constant.Value.IsMinSignedValue)
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(LlvmIntrinsicKind.SAddWithOverflow, [call.GetArgument(0), CallsInt(constant.Type, -constant.Value)]));
            return null;
        }
        private LlvmInstruction? FoldSaturatingIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.saturation.visited");
            bool signed = kind == LlvmIntrinsicKind.SAddSat || kind == LlvmIntrinsicKind.SSubSat;
            LlvmOpcode opcode = kind == LlvmIntrinsicKind.SAddSat || kind == LlvmIntrinsicKind.UAddSat ? LlvmOpcode.Add : LlvmOpcode.Sub;
            LlvmValue first = call.GetArgument(0), second = call.GetArgument(1);
            switch (ComputeOverflow(opcode, signed, first, second, call))
            {
                case LlvmOverflowResult.NeverOverflows: return new LlvmBinaryOperator(opcode, first, second) { HasNoSignedWrap = signed, HasNoUnsignedWrap = !signed };
                case LlvmOverflowResult.AlwaysOverflowsLow: return ReplaceInstUsesWith(call, CallsInt(call.Type, signed ? LlvmAPInt.GetSignedMinValue(call.Type.GetScalarSizeInBits()) : LlvmAPInt.GetZero(call.Type.GetScalarSizeInBits())));
                case LlvmOverflowResult.AlwaysOverflowsHigh: return ReplaceInstUsesWith(call, CallsInt(call.Type, signed ? LlvmAPInt.GetSignedMaxValue(call.Type.GetScalarSizeInBits()) : LlvmAPInt.GetAllOnes(call.Type.GetScalarSizeInBits())));
            }
            if (kind == LlvmIntrinsicKind.USubSat && first is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true, Left: LlvmConstant constant } sub
                && second is LlvmConstant other)
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(kind, [Builder.CreateIntrinsic(kind, [constant, other]), sub.Right]));
            if (kind == LlvmIntrinsicKind.SSubSat && second is LlvmConstantInt signedConstant && !signedConstant.Value.IsMinSignedValue)
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(LlvmIntrinsicKind.SAddSat, [first, CallsInt(second.Type, -signedConstant.Value)]));
            if (CallsIntrinsic(first, kind, out var inner) && second is LlvmConstantInt outerConstant && inner.GetArgument(1) is LlvmConstantInt innerConstant)
            {
                LlvmAPInt combined;
                if (!signed) combined = outerConstant.Value.UAddSat(innerConstant.Value);
                else if (outerConstant.IsNegative == innerConstant.IsNegative)
                {
                    combined = outerConstant.Value.SAddOverflow(innerConstant.Value, out bool overflow);
                    if (overflow) return null;
                }
                else return null;
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(kind, [inner.GetArgument(0), CallsInt(call.Type, combined)]));
            }
            return null;
        }
    }
}
