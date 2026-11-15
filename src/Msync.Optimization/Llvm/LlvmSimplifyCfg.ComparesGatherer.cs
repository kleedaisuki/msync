// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// getConstantInt and ConstantComparesGatherer.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    private static LlvmConstantInt? GetComparisonConstantInt(LlvmValue value, LlvmDataLayout layout)
    {
        if (value is LlvmConstantInt integer) return integer;
        if (value is not LlvmConstant || !value.Type.IsPointerTy()) return null;
        // The represented DataLayout has stable, integral pointers. ConstantExpr
        // inttoptr is not represented; a null pointer has the source zero case.
        return value is LlvmConstantPointerNull
            ? LlvmConstantInt.Get(layout.GetIntPtrType(value.Type.Context,
                ((LlvmPointerType)value.Type).AddressSpace), 0) : null;
    }

    private static bool MatchComparisonLogical(LlvmValue value, bool isOr, out LlvmValue left, out LlvmValue right)
    {
        if (!value.Type.GetScalarType().IsIntegerTy(1)) { left = right = null!; return false; }
        if (value is LlvmBinaryOperator binary && binary.Opcode == (isOr ? LlvmOpcode.Or : LlvmOpcode.And))
        { left = binary.Left; right = binary.Right; return true; }
        if (value is LlvmSelectInstruction select && select.Condition.Type == select.Type)
        {
            if (isOr && select.TrueValue is LlvmConstantInt { IsOne: true })
            { left = select.Condition; right = select.FalseValue; return true; }
            if (!isOr && select.FalseValue is LlvmConstantInt { IsZero: true })
            { left = select.Condition; right = select.TrueValue; return true; }
        }
        left = right = null!;
        return false;
    }

    private sealed class ConstantComparesGatherer
    {
        private readonly LlvmDataLayout layout;
        private readonly Dictionary<string, ulong>? metrics;
        internal LlvmValue? CompValue;
        internal LlvmValue? Extra;
        internal readonly List<LlvmConstantInt> Vals = [];
        internal uint UsedICmps;
        internal bool IsEq;
        private bool ignoreFirstMatch, multipleMatches;

        internal ConstantComparesGatherer(LlvmInstruction condition, LlvmDataLayout layout,
            Dictionary<string, ulong>? metrics = null)
        {
            this.layout = layout;
            this.metrics = metrics;
            Gather(condition);
            if (CompValue is not null || !multipleMatches) return;
            Extra = null; Vals.Clear(); UsedICmps = 0; ignoreFirstMatch = true;
            Count("simplifycfg.compare_gather.retry");
            Gather(condition);
        }

        private bool SetValueOnce(LlvmValue value)
        {
            if (ignoreFirstMatch) { ignoreFirstMatch = false; return false; }
            if (CompValue is not null && CompValue != value) { multipleMatches = true; return false; }
            CompValue = value;
            return true;
        }

        private bool MatchInstruction(LlvmInstruction instruction, bool isEq)
        {
            if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } not)
            {
                if (not.Left is LlvmConstantInt { Value.IsAllOnes: true } && not.Right is LlvmInstruction right)
                { instruction = right; isEq = !isEq; }
                else if (not.Right is LlvmConstantInt { Value.IsAllOnes: true } && not.Left is LlvmInstruction left)
                { instruction = left; isEq = !isEq; }
            }
            if (instruction is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true } trunc)
            {
                if (!SetValueOnce(trunc.Operand)) return false;
                ++UsedICmps;
                Vals.Add(LlvmConstantInt.Get((LlvmIntegerType)trunc.Operand.Type, isEq ? 1UL : 0UL));
                return true;
            }
            if (instruction is not LlvmICmpInstruction compare || GetComparisonConstantInt(compare.Right, layout) is not { } constant)
                return false;
            if (compare.Predicate == (isEq ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe))
            {
                if (compare.Left is LlvmBinaryOperator binary && binary.Right is LlvmConstantInt bits)
                {
                    LlvmAPInt mask = binary.Opcode == LlvmOpcode.And ? ~bits.Value : bits.Value;
                    bool masked = binary.Opcode == LlvmOpcode.And && (constant.Value & ~mask) == constant.Value;
                    bool ored = binary.Opcode == LlvmOpcode.Or && (constant.Value | mask) == constant.Value;
                    if (mask.IsPowerOf2 && (masked || ored))
                    {
                        if (!SetValueOnce(binary.Left)) return false;
                        Vals.Add(constant);
                        Vals.Add(LlvmConstantInt.Get(instruction.Type.Context,
                            masked ? constant.Value | mask : constant.Value & ~mask));
                        ++UsedICmps;
                        return true;
                    }
                }
                if (!SetValueOnce(compare.Left)) return false;
                ++UsedICmps; Vals.Add(constant);
                return true;
            }
            LlvmConstantRange span = LlvmConstantRange.MakeExactICmpRegion(compare.Predicate, constant.Value);
            LlvmValue candidate = compare.Left;
            if (compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt add } addition)
            { span = span.Subtract(add.Value); candidate = addition.Left; }
            if (!isEq) span = span.Inverse();
            if (span.IsSizeLargerThan(8) || span.IsEmptySet) return false;
            if (!SetValueOnce(candidate)) return false;
            LlvmAPInt item = span.Lower;
            do
            {
                Vals.Add(LlvmConstantInt.Get(instruction.Type.Context, item));
                item += new LlvmAPInt(item.BitWidth, 1);
            } while (item != span.Upper);
            ++UsedICmps;
            return true;
        }

        private void Gather(LlvmValue value)
        {
            if (MatchComparisonLogical(value, true, out var left, out var right)) IsEq = true;
            else if (MatchComparisonLogical(value, false, out left, out right)) IsEq = false;
            else return;
            List<LlvmValue> stack = [left, right];
            HashSet<LlvmValue> visited = [value, left, right];
            while (stack.Count != 0)
            {
                value = stack[^1]; stack.RemoveAt(stack.Count - 1);
                Count("simplifycfg.compare_gather.nodes");
                if (value is LlvmInstruction instruction)
                {
                    if (MatchComparisonLogical(instruction, IsEq, out left, out right))
                    {
                        if (visited.Add(right)) stack.Add(right);
                        if (visited.Add(left)) stack.Add(left);
                        continue;
                    }
                    if (MatchInstruction(instruction, IsEq)) continue;
                }
                if (Extra is null) { Extra = value; continue; }
                CompValue = null;
                Count("simplifycfg.compare_gather.unmatched");
                break;
            }
        }

        private void Count(string key)
        { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
    }
}
