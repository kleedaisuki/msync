// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// moveAddAfterMinMax/matchSAddSubSat/foldClampRangeOfTwo/reassociation/factorization.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldMinimumOverZeroCount(LlvmValue first, LlvmValue second, LlvmIntrinsicKind kind)
        {
            if (!CallsIntrinsic(first, kind, out var zeros) || !first.HasOneUse) return null;
            if (CallsIntrinsic(second, kind, out var otherZeros) && second.HasOneUse)
                return Builder.CreateIntrinsic(kind, [Builder.CreateOr(zeros.GetArgument(0), otherZeros.GetArgument(0)),
                    Builder.CreateOr(zeros.GetArgument(1), otherZeros.GetArgument(1))]);
            uint width = second.Type.GetScalarSizeInBits();
            if (second is not LlvmConstantInt constant || !constant.Value.Ult(new(width, width))) return null;
            LlvmAPInt mask = kind == LlvmIntrinsicKind.CTTZ ? LlvmAPInt.GetOneBitSet(width, (uint)constant.GetZExtValue())
                : LlvmAPInt.GetSignedMinValue(width).LShr((uint)constant.GetZExtValue());
            return Builder.CreateIntrinsic(kind, [Builder.CreateOr(zeros.GetArgument(0), CallsInt(second.Type, mask)), LlvmConstantInt.GetTrue(second.Context)]);
        }
        private LlvmInstruction? MoveNotAfterMinMax(LlvmCallInstruction call, LlvmIntrinsicKind kind, LlvmValue first, LlvmValue second)
        {
            if (!first.HasOneUse || !CallsNot(first, out var value) || IsFreeToInvert(value, value.HasOneUse)) return null;
            if (GetFreelyInverted(second, second.HasOneUse, Builder) is not { } inverse) return null;
            return new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateIntrinsic(LlvmIntrinsic.GetInverseMinMax(kind), [value, inverse]),
                CallsInt(call.Type, LlvmAPInt.GetAllOnes(call.Type.GetScalarSizeInBits())));
        }
        private LlvmInstruction? MoveAddAfterMinMax(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (call.GetArgument(0) is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt leftConstant } add
                || call.GetArgument(1) is not LlvmConstantInt rightConstant) return null;
            bool signed = kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.SMax;
            if (signed ? !add.HasNoSignedWrap : !add.HasNoUnsignedWrap) return null;
            bool overflow;
            LlvmAPInt difference = signed ? rightConstant.Value.SSubOverflow(leftConstant.Value, out overflow)
                : rightConstant.Value.USubOverflow(leftConstant.Value, out overflow);
            Debug.Assert(!overflow);
            return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateIntrinsic(kind, [add.Left, CallsInt(call.Type, difference)]), add.Right)
                { HasNoSignedWrap = signed, HasNoUnsignedWrap = !signed };
        }
        private LlvmInstruction? MatchSAddSubSat(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (call.GetArgument(1) is not LlvmConstantInt outerConstant) return null;
            LlvmIntrinsicKind inverse = LlvmIntrinsic.GetInverseMinMax(kind);
            if (kind != LlvmIntrinsicKind.SMin && kind != LlvmIntrinsicKind.SMax
                || !CallsIntrinsic(call.GetArgument(0), inverse, out var inner) || inner.GetArgument(1) is not LlvmConstantInt innerConstant
                || inner.GetArgument(0) is not LlvmBinaryOperator operation) return null;
            LlvmAPInt minimum = kind == LlvmIntrinsicKind.SMin ? innerConstant.Value : outerConstant.Value;
            LlvmAPInt maximum = kind == LlvmIntrinsicKind.SMin ? outerConstant.Value : innerConstant.Value;
            LlvmAPInt incremented = maximum + new LlvmAPInt(maximum.BitWidth, 1);
            if (!incremented.IsPowerOf2 || -minimum != incremented) return null;
            uint width = incremented.CountTrailingZeros() + 1;
            if (!ShouldChangeType(call.Type.GetScalarSizeInBits(), width) || !inner.HasOneUse || !operation.HasOneUse) return null;
            LlvmIntrinsicKind saturation;
            if (operation.Opcode == LlvmOpcode.Add) saturation = LlvmIntrinsicKind.SAddSat;
            else if (operation.Opcode == LlvmOpcode.Sub) saturation = LlvmIntrinsicKind.SSubSat;
            else return null;
            var query = Query(operation);
            uint Significant(LlvmValue value) => value.Type.GetScalarSizeInBits() - LlvmKnownBitsAnalysis.ComputeNumSignBits(value,
                context: operation, tree: query.Tree, metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo,
                assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC) + 1;
            if (Significant(operation.Left) > width || Significant(operation.Right) > width) return null;
            LlvmIntegerType type = LlvmIntegerType.Get(call.Context, width);
            LlvmValue saturated = Builder.CreateIntrinsic(saturation, [Builder.CreateTrunc(operation.Left, type), Builder.CreateTrunc(operation.Right, type)]);
            return new LlvmCastInstruction(LlvmOpcode.SExt, saturated, call.Type);
        }
        private LlvmInstruction? FoldClampRangeOfTwo(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (call.GetArgument(1) is not LlvmConstantInt outer || !call.GetArgument(0).HasOneUse
                || !CallsIntrinsic(call.GetArgument(0), LlvmIntrinsic.GetInverseMinMax(kind), out var inner)
                || inner.GetArgument(1) is not LlvmConstantInt inside) return null;
            bool maximum = kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMax;
            if (maximum ? inside.Value != outer.Value + new LlvmAPInt(outer.BitWidth, 1)
                : outer.Value != inside.Value + new LlvmAPInt(outer.BitWidth, 1)) return null;
            LlvmValue compare = Builder.CreateICmp(LlvmIntrinsic.GetMinMaxPredicate(kind), inner.GetArgument(0), outer);
            return new LlvmSelectInstruction(compare, inside, outer);
        }
        private LlvmValue? ReassociateMinMaxWithConstants(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (call.GetArgument(0) is not LlvmCallBase inner || inner.CalledFunction?.IntrinsicDescriptor is not { } descriptor
                || !LlvmIntrinsic.IsMinMax(descriptor.Kind) || inner.GetArgument(1) is not LlvmConstantInt first
                || call.GetArgument(1) is not LlvmConstantInt second) return null;
            LlvmIntrinsicKind innerKind = descriptor.Kind;
            if (innerKind != kind && !((kind == LlvmIntrinsicKind.UMax && innerKind == LlvmIntrinsicKind.SMax
                || kind == LlvmIntrinsicKind.SMin && innerKind == LlvmIntrinsicKind.UMin) && !first.IsNegative && !second.IsNegative)) return null;
            LlvmValue condition = Builder.CreateICmp(LlvmIntrinsic.GetMinMaxPredicate(kind), first, second);
            return Builder.CreateIntrinsic(innerKind, [inner.GetArgument(0), Builder.CreateSelect(condition, first, second)]);
        }
        private LlvmInstruction? ReassociateMinMaxWithConstantInOperand(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            for (int index = 0; index < 2; ++index)
            {
                if (!CallsIntrinsic(call.GetArgument(index), kind, out var inner) || !inner.HasOneUse
                    || inner.GetArgument(1) is not LlvmConstant constant || inner.GetArgument(0) is LlvmConstant
                    || call.GetArgument(1 - index) is LlvmConstant) continue;
                LlvmValue newInner = Builder.CreateIntrinsic(kind, [inner.GetArgument(0), call.GetArgument(1 - index)]);
                newInner.TakeName(inner);
                return CallsNewIntrinsic(call, kind, [newInner, constant]);
            }
            return null;
        }
        private LlvmInstruction? FactorizeMinMaxTree(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (!CallsIntrinsic(call.GetArgument(0), kind, out var left) || !CallsIntrinsic(call.GetArgument(1), kind, out var right)
                || !left.HasOneUse && !right.HasOneUse) return null;
            LlvmValue a = left.GetArgument(0), b = left.GetArgument(1), c = right.GetArgument(0), d = right.GetArgument(1);
            LlvmValue? operand = null, third = null;
            if (left.HasOneUse)
            {
                if (ReferenceEquals(d, a) || ReferenceEquals(c, a)) { operand = right; third = b; }
                else if (ReferenceEquals(d, b) || ReferenceEquals(c, b)) { operand = right; third = a; }
            }
            else
            {
                if (ReferenceEquals(d, a) || ReferenceEquals(d, b)) { operand = left; third = c; }
                else if (ReferenceEquals(c, a) || ReferenceEquals(c, b)) { operand = left; third = d; }
            }
            return operand is not null && third is not null ? CallsNewIntrinsic(call, kind, [operand, third]) : null;
        }
        private LlvmValue? FoldIntrinsicUsingDistributiveLaws(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (call.GetArgument(0) is not LlvmBinaryOperator first || call.GetArgument(1) is not LlvmBinaryOperator second
                || first.Opcode is not (LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Shl or LlvmOpcode.Mul)
                || first.Opcode != second.Opcode || !first.HasOneUse || !second.HasOneUse) return null;
            bool nuw = first.HasNoUnsignedWrap && second.HasNoUnsignedWrap;
            bool nsw = first.HasNoSignedWrap && second.HasNoSignedWrap;
            LlvmValue a = first.Left, b = first.Right, c = second.Left, d = second.Right;
            if (first.IsCommutative && !ReferenceEquals(a, c) && !ReferenceEquals(b, d))
            {
                if (ReferenceEquals(a, d) || ReferenceEquals(b, c)) (c, d) = (d, c);
                else return null;
            }
            bool signed = kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.SMax;
            bool leftDistributes = signed ? nsw && first.Opcode == LlvmOpcode.Add : nuw && first.Opcode is LlvmOpcode.Add or LlvmOpcode.Shl;
            if (ReferenceEquals(a, c) && leftDistributes)
                return Builder.CreateBinary(first.Opcode, a, Builder.CreateIntrinsic(kind, [b, d]), nuw: nuw, nsw: nsw);
            bool rightDistributes = first.IsCommutative || first.Opcode == LlvmOpcode.Shl ? leftDistributes
                : first.Opcode == LlvmOpcode.Sub && (signed ? nsw : nuw);
            if (ReferenceEquals(b, d) && rightDistributes)
                return Builder.CreateBinary(first.Opcode, Builder.CreateIntrinsic(kind, [a, c]), b, nuw: nuw, nsw: nsw);
            return null;
        }
    }
}
