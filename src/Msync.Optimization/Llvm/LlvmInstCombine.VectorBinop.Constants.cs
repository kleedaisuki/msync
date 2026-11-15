// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// unshuffleConstant/constantFoldBinOpWithSplat; InstCombiner.h getSafeVectorConstantForBinop.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmConstant? UnshuffleConstant(IReadOnlyList<int> mask, LlvmConstant constant, LlvmVectorType newType)
        {
            Count("vector_binop.unshuffle_queries");
            if (newType is LlvmScalableVectorType)
            {
                var splat = constant.GetSplatValue();
                return splat is null ? null : LlvmConstantVector.GetSplat(newType.ElementCount, splat);
            }
            uint newCount = ((LlvmFixedVectorType)newType).NumElements;
            uint count = ((LlvmFixedVectorType)constant.Type).NumElements;
            if (newCount > count) return null;
            var elements = Enumerable.Repeat<LlvmConstant>(LlvmPoisonValue.Get(((LlvmVectorType)constant.Type).ElementType), (int)newCount).ToArray();
            for (uint index = 0; index < count; ++index)
            {
                var element = constant.GetAggregateElement(index);
                int destination = mask[(int)index];
                if (destination < 0 || destination >= newCount) continue;
                var previous = elements[destination];
                if (element is null || previous is not LlvmPoisonValue && previous != element || index >= newCount) return null;
                elements[destination] = element;
            }
            return LlvmConstantVector.Get(elements);
        }

        private LlvmConstant? ConstantFoldVectorBinopWithSplat(LlvmOpcode opcode, LlvmConstant vector,
            LlvmConstant splat, bool splatLeft)
        {
            var expanded = LlvmConstantVector.GetSplat(((LlvmVectorType)vector.Type).ElementCount, splat);
            return LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, splatLeft ? expanded : vector, splatLeft ? vector : expanded, DL, Metrics);
        }

        private LlvmConstant GetSafeVectorConstantForBinop(LlvmOpcode opcode, LlvmConstant constant, bool constantRight)
        {
            var type = (LlvmFixedVectorType)constant.Type;
            var elementType = type.ElementType;
            var safe = LlvmConstantExpr.GetBinOpIdentity(opcode, elementType, constantRight);
            if (safe is null)
            {
                if (constantRight)
                    safe = opcode switch
                    {
                        LlvmOpcode.SRem or LlvmOpcode.URem => LlvmConstantInt.Get((LlvmIntegerType)elementType, 1),
                        LlvmOpcode.FRem => LlvmConstantFP.Get(elementType, 1.0),
                        _ => throw new UnreachableException("Only remainder operations have no right identity constant.")
                    };
                else
                    safe = opcode switch
                    {
                        LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr or LlvmOpcode.SDiv or LlvmOpcode.UDiv or
                        LlvmOpcode.SRem or LlvmOpcode.URem or LlvmOpcode.Sub or LlvmOpcode.FSub or LlvmOpcode.FDiv or
                        LlvmOpcode.FRem => LlvmConstant.GetNullValue(elementType),
                        _ => throw new UnreachableException("Expected an identity constant for the operation.")
                    };
            }
            var elements = new LlvmConstant[type.NumElements];
            for (uint index = 0; index < type.NumElements; ++index)
            {
                var element = constant.GetAggregateElement(index)!;
                elements[index] = element is LlvmUndefValue ? safe : element;
            }
            return LlvmConstantVector.Get(elements);
        }
    }
}
