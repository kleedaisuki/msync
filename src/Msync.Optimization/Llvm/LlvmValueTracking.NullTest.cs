// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp11265-11294.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmValueTracking
{
    public static LlvmValue? StripNullTest(LlvmValue value, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.strip_null_test.queries");
        if (value is LlvmBinaryOperator binary && binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Or)
        {
            LlvmValue? input = Match(binary.Left, binary.Right) ?? Match(binary.Right, binary.Left);
            if (input is not null) { Count(metrics, "value_tracking.strip_null_test.shift_and_nonzero"); return input; }
        }
        if (value is LlvmSelectInstruction select)
        {
            LlvmValue? input = Negated(select.TrueValue, select.FalseValue) ?? Negated(select.FalseValue, select.TrueValue);
            if (input is not null) { Count(metrics, "value_tracking.strip_null_test.select_negated"); return input; }
        }
        return null;
        static LlvmValue? Match(LlvmValue first, LlvmValue second)
        {
            if (first is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt amount } shift ||
                second is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmICmpInstruction
                    { Predicate: LlvmCmpPredicate.ICmpNe, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask } and,
                      Right: LlvmConstantInt { Value.IsZero: true } } }) return null;
            LlvmAPInt bits = mask.Value;
            return ReferenceEquals(shift.Left, and.Left) && !bits.IsZero && (bits & (bits + new LlvmAPInt(bits.BitWidth, 1, implicitTrunc: true))).IsZero &&
                amount.Value.GetZExtValue() == bits.PopCount() ? shift.Left : null;
        }
        static LlvmValue? Negated(LlvmValue first, LlvmValue second) => second is LlvmBinaryOperator
            { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { Value.IsZero: true } } neg && ReferenceEquals(first, neg.Right) ? first : null;
    }
}
