// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectBinOpIdentity.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectBinOpIdentity(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmCmpInstruction compare || compare.Right is not LlvmConstant constant) return null;
            LlvmValue x = compare.Left;
            bool equality;
            if (compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) equality = compare.Predicate == LlvmCmpPredicate.ICmpEq;
            else if (compare.Predicate == LlvmCmpPredicate.FCmpOeq) equality = true;
            else if (compare.Predicate == LlvmCmpPredicate.FCmpUne) equality = false;
            else return null;
            if ((equality ? select.TrueValue : select.FalseValue) is not LlvmBinaryOperator binary) return null;
            bool absorbing = false; LlvmValue y;
            bool MatchAbs(LlvmValue value) => value is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.FAbs && ReferenceEquals(call.GetArgument(0), x);
            if (binary.IsCommutative)
            {
                if (binary.Opcode == LlvmOpcode.FMul && SelectIsAnyZeroFP(constant) &&
                    ((MatchAbs(binary.Left) && FmulByZeroIsZero(binary.Right, binary.FastMathFlags, select)) ||
                    (MatchAbs(binary.Right) && FmulByZeroIsZero(binary.Left, binary.FastMathFlags, select))))
                { absorbing = true; y = constant; }
                else if (ReferenceEquals(binary.Right, x)) y = binary.Left;
                else if (ReferenceEquals(binary.Left, x)) y = binary.Right;
                else return null;
            }
            else { if (!ReferenceEquals(binary.Right, x)) return null; y = binary.Left; }
            LlvmValue folded;
            if (absorbing) folded = constant;
            else
            {
                LlvmConstant? identity = LlvmConstantFolding.BinOpIdentity(binary.Opcode, binary.Type, true);
                if (!ReferenceEquals(identity, constant))
                {
                    if (identity is null || compare is not LlvmFCmpInstruction || !SelectIsAnyZeroFP(identity) || !SelectIsAnyZeroFP(constant)) return null;
                }
                if (binary.Type.IsFloatingPointTy() && (binary.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) == 0 &&
                    !LlvmKnownFPClasses.CannotBeNegativeZero(y, select, DT, Metrics, assumptions: AC, conditions: SQ.DC)) return null;
                folded = y;
            }
            return ReplaceOperand(select, equality ? 1 : 2, folded);
        }
    }
}
