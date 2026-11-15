// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp public SimplifyQuery entry points.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal static LlvmValue? SimplifyBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right, BinaryQuery query) =>
        SimplifyBinary(opcode, left, right, LlvmFastMathFlags.None, query, 3);
    internal static LlvmValue? SimplifyBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        LlvmFastMathFlags flags, BinaryQuery query) => SimplifyBinary(opcode, left, right, flags, query, 3);
    internal static LlvmValue? SimplifyICmp(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right, BinaryQuery query) =>
        SimplifyIntegerCompare(predicate, left, right, query, 3);
    internal static LlvmValue? SimplifyFCmp(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right,
        LlvmFastMathFlags flags, BinaryQuery query) => SimplifyFloatingCompare(predicate, left, right, flags, query, 3);
    internal static LlvmValue? SimplifySelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse,
        LlvmFastMathFlags flags, BinaryQuery query) => SimplifyScalarSelect(condition, whenTrue, whenFalse, flags, query, 3);
    internal static LlvmValue? SimplifyCall(LlvmCallInstruction call, IReadOnlyList<LlvmValue> operands, BinaryQuery query) =>
        SimplifyScalarCall(call, operands, query);
    internal static LlvmValue? SimplifyCall(LlvmCallInstruction call, LlvmValue callee,
        IReadOnlyList<LlvmValue> arguments, BinaryQuery query) => SimplifyCallCore(call, callee, arguments, query);
    internal static LlvmValue? SimplifyLoadInst(LlvmLoadInstruction load, LlvmValue pointer, BinaryQuery query) =>
        SimplifyScalarLoad(load, pointer, query);
    internal static LlvmValue? SimplifyInstructionWithOperands(LlvmInstruction instruction,
        IReadOnlyList<LlvmValue> operands, BinaryQuery query) => SimplifyBitwiseReplacedInstruction(instruction, operands, query, 3);
    internal static bool? IsImpliedConditionForInstCombine(LlvmValue condition, LlvmCmpPredicate predicate,
        LlvmValue left, LlvmValue right, BinaryQuery query, bool lhsIsTrue = true) =>
        IsImpliedCondition(condition, predicate, left, right, query, lhsIsTrue);
    internal static bool? IsImpliedConditionForInstCombine(LlvmValue left, LlvmValue right,
        BinaryQuery query, bool lhsIsTrue = true) => IsImpliedCondition(left, right, query, lhsIsTrue);
    internal static LlvmValue? SimplifyWithOpReplaced(LlvmValue value, LlvmValue operand, LlvmValue replacement,
        BinaryQuery query, bool allowRefinement = true, ICollection<LlvmInstruction>? dropFlags = null) => BitwiseSimplifyWithOpsReplaced(value,
            [(operand, replacement)], allowRefinement ? query : query with { CanUseUndef = false }, allowRefinement, 3, dropFlags);
}
