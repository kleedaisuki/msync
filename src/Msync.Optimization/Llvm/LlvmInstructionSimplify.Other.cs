// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyInsertValueInst/simplifyExtractValueInst,
// simplifyFreezeInst, simplifyCall and default instruction dispatch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyOtherInstruction(LlvmInstruction instruction, IReadOnlyList<LlvmValue> operands,
        BinaryQuery q, uint depth)
    {
        return instruction switch
        {
            LlvmAllocaInstruction => null,
            LlvmInsertValueInstruction insert => SimplifyAggregateInsert(operands[0], operands[1], insert.Indices, q),
            LlvmExtractValueInstruction extract => SimplifyAggregateExtract(operands[0], extract.Indices, q),
            LlvmFreezeInstruction => LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(operands[0], q.Tree, q.Context, q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC)
                ? operands[0] : null,
            LlvmCallInstruction call => SimplifyScalarCall(call, operands, q),
            LlvmGetElementPtrInstruction gep => SimplifyScalarGep(gep, operands[0], operands.Skip(1).ToArray(), q),
            LlvmLoadInstruction load => SimplifyScalarLoad(load, operands[0], q),
            _ => FoldBitwiseReplacedOperands(instruction, operands, q)
        };
    }

    private static LlvmValue? SimplifyAggregateInsert(LlvmValue aggregate, LlvmValue value,
        IReadOnlyList<uint> indices, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.aggregate.insert_queries");
        if (aggregate is LlvmConstant a && value is LlvmConstant v)
            return LlvmConstantFolding.ConstantFoldInsertValueInstruction(a, v, indices, q.Metrics);
        if (value is LlvmPoisonValue || q.IsUndef(value) && LlvmValueTracking.IsGuaranteedNotToBePoison(aggregate)) return aggregate;
        if (value is not LlvmExtractValueInstruction extract || extract.AggregateOperand.Type != aggregate.Type ||
            !extract.Indices.SequenceEqual(indices)) return null;
        if (aggregate is LlvmPoisonValue || q.IsUndef(aggregate) && LlvmValueTracking.IsGuaranteedNotToBePoison(extract.AggregateOperand))
            return extract.AggregateOperand;
        return aggregate == extract.AggregateOperand ? aggregate : null;
    }

    private static LlvmValue? SimplifyAggregateExtract(LlvmValue aggregate, IReadOnlyList<uint> indices, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.aggregate.extract_queries");
        if (aggregate is LlvmConstant constant)
            return LlvmConstantFolding.ConstantFoldExtractValueInstruction(constant, indices, q.Metrics);
        HashSet<LlvmInsertValueInstruction> visited = [];
        for (var insert = aggregate as LlvmInsertValueInstruction; insert is not null;
            insert = insert.AggregateOperand as LlvmInsertValueInstruction)
        {
            if (!visited.Add(insert)) break;
            int common = Math.Min(indices.Count, insert.Indices.Count);
            bool equal = true;
            for (int index = 0; index < common; ++index)
                if (indices[index] != insert.Indices[index]) { equal = false; break; }
            if (!equal) continue;
            if (indices.Count == insert.Indices.Count) return insert.InsertedValueOperand;
            break;
        }
        // The umul.with.overflow shortcut has no represented intrinsic ID.
        return null;
    }

    private static LlvmValue? SimplifyScalarCall(LlvmCallInstruction call, IReadOnlyList<LlvmValue> operands, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.call.queries");
        if (call.TailCallKind == LlvmTailCallKind.MustTail) return null;
        LlvmValue callee = operands[^1];
        if (callee is LlvmUndefValue or LlvmConstantPointerNull) return LlvmPoisonValue.Get(call.Type);
        if (callee is LlvmFunction { IntrinsicDescriptor: { } descriptor })
            return SimplifyScalarIntrinsic(descriptor.Kind, call.Type, operands.Take(call.ArgumentCount).ToArray(), q);
        if (call.CalledFunction is not null)
        {
            bool constantArgs = true;
            for (int index = 0; index < call.ArgumentCount; ++index)
                if (operands[index] is not LlvmConstant) { constantArgs = false; break; }
            if (constantArgs) Count(q.Metrics, "simplify.call.unrepresented_tli_intrinsic");
        }
        return null;
    }
}
