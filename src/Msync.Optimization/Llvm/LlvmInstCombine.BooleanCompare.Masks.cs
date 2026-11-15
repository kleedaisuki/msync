// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// MaskedICmpType, getMaskedICmpType, conjugateICmpMask, getMaskedTypeForICmpPair.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        [Flags]
        private enum BoolCmpMask : uint
        {
            AAllOnes = 1, ANotAllOnes = 2, BAllOnes = 4, BNotAllOnes = 8,
            AllZeros = 16, NotAllZeros = 32, AMixed = 64, ANotMixed = 128, BMixed = 256, BNotMixed = 512
        }
        private readonly record struct BoolCmpMaskedPair(LlvmValue A, LlvmValue B, LlvmValue C,
            LlvmValue D, LlvmValue E, LlvmCmpPredicate PredLeft, LlvmCmpPredicate PredRight,
            BoolCmpMask LeftMask, BoolCmpMask RightMask);

        private static BoolCmpMask GetMaskedICmpType(LlvmValue a, LlvmValue b, LlvmValue c, LlvmCmpPredicate predicate)
        {
            LlvmAPInt? ca = (a as LlvmConstantInt)?.Value, cb = (b as LlvmConstantInt)?.Value, cc = (c as LlvmConstantInt)?.Value;
            bool eq = predicate == LlvmCmpPredicate.ICmpEq;
            bool powerA = ca is { IsPowerOf2: true }, powerB = cb is { IsPowerOf2: true };
            BoolCmpMask mask = 0;
            if (cc is { IsZero: true })
            {
                mask |= eq ? BoolCmpMask.AllZeros | BoolCmpMask.AMixed | BoolCmpMask.BMixed :
                    BoolCmpMask.NotAllZeros | BoolCmpMask.ANotMixed | BoolCmpMask.BNotMixed;
                if (powerA) mask |= eq ? BoolCmpMask.ANotAllOnes | BoolCmpMask.ANotMixed : BoolCmpMask.AAllOnes | BoolCmpMask.AMixed;
                if (powerB) mask |= eq ? BoolCmpMask.BNotAllOnes | BoolCmpMask.BNotMixed : BoolCmpMask.BAllOnes | BoolCmpMask.BMixed;
                return mask;
            }
            if (a == c)
            {
                mask |= eq ? BoolCmpMask.AAllOnes | BoolCmpMask.AMixed : BoolCmpMask.ANotAllOnes | BoolCmpMask.ANotMixed;
                if (powerA) mask |= eq ? BoolCmpMask.NotAllZeros | BoolCmpMask.ANotMixed : BoolCmpMask.AllZeros | BoolCmpMask.AMixed;
            }
            else if (ca is { } ac && cc is { } c0 && (c0 & ~ac).IsZero) mask |= eq ? BoolCmpMask.AMixed : BoolCmpMask.ANotMixed;
            if (b == c)
            {
                mask |= eq ? BoolCmpMask.BAllOnes | BoolCmpMask.BMixed : BoolCmpMask.BNotAllOnes | BoolCmpMask.BNotMixed;
                if (powerB) mask |= eq ? BoolCmpMask.NotAllZeros | BoolCmpMask.BNotMixed : BoolCmpMask.AllZeros | BoolCmpMask.BMixed;
            }
            else if (cb is { } bc && cc is { } c1 && (c1 & ~bc).IsZero) mask |= eq ? BoolCmpMask.BMixed : BoolCmpMask.BNotMixed;
            return mask;
        }
        private static BoolCmpMask ConjugateICmpMask(BoolCmpMask mask)
        {
            const BoolCmpMask normal = BoolCmpMask.AAllOnes | BoolCmpMask.BAllOnes | BoolCmpMask.AllZeros | BoolCmpMask.AMixed | BoolCmpMask.BMixed;
            const BoolCmpMask inverted = BoolCmpMask.ANotAllOnes | BoolCmpMask.BNotAllOnes | BoolCmpMask.NotAllZeros | BoolCmpMask.ANotMixed | BoolCmpMask.BNotMixed;
            return (BoolCmpMask)(((uint)(mask & normal) << 1) | ((uint)(mask & inverted) >> 1));
        }
        private static (LlvmValue Left, LlvmValue Right) BoolCmpAndOperandsOrSelf(LlvmValue value)
            => value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } binary ? (binary.Left, binary.Right) :
                (value, LlvmConstantInt.Get(value.Context, LlvmAPInt.GetAllOnes(value.Type.GetScalarSizeInBits())));
        private bool BoolCmpDecompose(LlvmValue condition, out LlvmCmpPredicate predicate,
            out LlvmValue x, out LlvmValue y, out LlvmValue z)
        {
            if (LlvmCmpInstAnalysis.DecomposeBitTest(condition, true, true, true, Metrics) is { } bitTest)
            {
                predicate = bitTest.Pred; x = bitTest.X;
                y = BoolCmpC(x, bitTest.Mask); z = BoolCmpC(x, bitTest.C); return true;
            }
            predicate = default; x = y = z = null!; return false;
        }

        private BoolCmpMaskedPair? GetMaskedTypeForICmpPair(LlvmValue left, LlvmValue right)
        {
            LlvmValue? l1, l21, l22;
            LlvmValue l11, l12, l2;
            LlvmCmpPredicate predLeft, predRight;
            if (BoolCmpDecompose(left, out predLeft, out l11, out l12, out l2)) l1 = l21 = l22 = null;
            else
            {
                if (left is not LlvmICmpInstruction comparison || !comparison.Left.Type.IsIntOrIntVectorTy()) return null;
                predLeft = comparison.Predicate; l1 = comparison.Left; l2 = comparison.Right;
                (l11, l12) = BoolCmpAndOperandsOrSelf(l1); (l21, l22) = BoolCmpAndOperandsOrSelf(l2);
            }
            if (!BoolCmpEq(predLeft)) return null;
            bool Shared(LlvmValue candidate) => candidate == l11 || candidate == l12 || candidate == l21 || candidate == l22;
            LlvmValue a, d, e;
            if (BoolCmpDecompose(right, out predRight, out var r11, out var r12, out var r2))
            {
                if (Shared(r11)) { a = r11; d = r12; }
                else if (Shared(r12)) { a = r12; d = r11; }
                else return null;
                e = r2;
            }
            else
            {
                if (right is not LlvmICmpInstruction comparison || !comparison.Left.Type.IsIntOrIntVectorTy()) return null;
                predRight = comparison.Predicate;
                LlvmValue r1 = comparison.Left; r2 = comparison.Right;
                (r11, r12) = BoolCmpAndOperandsOrSelf(r1);
                LlvmValue? common = Shared(r11) ? r11 : Shared(r12) ? r12 : null;
                bool matched = common is not null && !BoolCmpOnes(common);
                if (matched) { a = common!; d = a == r11 ? r12 : r11; e = r2; }
                else
                {
                    (r11, r12) = BoolCmpAndOperandsOrSelf(r2);
                    if (Shared(r11)) { a = r11; d = r12; e = r1; }
                    else if (Shared(r12)) { a = r12; d = r11; e = r1; }
                    else return null;
                }
            }
            if (!BoolCmpEq(predRight)) return null;
            LlvmValue b, c;
            if (l11 == a) { b = l12; c = l2; }
            else if (l12 == a) { b = l11; c = l2; }
            else if (l21 == a) { b = l22!; c = l1!; }
            else { b = l21!; c = l1!; }
            return new(a, b, c, d, e, predLeft, predRight,
                GetMaskedICmpType(a, b, c, predLeft), GetMaskedICmpType(a, d, e, predRight));
        }
        private LlvmValue GetReconstructedMaskedICmp(bool logical, LlvmValue right, LlvmValue a,
            LlvmValue d, LlvmValue e, LlvmCmpPredicate predicate)
            => logical ? Builder.CreateICmp(predicate, Builder.CreateAnd(a, d), e) : right;
    }
}
