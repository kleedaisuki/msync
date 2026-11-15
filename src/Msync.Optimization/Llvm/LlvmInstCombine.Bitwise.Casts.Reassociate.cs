// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// reassociateForUses / canonicalizeLogicFirst / foldBinOpOfDisplacedShifts / foldBitwiseLogicWithIntrinsics.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? ReassociateForUses(LlvmBinaryOperator instruction)
        {
            LlvmValue candidate = instruction.Left, z = instruction.Right;
            bool Match(LlvmValue value, LlvmValue other) => value is LlvmBinaryOperator binary && binary.Opcode == instruction.Opcode && binary.HasOneUse && other.HasOneUse;
            if (!Match(candidate, z)) (candidate, z) = (z, candidate);
            if (!Match(candidate, z)) return null;
            var inner = (LlvmBinaryOperator)candidate;
            LlvmValue x = inner.Left, y = inner.Right;
            if (x is LlvmConstant || y is LlvmConstant || z is LlvmConstant) return null;
            if (!x.HasOneUse)
                return LogicResult(new LlvmBinaryOperator(instruction.Opcode, Builder.CreateBinOp(instruction.Opcode, y, z), x), "reassociate_uses_yz");
            if (!y.HasOneUse)
                return LogicResult(new LlvmBinaryOperator(instruction.Opcode, Builder.CreateBinOp(instruction.Opcode, x, z), y), "reassociate_uses_xz");
            return null;
        }
        private LlvmInstruction? CanonicalizeLogicFirst(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt addConstant } add || instruction.Right is not LlvmConstantInt constant) return null;
            uint width = instruction.Type.GetScalarSizeInBits(), affected = width - addConstant.Value.CountTrailingZeros();
            bool fits = instruction.Opcode switch
            {
                LlvmOpcode.And => constant.Value.CountLeadingOnes() >= affected,
                LlvmOpcode.Or or LlvmOpcode.Xor => constant.Value.CountLeadingZeros() >= affected,
                _ => throw new UnreachableException("CanonicalizeLogicFirst expects and/or/xor")
            };
            if (!fits) return null;
            LlvmValue first = Builder.CreateBinOp(instruction.Opcode, add.Left, constant);
            return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Add, first, addConstant) { HasNoUnsignedWrap = add.HasNoUnsignedWrap, HasNoSignedWrap = add.HasNoSignedWrap }, "canonicalize_logic_first");
        }
        private LlvmInstruction? FoldBinOpOfDisplacedShifts(LlvmBinaryOperator instruction)
        {
            LlvmValue candidate = instruction.Left, displacedValue = instruction.Right;
            LlvmBinaryOperator? first = null, second = null;
            LlvmConstant? offset = null;
            bool Match(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmBinaryOperator shift0 || !IsShiftOp(shift0.Opcode) || shift0.Left is not (LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue) ||
                    other is not LlvmBinaryOperator shift1 || !IsShiftOp(shift1.Opcode) || shift1.Left is not (LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue) ||
                    shift1.Right is not LlvmBinaryOperator amount || (amount.Opcode != LlvmOpcode.Add && !(amount.Opcode == LlvmOpcode.Or && amount.IsDisjoint)) ||
                    !ReferenceEquals(shift0.Right, amount.Left) || amount.Right is not (LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue)) return false;
                first = shift0; second = shift1; offset = (LlvmConstant)amount.Right; return true;
            }
            bool matched = Match(candidate, displacedValue);
            if (!matched)
            { (candidate, displacedValue) = (displacedValue, candidate); matched = Match(candidate, displacedValue); }
            if (!matched || offset is not LlvmConstantInt offsetInt || offsetInt.Value.GetLimitedValue(instruction.Type.GetScalarSizeInBits()) >= instruction.Type.GetScalarSizeInBits()) return null;
            if (first!.Opcode != second!.Opcode || (instruction.Opcode == LlvmOpcode.Add && first.Opcode != LlvmOpcode.Shl)) return null;
            LlvmValue secondConstant = Builder.CreateBinOp(first.Opcode, second.Left, offset);
            LlvmValue combined = Builder.CreateBinOp(instruction.Opcode, first.Left, secondConstant);
            return LogicResult(new LlvmBinaryOperator(first.Opcode, combined, first.Right), "fold_displaced_shifts");
        }
        private LlvmInstruction? FoldBitwiseLogicWithIntrinsics(LlvmBinaryOperator instruction)
        {
            if (!instruction.Left.HasOneUse || instruction.Left is not LlvmCallBase first || first.CalledFunction?.IntrinsicDescriptor is not { } firstDescriptor) return null;
            LlvmCallBase? second = instruction.Right as LlvmCallBase;
            if (second is not null && (!second.HasOneUse || second.CalledFunction?.IntrinsicDescriptor?.Kind != firstDescriptor.Kind)) return null;
            LlvmIntrinsicKind kind = firstDescriptor.Kind;
            bool reverse = kind == LlvmIntrinsicKind.BSwap || kind == LlvmIntrinsicKind.BitReverse;
            LlvmConstantInt? constant = instruction.Right as LlvmConstantInt;
            if (second is null && (!reverse || constant is null)) return null;
            if (kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr)
            {
                if (!ReferenceEquals(first.GetArgument(2), second!.GetArgument(2))) return null;
                LlvmValue x = Builder.CreateBinOp(instruction.Opcode, first.GetArgument(0), second.GetArgument(0));
                LlvmValue y = Builder.CreateBinOp(instruction.Opcode, first.GetArgument(1), second.GetArgument(1));
                LlvmFunction declaration = LlvmIntrinsic.GetDeclaration(F.Parent!, kind, (LlvmIntegerType)instruction.Type, Metrics);
                return LogicResult(new LlvmCallInstruction(declaration.FunctionType, declaration, [x, y, first.GetArgument(2)]), "fold_bitwise_funnel_intrinsics");
            }
            if (reverse)
            {
                LlvmValue rhs = second is not null ? second.GetArgument(0) : LogicC(instruction.Type, kind == LlvmIntrinsicKind.BSwap ? constant!.Value.ByteSwap() : constant!.Value.ReverseBits());
                LlvmValue value = Builder.CreateBinOp(instruction.Opcode, first.GetArgument(0), rhs);
                LlvmFunction declaration = LlvmIntrinsic.GetDeclaration(F.Parent!, kind, (LlvmIntegerType)instruction.Type, Metrics);
                return LogicResult(new LlvmCallInstruction(declaration.FunctionType, declaration, [value]), "fold_bitwise_reverse_intrinsics");
            }
            return null;
        }
    }
}
