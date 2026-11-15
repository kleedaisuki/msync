// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp 1565–1601.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? DynCastNegVal(LlvmValue value)
        {
            Count("addsub.neg_value.queries");
            if (SubNeg(value, out var operand)) return operand;
            if (value is LlvmConstantInt integer) return LlvmConstantExpr.GetNeg(integer);
            if (value is LlvmConstantDataVector data && data.Type.GetScalarType().IsIntegerTy()) return LlvmConstantExpr.GetNeg(data);
            if (value is LlvmConstantVector vector)
            {
                for (uint index = 0; index < ((LlvmFixedVectorType)vector.Type).NumElements; index++)
                {
                    var element = vector.GetAggregateElement(index);
                    if (element is not (LlvmUndefValue or LlvmConstantInt)) return null;
                }
                return LlvmConstantExpr.GetNeg(vector);
            }
            return value is LlvmConstant constant && value.Type.IsVectorTy() && value.Type.GetScalarType().IsIntegerTy() &&
                constant.GetSplatValue() is not null ? LlvmConstantExpr.GetNeg(constant) : null;
        }
    }
}
