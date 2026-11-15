// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// addValueAffectedByCondition/findValuesAffectedByCondition; represented scalar patterns.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static void FindValuesAffectedByCondition(LlvmValue condition, bool isAssume, Action<LlvmValue> insertAffected)
    {
        void Add(LlvmValue value)
        {
            if (value is LlvmArgument or LlvmGlobalValue) insertAffected(value);
            else if (value is LlvmInstruction)
            {
                insertAffected(value);
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr or LlvmOpcode.Trunc } cast &&
                    cast.Operand is LlvmInstruction or LlvmArgument) insertAffected(cast.Operand);
            }
        }
        void AddCmp(LlvmValue left, LlvmValue right)
        {
            if (isAssume) { Add(left); Add(right); }
            else if (right is LlvmConstant) Add(left);
        }
        void AddSquare(LlvmValue value)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasNoUnsignedWrap: true } mul &&
                ReferenceEquals(mul.Left, mul.Right)) Add(mul.Left);
        }
        List<LlvmValue> work = [condition]; HashSet<LlvmValue> visited = [];
        while (work.Count != 0)
        {
            var value = work[^1]; work.RemoveAt(work.Count - 1);
            if (!visited.Add(value)) continue;
            if (isAssume) { Add(value); if (AssumeNot(value) is { } inner) Add(inner); }
            if (AssumeLogical(value) is { } logical)
            {
                if (!isAssume) { work.Add(logical.Left); work.Add(logical.Right); }
            }
            else if (value is LlvmICmpInstruction cmp)
            {
                var a = cmp.Left; var b = cmp.Right; var predicate = cmp.Predicate;
                bool hasConstant = b is LlvmConstantInt;
                if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)
                {
                    Add(a); if (isAssume) Add(b);
                    if (hasConstant && a is LlvmBinaryOperator binary)
                    {
                        if (binary.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr && binary.Right is LlvmConstantInt)
                            Add(binary.Left);
                        else if (binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Sub)
                        { Add(binary.Left); Add(binary.Right); }
                    }
                }
                else
                {
                    AddCmp(a, b);
                    if (hasConstant && a is LlvmBinaryOperator binary)
                    {
                        if ((binary.Opcode == LlvmOpcode.Add || binary.Opcode == LlvmOpcode.Or && binary.IsDisjoint) && binary.Right is LlvmConstantInt)
                            Add(binary.Left);
                        else if (binary.Opcode == LlvmOpcode.Sub) { Add(binary.Left); Add(binary.Right); }
                        if (predicate is >= LlvmCmpPredicate.ICmpUgt and <= LlvmCmpPredicate.ICmpUle &&
                            (binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or || binary.Opcode == LlvmOpcode.Add && binary.HasNoUnsignedWrap))
                        { Add(binary.Left); Add(binary.Right); }
                    }
                    if (a is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast && !cast.Type.IsVectorTy() &&
                        (predicate == LlvmCmpPredicate.ICmpSlt && b is LlvmConstantInt { IsZero: true } ||
                         predicate == LlvmCmpPredicate.ICmpSgt && b is LlvmConstantInt { IsMinusOne: true }))
                        insertAffected(cast.Operand);
                }
                AddSquare(a); AddSquare(b);
                if (hasConstant && a is LlvmCallBase call && call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CTPop)
                    Add(call.GetArgument(0));
            }
            else if (value is LlvmFCmpInstruction cmpFp)
            {
                AddCmp(cmpFp.Left, cmpFp.Right);
                if (cmpFp.Left is LlvmUnaryOperator neg) Add(neg.Operand);
                else if (cmpFp.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmConstantFP { Value.IsZero: true, IsNegative: true } } sub)
                    Add(sub.Right);
            }
            else if (!isAssume && value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc) Add(trunc.Operand);
            else if (!isAssume && AssumeNot(value) is { } inner) work.Add(inner);
        }
    }
    private static LlvmValue? AssumeNot(LlvmValue value)
    {
        if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } binary) return null;
        if (binary.Right is LlvmConstantInt { IsMinusOne: true }) return binary.Left;
        return binary.Left is LlvmConstantInt { IsMinusOne: true } ? binary.Right : null;
    }
    private static (LlvmValue Left, LlvmValue Right)? AssumeLogical(LlvmValue value)
    {
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And or LlvmOpcode.Or } binary && value.Type.IsIntOrIntVectorTy(1))
            return (binary.Left, binary.Right);
        if (value is not LlvmSelectInstruction select || !ReferenceEquals(select.Condition.Type, select.Type)) return null;
        if (select.FalseValue is LlvmConstantInt { IsZero: true }) return (select.Condition, select.TrueValue);
        if (select.TrueValue is LlvmConstantInt { IsMinusOne: true }) return (select.Condition, select.FalseValue);
        return null;
    }
}
