// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// collectOffsetOp/OffsetResult/foldICmpEqualityWithOffset (6280–6425).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private readonly record struct ICmpOffsetResult(LlvmValue Value, LlvmValue? TrueValue = null, LlvmValue? FalseValue = null, LlvmSelectInstruction? Source = null)
        {
            public LlvmValue Materialize(LlvmTransformBuilder builder)
            {
                if (Source is null) return Value;
                var result = builder.CreateSelect(Value, TrueValue!, FalseValue!);
                if (result is LlvmInstruction instruction && result != TrueValue && result != FalseValue && result != Value) instruction.Source = Source.Source;
                return result;
            }
        }
        private void CollectICmpOffsetOps(LlvmValue value, List<(LlvmOpcode Opcode, LlvmValue RHS)> offsets, bool recurse)
        {
            if (value is not LlvmInstruction { HasOneUse: true } instruction) return;
            Count("icmp.equality.offset.collect");
            if (instruction is LlvmSelectInstruction select)
            {
                if (recurse) { CollectICmpOffsetOps(select.TrueValue, offsets, false); CollectICmpOffsetOps(select.FalseValue, offsets, false); }
                return;
            }
            if (instruction is not LlvmBinaryOperator binary) return;
            switch (binary.Opcode)
            {
                case LlvmOpcode.Add: offsets.Add((LlvmOpcode.Sub, binary.Right)); offsets.Add((LlvmOpcode.Sub, binary.Left)); break;
                case LlvmOpcode.Sub: offsets.Add((LlvmOpcode.Add, binary.Right)); break;
                case LlvmOpcode.Xor: offsets.Add((LlvmOpcode.Xor, binary.Right)); offsets.Add((LlvmOpcode.Xor, binary.Left)); break;
                case LlvmOpcode.Shl:
                    if (binary.HasNoSignedWrap) offsets.Add((LlvmOpcode.AShr, binary.Right));
                    if (binary.HasNoUnsignedWrap) offsets.Add((LlvmOpcode.LShr, binary.Right));
                    break;
            }
        }
        private LlvmValue? ApplyICmpOffset(LlvmValue value, LlvmOpcode opcode, LlvmValue rhs, LlvmInstructionSimplify.BinaryQuery query)
        {
            Count("icmp.equality.offset.simplify_attempts");
            if (opcode is LlvmOpcode.AShr or LlvmOpcode.LShr)
            {
                bool reversible = value is LlvmConstantInt c && rhs is LlvmConstantInt shift &&
                    (opcode == LlvmOpcode.AShr ? c.Value.AShr(shift.Value) : c.Value.LShr(shift.Value)).Shl(shift.Value) == c.Value;
                bool matchingShift = value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } binary && binary.Right == rhs &&
                    (opcode == LlvmOpcode.AShr ? binary.HasNoSignedWrap : binary.HasNoUnsignedWrap);
                if (!reversible && !matchingShift) return null;
            }
            var simplified = LlvmInstructionSimplify.SimplifyBinOp(opcode, value, rhs, query);
            if (simplified is null || simplified is LlvmConstant && !ICmpImmediate(simplified)) return null;
            if (LlvmValueTracking.ImpliesPoison(rhs, value, Metrics)) return simplified;
            Count("icmp.equality.offset.poison_rejected");
            return null;
        }
        private ICmpOffsetResult? ApplyICmpOffsetResult(LlvmValue value, LlvmOpcode opcode, LlvmValue rhs, LlvmInstructionSimplify.BinaryQuery query)
        {
            if (value is not LlvmSelectInstruction select)
                return ApplyICmpOffset(value, opcode, rhs, query) is { } result ? new ICmpOffsetResult(result) : null;
            if (!select.HasOneUse) return null;
            var whenTrue = ApplyICmpOffset(select.TrueValue, opcode, rhs, query);
            if (whenTrue is null) return null;
            var whenFalse = ApplyICmpOffset(select.FalseValue, opcode, rhs, query);
            return whenFalse is null ? null : new ICmpOffsetResult(select.Condition, whenTrue, whenFalse, select);
        }
        private LlvmInstruction? FoldICmpEqualityWithOffset(LlvmICmpInstruction instruction)
        {
            if (!instruction.Left.Type.IsIntOrIntVectorTy()) return null;
            var offsets = new List<(LlvmOpcode Opcode, LlvmValue RHS)>(4);
            CollectICmpOffsetOps(instruction.Left, offsets, true); CollectICmpOffsetOps(instruction.Right, offsets, true);
            var query = Query(instruction);
            foreach (var (opcode, rhs) in offsets)
            {
                var left = ApplyICmpOffsetResult(instruction.Left, opcode, rhs, query);
                if (!left.HasValue) continue;
                var right = ApplyICmpOffsetResult(instruction.Right, opcode, rhs, query);
                if (!right.HasValue) continue;
                Count("icmp.equality.offset.rewrite");
                return new LlvmICmpInstruction(instruction.Predicate, left.Value.Materialize(Builder), right.Value.Materialize(Builder));
            }
            return null;
        }
    }
}
