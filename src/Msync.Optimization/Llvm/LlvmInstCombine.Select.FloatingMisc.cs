// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectToCopysign/fmulByZeroIsZero/matchFMulByZeroIfResultEqZero,
// foldSelectIntoAddConstant/isSelectZeroSignInsignificant.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectToCopysign(LlvmSelectInstruction select)
        {
            if (select.TrueValue is not LlvmConstantFP trueConstant || select.FalseValue is not LlvmConstantFP falseConstant ||
                !trueConstant.Value.ClearSign().BitwiseIsEqual(falseConstant.Value.ClearSign()) ||
                select.Condition is not LlvmICmpInstruction { Left: LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast, Right: LlvmConstantInt constant } compare ||
                !compare.HasOneUse || !LlvmValueTracking.IsSignBitCheck(compare.Predicate, constant.Value, out bool trueIfSignSet) ||
                !ReferenceEquals(cast.Operand.Type, select.Type)) return null;
            LlvmValue sign = cast.Operand;
            if (trueIfSignSet ^ trueConstant.Value.IsNegative) sign = Builder.CreateFNeg(sign);
            LlvmValue magnitude = LlvmConstantFP.Get(select.Type, trueConstant.Value.ClearSign());
            return LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CopySign, [magnitude, sign], F.Parent!, Metrics);
        }
        private bool FmulByZeroIsZero(LlvmValue value, LlvmFastMathFlags flags, LlvmInstruction context)
        {
            LlvmKnownFPClass known = LlvmKnownFPClasses.Compute(value, flags, LlvmFPClassTest.Negative, context, DT, Metrics, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: SQ.DC);
            return known.IsKnownNeverNaN() && known.IsKnownNeverInfinity() &&
                ((flags & LlvmFastMathFlags.NoSignedZeros) != 0 || known.IsKnownNever(LlvmFPClassTest.Negative));
        }
        private bool MatchFMulByZeroIfResultEqZero(LlvmValue compareLeft, LlvmValue compareRight, LlvmValue whenTrue,
            LlvmValue whenFalse, LlvmInstruction context, bool selectNoSignedZeros)
        {
            if (!SelectIsPositiveZeroFP(compareRight) || whenTrue is not LlvmBinaryOperator { Opcode: LlvmOpcode.FMul } multiply) return false;
            LlvmValue other;
            if (ReferenceEquals(multiply.Left, compareLeft)) other = multiply.Right;
            else if (ReferenceEquals(multiply.Right, compareLeft)) other = multiply.Left;
            else return false;
            LlvmFastMathFlags flags = multiply.FastMathFlags & ~LlvmFastMathFlags.NoSignedZeros;
            if (selectNoSignedZeros) flags |= LlvmFastMathFlags.NoSignedZeros;
            return FmulByZeroIsZero(other, flags, context);
        }
        private LlvmValue? FoldSelectIntoAddConstant(LlvmSelectInstruction select)
        {
            if (!select.Type.IsFloatingPointTy() ||
                (select.FastMathFlags & (LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.NoNaNs)) != (LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.NoNaNs) ||
                select.Condition is not LlvmFCmpInstruction compare || !compare.HasOneUse) return null;
            LlvmValue? TryFold(LlvmValue addValue, LlvmValue constantValue, bool swapped)
            {
                if (compare.Predicate is not (LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle or
                    LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle) ||
                    !SelectIsAnyZeroFP(compare.Right) || constantValue is not LlvmConstant constant ||
                    addValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.FAdd } add || !add.HasOneUse ||
                    !ReferenceEquals(add.Left, compare.Left) || !ReferenceEquals(add.Right, constant)) return null;
                LlvmValue selected = Builder.CreateSelect(select.Condition, swapped ? compare.Right : compare.Left,
                    swapped ? compare.Left : compare.Right);
                selected.TakeName(select);
                LlvmValue newAdd = Builder.CreateBinOp(LlvmOpcode.FAdd, selected, constant);
                newAdd.TakeName(add);
                LlvmFastMathFlags flags = (SelectRewriteFlags & select.FastMathFlags & add.FastMathFlags) |
                    (SelectValueFlags & (select.FastMathFlags | add.FastMathFlags));
                if (newAdd is ILlvmFastMathInstruction fpAdd) fpAdd.FastMathFlags = flags;
                if (selected is ILlvmFastMathInstruction fpSelect) fpSelect.FastMathFlags = flags;
                return newAdd;
            }
            return TryFold(select.TrueValue, select.FalseValue, false) ?? TryFold(select.FalseValue, select.TrueValue, true);
        }
        private bool IsSelectZeroSignInsignificant(LlvmSelectInstruction select)
        {
            const uint maxUses = 16;
            uint uses = 0;
            HashSet<LlvmInstruction> visited = [];
            List<LlvmInstruction> worklist = [select];
            while (worklist.Count != 0)
            {
                LlvmInstruction instruction = worklist[^1]; worklist.RemoveAt(worklist.Count - 1);
                foreach (LlvmUse use in instruction.Uses)
                {
                    if (++uses > maxUses) return false;
                    LlvmInstruction user = (LlvmInstruction)use.User;
                    if (ReferenceEquals(user, select) || LlvmValueTracking.CanIgnoreSignBitOfZero(use, Metrics)) continue;
                    if (user is LlvmPhiInstruction or LlvmSelectInstruction)
                    { if (visited.Add(user)) worklist.Add(user); continue; }
                    return false;
                }
            }
            return true;
        }
    }
}
