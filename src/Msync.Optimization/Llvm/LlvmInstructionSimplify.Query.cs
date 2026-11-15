// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// simplifyInstruction / simplifyInstructionWithOperands SimplifyQuery dispatch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal static LlvmValue? SimplifyInstruction(LlvmInstruction instruction, BinaryQuery query)
    {
        if (query.Context is null) query = query with { Context = instruction };
        LlvmValue? result = instruction switch
        {
            LlvmPhiInstruction phi => SimplifyPhiCore(phi, query),
            LlvmBinaryOperator binary => SimplifyBinaryWithQuery(binary, query),
            LlvmICmpInstruction compare => SimplifyIntegerCompare(compare.Predicate, compare.Left, compare.Right, query, 3),
            LlvmFCmpInstruction compare => SimplifyFloatingCompare(compare.Predicate, compare.Left, compare.Right, compare.FastMathFlags, query, 3),
            LlvmCastInstruction cast => SimplifyIntegerCast(cast.Opcode, cast.Operand, cast.Type, query, 3),
            LlvmSelectInstruction select => SimplifyScalarSelect(select.Condition, select.TrueValue, select.FalseValue, select.FastMathFlags, query, 3),
            LlvmUnaryOperator unary => SimplifyFNeg(unary.Operand, query),
            _ => SimplifyOtherInstruction(instruction, instruction.Operands.Select(value => value!).ToArray(), query, 3)
        };
        return ReferenceEquals(result, instruction) ? LlvmPoisonValue.Get(instruction.Type) : result;
    }

    private static LlvmValue? SimplifyBinaryWithQuery(LlvmBinaryOperator instruction, BinaryQuery query) =>
        instruction.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem
            ? SimplifyFloatingBinary(instruction.Opcode, instruction.Left, instruction.Right, instruction.FastMathFlags, query)
            : SimplifyIntegerBinary(instruction.Opcode, instruction.Left, instruction.Right,
                query.UseInstructionInfo && instruction.HasNoSignedWrap, query.UseInstructionInfo && instruction.HasNoUnsignedWrap,
                query.UseInstructionInfo && instruction.IsExact, query, 3);
}
