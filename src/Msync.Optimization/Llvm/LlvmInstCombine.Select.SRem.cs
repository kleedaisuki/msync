// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectWithSRem.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectWithSRem(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmICmpInstruction { Right: LlvmConstantInt constant } compare ||
                !LlvmValueTracking.IsSignBitCheck(compare.Predicate, constant.Value, out bool trueIfSigned)) return null;
            LlvmValue whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            if (!trueIfSigned) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            if (compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.SRem } rem || !ReferenceEquals(whenFalse, rem)) return null;
            LlvmValue? divisor = null;
            if (whenTrue is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add)
            {
                if (ReferenceEquals(add.Left, rem)) divisor = add.Right;
                else if (ReferenceEquals(add.Right, rem)) divisor = add.Left;
                if (!ReferenceEquals(divisor, rem.Right) || !LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(divisor!, true,
                    context: SQ.Context, tree: DT, metrics: Metrics, assumptions: AC, conditions: SQ.DC)) return null;
            }
            else if (SelectIsOne(whenTrue) && rem.Right is LlvmConstantInt two && two.Value == new LlvmAPInt(two.Value.BitWidth, 2, implicitTrunc: true)) divisor = two;
            else return null;
            LlvmValue minusOne = LlvmConstantInt.Get((LlvmIntegerType)rem.Type, LlvmAPInt.GetAllOnes(rem.Type.GetIntegerBitWidth()));
            LlvmValue mask = Builder.CreateAdd(divisor!, minusOne);
            return new LlvmBinaryOperator(LlvmOpcode.And, rem.Left, mask);
        }
    }
}
