// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp SimpleValue/GEPValue.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private static bool CanHandleSimple(LlvmInstruction instruction)
    {
        if (instruction is LlvmCallInstruction call)
            return LlvmMemoryEffects.CreateFromIntValue(call.GetMemoryEffects().Raw).DoesNotAccessMemory &&
                !call.Parent!.Parent!.IsPresplitCoroutine;
        return instruction is LlvmCastInstruction or LlvmUnaryOperator or LlvmBinaryOperator or LlvmCmpInstruction or
            LlvmSelectInstruction or LlvmExtractElementInstruction or LlvmInsertElementInstruction or
            LlvmShuffleVectorInstruction or LlvmExtractValueInstruction or LlvmInsertValueInstruction or LlvmFreezeInstruction;
    }

    private static void MatchSelect(LlvmSelectInstruction select, out LlvmValue condition, out LlvmValue a, out LlvmValue b)
    {
        condition = select.Condition; a = select.TrueValue; b = select.FalseValue;
        if (condition is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
        {
            if (xor.Right is LlvmConstantInt { IsMinusOne: true }) { condition = xor.Left; (a, b) = (b, a); }
            else if (xor.Left is LlvmConstantInt { IsMinusOne: true }) { condition = xor.Right; (a, b) = (b, a); }
        }
    }

    private sealed class SimpleComparer : IEqualityComparer<LlvmInstruction>
    {
        private readonly Dictionary<LlvmValue, int> order = new(ReferenceEqualityComparer.Instance);
        private int Order(LlvmValue value)
        {
            if (order.TryGetValue(value, out int id)) return id;
            order.Add(value, id = order.Count); return id;
        }
        public int GetHashCode(LlvmInstruction i)
        {
            HashCode hash = new(); hash.Add(i.Opcode);
            if (i is LlvmCallInstruction call)
            {
                if (call.CalledFunction?.IntrinsicDescriptor is { IsCommutative: true } && call.ArgumentCount >= 2)
                {
                    int a = Order(call.GetArgument(0)), b = Order(call.GetArgument(1));
                    if (a > b) (a, b) = (b, a);
                    hash.Add(a); hash.Add(b);
                    foreach (var operand in call.Operands.Skip(2)) hash.Add(Order(operand!));
                    return hash.ToHashCode();
                }
                if (call.IsConvergent) hash.Add(call.Parent);
                foreach (var operand in call.Operands) hash.Add(Order(operand!));
                return hash.ToHashCode();
            }
            if (i is LlvmBinaryOperator bin)
            {
                int a = Order(bin.Left), b = Order(bin.Right);
                if (bin.IsCommutative && a > b) (a, b) = (b, a);
                hash.Add(a); hash.Add(b); return hash.ToHashCode();
            }
            if (i is LlvmCmpInstruction compare)
            {
                int a = Order(compare.Left), b = Order(compare.Right);
                var p = compare.Predicate; var swapped = LlvmCmpInstruction.GetSwappedPredicate(p);
                if (a > b || a == b && p > swapped) { (a, b) = (b, a); p = swapped; }
                hash.Add(p); hash.Add(a); hash.Add(b); return hash.ToHashCode();
            }
            if (i is LlvmSelectInstruction select)
            {
                MatchSelect(select, out var condition, out var a, out var b);
                if (condition is LlvmCmpInstruction c)
                {
                    var p = c.Predicate; var inverse = LlvmCmpInstruction.GetInversePredicate(p);
                    if (inverse < p) { p = inverse; (a, b) = (b, a); }
                    hash.Add(p); hash.Add(Order(c.Left)); hash.Add(Order(c.Right));
                }
                else hash.Add(Order(condition));
                hash.Add(Order(a)); hash.Add(Order(b)); return hash.ToHashCode();
            }
            if (i is LlvmCastInstruction) hash.Add(i.Type);
            foreach (var operand in i.Operands) hash.Add(Order(operand!));
            if (i is LlvmExtractValueInstruction extract) foreach (uint index in extract.Indices) hash.Add(index);
            if (i is LlvmInsertValueInstruction insert) foreach (uint index in insert.Indices) hash.Add(index);
            return hash.ToHashCode();
        }
        public bool Equals(LlvmInstruction? a, LlvmInstruction? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Opcode != b.Opcode) return false;
            if (a.IsIdenticalToWhenDefined(b, intersectAttrs: true))
                return a is not LlvmCallInstruction call || !call.IsConvergent || a.Parent == b.Parent;
            if (a is LlvmBinaryOperator ab && b is LlvmBinaryOperator bb)
                return ab.IsCommutative && ab.Left == bb.Right && ab.Right == bb.Left;
            if (a is LlvmCmpInstruction ac && b is LlvmCmpInstruction bc)
                return ac.Left == bc.Right && ac.Right == bc.Left &&
                    LlvmCmpInstruction.GetSwappedPredicate(ac.Predicate) == bc.Predicate;
            if (a is LlvmCallInstruction ai && b is LlvmCallInstruction bi &&
                ai.CalledFunction?.IntrinsicDescriptor is { IsCommutative: true } intrinsic &&
                bi.CalledFunction?.IntrinsicDescriptor?.Kind == intrinsic.Kind && ai.ArgumentCount >= 2)
                return ai.GetArgument(0) == bi.GetArgument(1) && ai.GetArgument(1) == bi.GetArgument(0) &&
                    ai.Operands.Skip(2).SequenceEqual(bi.Operands.Skip(2)) &&
                    ai.HasSameSpecialState(bi, ignoreAlignment: false, intersectAttrs: true);
            if (a is not LlvmSelectInstruction sa || b is not LlvmSelectInstruction sb) return false;
            MatchSelect(sa, out var ca, out var aa, out var ba);
            MatchSelect(sb, out var cb, out var ba2, out var bb2);
            if (ca == cb && aa == ba2 && ba == bb2) return true;
            return aa == bb2 && ba == ba2 && ca is LlvmCmpInstruction l && cb is LlvmCmpInstruction r &&
                l.Left == r.Left && l.Right == r.Right && LlvmCmpInstruction.GetInversePredicate(l.Predicate) == r.Predicate;
        }
    }

    private readonly record struct GepValue(LlvmGetElementPtrInstruction Instruction, long? ConstantOffset,
        LlvmStructType? TargetStorage, int LogicalType);
    private sealed class GepComparer : IEqualityComparer<GepValue>
    {
        public int GetHashCode(GepValue key)
        {
            HashCode hash = new(); hash.Add(key.Instruction.Opcode); hash.Add(key.Instruction.PointerOperand);
            hash.Add(key.TargetStorage); hash.Add(key.LogicalType);
            if (key.ConstantOffset.HasValue) hash.Add(key.ConstantOffset.Value);
            else foreach (var operand in key.Instruction.Operands.Skip(1)) hash.Add(operand);
            return hash.ToHashCode();
        }
        public bool Equals(GepValue a, GepValue b) => a.Instruction.PointerOperand == b.Instruction.PointerOperand &&
            a.TargetStorage == b.TargetStorage && a.LogicalType == b.LogicalType &&
            (a.ConstantOffset.HasValue && b.ConstantOffset.HasValue ? a.ConstantOffset.Value == b.ConstantOffset.Value :
                a.Instruction.IsIdenticalToWhenDefined(b.Instruction));
    }
}
