// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldCmpLoadFromIndexedGlobal, scalar constant-load enumeration/state machines.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldCmpLoadFromIndexedGlobal(LlvmLoadInstruction load, LlvmGetElementPtrInstruction gep,
            LlvmCmpPredicate predicate, LlvmConstant rhs, LlvmInstruction context, LlvmConstant? andConstant = null)
        {
            if (load.IsVolatile || LlvmValueTracking.GetUnderlyingObject(gep, metrics: Metrics) is not LlvmGlobalVariable
                { IsConstant: true, HasDefinitiveInitializer: true } global) return null;
            var elementSize = DL.GetTypeStoreSize(load.Type);
            if (elementSize.IsScalable) return null;
            var expression = ICmpDecomposeLinearExpression(gep);
            if (expression.Index is null || expression.BasePointer != global || expression.Offset.BitWidth > 64) return null;
            var initializer = global.Initializer!;
            ulong globalSize = DL.GetTypeAllocSize(initializer.Type).GetFixedValue();
            var index = expression.Index;
            var stride = expression.Scale; var constantOffset = expression.Offset;
            if (!constantOffset.Ult(stride) || stride.GetLimitedValue() < elementSize.GetFixedValue()) return null;
            if (!(expression.Flags.HasNoUnsignedWrap || (expression.Flags.HasNoUnsignedSignedWrap ? !stride.IsNegative : stride.IsPowerOf2))) return null;
            ulong bytes = unchecked(globalSize - constantOffset.GetZExtValue());
            ulong step = stride.GetZExtValue();
            ulong count = bytes / step + (bytes % step == 0 ? 0UL : 1UL);
            if (count > CLOpts.MaxArraySize) return null;
            const int overdefined = -3, undefined = -2;
            int firstTrue = undefined, secondTrue = undefined, firstFalse = undefined, secondFalse = undefined;
            int trueEnd = undefined, falseEnd = undefined;
            ulong bits = 0;
            var offset = constantOffset;
            for (uint i = 0; i < count; ++i, offset += stride)
            {
                Count("icmp.indexed_global.elements");
                var element = LlvmConstantFolding.ConstantFoldLoadFromConst(initializer, load.Type, offset, DL, Metrics);
                if (element is null) return null;
                if (andConstant is not null)
                {
                    element = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.And, element, andConstant, DL, Metrics);
                    if (element is null) return null;
                }
                var result = LlvmConstantFolding.ConstantFoldCompareInstOperands(predicate, element, rhs, DL, metrics: Metrics);
                if (result is null) return null;
                int position = unchecked((int)i);
                if (result is LlvmUndefValue)
                {
                    if (trueEnd == position - 1) trueEnd = position;
                    if (falseEnd == position - 1) falseEnd = position;
                    continue;
                }
                if (result is not LlvmConstantInt boolean) return null;
                if (!boolean.IsZero)
                {
                    if (firstTrue == undefined) firstTrue = trueEnd = position;
                    else
                    {
                        secondTrue = secondTrue == undefined ? position : overdefined;
                        trueEnd = trueEnd == position - 1 ? position : overdefined;
                    }
                    if (i < 64) bits |= 1UL << position;
                }
                else if (firstFalse == undefined) firstFalse = falseEnd = position;
                else
                {
                    secondFalse = secondFalse == undefined ? position : overdefined;
                    falseEnd = falseEnd == position - 1 ? position : overdefined;
                }
                if (i >= 64 && secondTrue == overdefined && secondFalse == overdefined && trueEnd == overdefined && falseEnd == overdefined) return null;
            }
            LlvmValue MaskIndex(LlvmValue value)
            {
                uint zeros = stride.CountTrailingZeros();
                if (!expression.Flags.HasNoUnsignedWrap && !expression.Flags.HasNoUnsignedSignedWrap && zeros != 0)
                {
                    var mask = Builder.CreateBinary(LlvmOpcode.LShr, ICmpOnes(value), LlvmConstantInt.Get((LlvmIntegerType)value.Type, zeros));
                    value = Builder.CreateAnd(value, mask);
                }
                return value;
            }
            LlvmConstantInt IndexConstant(int value) => LlvmConstantInt.Get(index.Context,
                new LlvmAPInt(index.Type.GetIntegerBitWidth(), unchecked((ulong)(long)value), isSigned: true, implicitTrunc: true));
            if (secondTrue != overdefined)
            {
                index = MaskIndex(index);
                if (firstTrue == undefined) return ReplaceInstUsesWith(context, LlvmConstantInt.GetFalse(context.Context));
                if (secondTrue == undefined) return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, index, IndexConstant(firstTrue));
                Count("icmp.indexed_global.two_equalities");
                return new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, index, IndexConstant(firstTrue)),
                    Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, index, IndexConstant(secondTrue)));
            }
            if (secondFalse != overdefined)
            {
                index = MaskIndex(index);
                if (firstFalse == undefined) return ReplaceInstUsesWith(context, LlvmConstantInt.GetTrue(context.Context));
                if (secondFalse == undefined) return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, index, IndexConstant(firstFalse));
                Count("icmp.indexed_global.two_inequalities");
                return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, index, IndexConstant(firstFalse)),
                    Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, index, IndexConstant(secondFalse)));
            }
            if (trueEnd != overdefined)
            {
                index = MaskIndex(index);
                if (firstTrue != 0) index = Builder.CreateAdd(index, IndexConstant(-firstTrue));
                Count("icmp.indexed_global.true_range");
                return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUlt, index, IndexConstant(trueEnd - firstTrue + 1));
            }
            if (falseEnd != overdefined)
            {
                index = MaskIndex(index);
                if (firstFalse != 0) index = Builder.CreateAdd(index, IndexConstant(-firstFalse));
                Count("icmp.indexed_global.false_range");
                return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUgt, index, IndexConstant(falseEnd - firstFalse));
            }
            var integer = count <= index.Type.GetIntegerBitWidth() ? (LlvmIntegerType)index.Type
                : DL.GetSmallestLegalIntType(index.Context, (uint)count);
            if (integer is null) return null;
            index = MaskIndex(index);
            var cast = Builder.CreateZExtOrTrunc(index, integer);
            var shifted = Builder.CreateBinary(LlvmOpcode.LShr, LlvmConstantInt.Get(integer, bits, implicitTrunc: true), cast);
            var bit = Builder.CreateAnd(LlvmConstantInt.Get(integer, 1), shifted);
            Count("icmp.indexed_global.bitvector");
            return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, bit, LlvmConstantInt.Get(integer, 0));
        }
    }
}
