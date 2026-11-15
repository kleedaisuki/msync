// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectIntoOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool IsSelect01(LlvmAPInt first, LlvmAPInt second) =>
            (first.IsZero || second.IsZero) && (first.IsOne || first.IsAllOnes || second.IsOne || second.IsAllOnes);
        private LlvmInstruction? FoldSelectIntoOp(LlvmSelectInstruction select, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            LlvmInstruction? TryFold(LlvmValue trueValue, LlvmValue falseValue, bool swapped)
            {
                if (trueValue is not LlvmBinaryOperator binary || !binary.HasOneUse || falseValue is LlvmConstant) return null;
                uint foldable = GetSelectFoldableOperands(binary);
                int operandToFold = (foldable & 1) != 0 && ReferenceEquals(falseValue, binary.Left) ? 1 :
                    (foldable & 2) != 0 && ReferenceEquals(falseValue, binary.Right) ? 2 : 0;
                if (operandToFold == 0) return null;
                LlvmFastMathFlags flags = select.Type.IsFPOrFPVectorTy() ? select.FastMathFlags : LlvmFastMathFlags.None;
                LlvmConstant identity = LlvmConstantFolding.BinOpIdentity(binary.Opcode, binary.Type, true,
                    (flags & LlvmFastMathFlags.NoSignedZeros) != 0)!;
                LlvmValue other = binary.GetOperand(2 - operandToFold)!;
                if (other is LlvmConstant && (other is not LlvmConstantInt integer || identity is not LlvmConstantInt identityInteger ||
                    !IsSelect01(identityInteger.Value, integer.Value))) return null;
                if (select.Type.IsFPOrFPVectorTy() && !LlvmKnownFPClasses.Compute(falseValue, flags, LlvmFPClassTest.NaN,
                    select, DT, Metrics, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: SQ.DC).IsKnownNeverNaN()) return null;
                LlvmValue newSelect = Builder.CreateSelect(select.Condition, swapped ? identity : other, swapped ? other : identity);
                if (select.Type.IsFPOrFPVectorTy() && newSelect is ILlvmFastMathInstruction floating)
                {
                    LlvmFastMathFlags newFlags = flags;
                    bool inferFinite = binary.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul;
                    bool noInfs = (binary.FastMathFlags & LlvmFastMathFlags.NoInfs) != 0 ||
                        (inferFinite && (flags & (LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoNaNs)) == (LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoNaNs));
                    floating.FastMathFlags = noInfs ? newFlags | LlvmFastMathFlags.NoInfs : newFlags & ~LlvmFastMathFlags.NoInfs;
                }
                newSelect.TakeName(binary);
                LlvmBinaryOperator result = new(binary.Opcode, falseValue, newSelect);
                result.CopyIRFlags(binary);
                if (select.Type.IsFPOrFPVectorTy())
                {
                    const LlvmFastMathFlags poisonAndZero = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoSignedZeros;
                    result.FastMathFlags = (result.FastMathFlags & ~poisonAndZero) | (result.FastMathFlags & flags & poisonAndZero);
                }
                Count("select.into_op.rewrites");
                return result;
            }
            return TryFold(whenTrue, whenFalse, false) ?? TryFold(whenFalse, whenTrue, true);
        }
    }
}
