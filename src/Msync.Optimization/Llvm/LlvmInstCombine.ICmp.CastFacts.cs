// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 6665–6888.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpWithTrunc(LlvmICmpInstruction compare)
        {
            Count("icmp.cast.trunc.queries");
            if (compare.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true } trunc ||
                ICmpAPInt(compare.Right) is not { } constant) return null;
            LlvmValue input = trunc.Operand;
            if (LlvmCmpInstAnalysis.DecomposeBitTestICmp(compare.Left, compare.Right, compare.Predicate,
                lookThroughTrunc: true, allowNonZeroC: true, metrics: Metrics) is { } test)
            {
                Count("icmp.cast.trunc.bit-test");
                return IccCompare(test.Pred, Builder.CreateAnd(test.X, LlvmConstantInt.Get(test.X.Type, test.Mask)), LlvmConstantInt.Get(test.X.Type, test.C));
            }
            uint src = input.Type.GetScalarSizeInBits();
            if (input is LlvmCallInstruction call && (call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CTTZ || call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CTLZ))
            {
                uint max = src;
                if (ICmpConstantMatches(call.GetArgument(1), value => value.IsOne)) max--;
                if (new LlvmAPInt(32, max).GetActiveBits() <= trunc.Type.GetScalarSizeInBits())
                    return FoldICmpIntrinsicWithConstant(compare, call, constant.Value.ZExt(src));
            }
            return null;
        }

        private LlvmInstruction? FoldICmpWithZextOrSext(LlvmICmpInstruction compare)
        {
            Count("icmp.cast.extension.queries");
            if (compare.Left is not LlvmCastInstruction left || left.Opcode is not (LlvmOpcode.ZExt or LlvmOpcode.SExt)) return null;
            LlvmValue x = left.Operand;
            bool signedExt = left.Opcode == LlvmOpcode.SExt;
            if (compare.Right is LlvmCastInstruction right && right.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt)
            {
                LlvmValue y = right.Operand;
                bool zext0 = left.Opcode == LlvmOpcode.ZExt, zext1 = right.Opcode == LlvmOpcode.ZExt;
                if (zext0 != zext1)
                {
                    if (ICmpIsEquality(compare.Predicate) && x.Type.IsIntOrIntVectorTy(1) && y.Type.IsIntOrIntVectorTy(1))
                        return IccCompare(compare.Predicate, Builder.CreateOr(x, y), LlvmConstant.GetNullValue(x.Type));
                    if (zext0 && left.IsNonNegative || zext1 && right.IsNonNegative) signedExt = true;
                    else return null;
                }
                if (x.Type != y.Type)
                {
                    if (!left.HasOneUse && !right.HasOneUse) return null;
                    LlvmOpcode opcode = signedExt ? LlvmOpcode.SExt : LlvmOpcode.ZExt;
                    if (x.Type.GetScalarSizeInBits() < y.Type.GetScalarSizeInBits()) x = Builder.CreateCast(opcode, x, y.Type);
                    else if (y.Type.GetScalarSizeInBits() < x.Type.GetScalarSizeInBits()) y = Builder.CreateCast(opcode, y, x.Type);
                    else return null;
                }
                Count("icmp.cast.extension.paired");
                return IccCompare(ICmpIsEquality(compare.Predicate) || ICmpIsSigned(compare.Predicate) && signedExt
                    ? compare.Predicate : ICmpUnsignedPredicate(compare.Predicate), x, y);
            }
            if (compare.Right is not LlvmConstant constant) return null;
            if (GetLosslessInvCast(constant, x.Type, left.Opcode, DL) is { } narrowed)
                return IccCompare(ICmpIsEquality(compare.Predicate) || signedExt && ICmpIsSigned(compare.Predicate)
                    ? compare.Predicate : ICmpUnsignedPredicate(compare.Predicate), x, narrowed);
            if (ICmpIsSigned(compare.Predicate) || !signedExt || constant is not LlvmConstantInt) return null;
            Count("icmp.cast.extension.unsigned-sign-test");
            if (compare.Predicate == LlvmCmpPredicate.ICmpUlt) return IccCompare(LlvmCmpPredicate.ICmpSgt, x, LlvmConstant.GetAllOnesValue(x.Type));
            Debug.Assert(compare.Predicate == LlvmCmpPredicate.ICmpUgt);
            return IccCompare(LlvmCmpPredicate.ICmpSlt, x, LlvmConstant.GetNullValue(x.Type));
        }

        private LlvmInstruction? FoldICmpWithCastOp(LlvmICmpInstruction compare)
        {
            Count("icmp.cast.queries");
            LlvmValue? a = SimplifyIntToPtrRoundTripCast(compare.Left), b = SimplifyIntToPtrRoundTripCast(compare.Right);
            if (a is not null || b is not null) return IccCompare(compare.Predicate, a ?? compare.Left, b ?? compare.Right);
            if (compare.Left is not LlvmCastInstruction cast || compare.Right is not (LlvmConstant or LlvmCastInstruction)) return null;
            LlvmValue input = cast.Operand, rhs = compare.Right;
            LlvmType src = input.Type, dst = cast.Type;
            if (cast.Opcode is LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr)
            {
                bool ptrToInt = cast.Opcode == LlvmOpcode.PtrToInt;
                LlvmValue? other = null;
                if (rhs is LlvmCastInstruction right && right.Opcode is LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr)
                { other = right.Operand; ptrToInt |= right.Opcode == LlvmOpcode.PtrToInt; }
                else if (rhs is LlvmConstantExpr expr && expr.Opcode is LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr)
                { other = expr.GetConstantOperand(0); ptrToInt |= expr.Opcode == LlvmOpcode.PtrToInt; }
                else if (rhs is LlvmConstant constant) other = LlvmConstantExpr.GetIntToPtr(constant, src);
                if ((!ptrToInt || Compatible(src, dst)) && other is not null && other.Type == src)
                    return IccCompare(compare.Predicate, input, other);
            }
            if (cast.Opcode == LlvmOpcode.IntToPtr && Compatible(dst, src))
            {
                LlvmValue? other = rhs is LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr } right && right.Operand.Type == src ? right.Operand :
                    rhs is LlvmConstant constant ? LlvmConstantFolding.ConstantFoldConstant(LlvmConstantExpr.GetPtrToInt(constant, src), DL, Metrics) : null;
                if (other is not null) return IccCompare(compare.Predicate, input, other);
            }
            return FoldICmpWithTrunc(compare) ?? FoldICmpWithZextOrSext(compare);
            bool Compatible(LlvmType pointer, LlvmType integer)
            {
                uint index = DL.GetAddressSizeInBits(((LlvmPointerType)pointer.GetScalarType()).AddressSpace);
                return integer.GetScalarSizeInBits() == index && index == DL.GetPointerTypeSizeInBits(pointer);
            }
        }
    }
}
