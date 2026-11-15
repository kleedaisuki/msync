// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ConstantFolding.cpp
// ConstantFoldInstOperandsImpl/FPInstOperands, represented instruction dispatch.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldInstruction(LlvmInstruction instruction, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.scan.queries");
        Dictionary<LlvmConstant, LlvmConstant>? folded = null;
        if (instruction is LlvmPhiInstruction phi)
        {
            LlvmConstant? common = null;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                Count(metrics, "constant_fold.scan.phi_incoming");
                LlvmValue incoming = phi.GetIncomingValue(i);
                if (incoming is LlvmUndefValue) continue;
                if (incoming is not LlvmConstant constant) return null;
                constant = FoldConstantOperand(constant, layout, ref folded, metrics);
                if (common is not null && !ReferenceEquals(common, constant)) return null;
                common = constant;
            }
            return common ?? LlvmUndefValue.Get(phi.Type);
        }
        List<LlvmConstant> operands = new(instruction.OperandCount);
        foreach (LlvmUse operand in instruction.OperandUses)
        {
            Count(metrics, "constant_fold.scan.operands");
            if (operand.Value is not LlvmConstant constant) return null;
            operands.Add(FoldConstantOperand(constant, layout, ref folded, metrics));
        }
        return ConstantFoldInstOperands(instruction, operands, layout, metrics: metrics);
    }

    public static LlvmConstant? ConstantFoldInstOperands(LlvmInstruction instruction, IReadOnlyList<LlvmConstant> operands,
        LlvmDataLayout layout, bool allowNonDeterministic = true, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(instruction.OperandCount == operands.Count);
        Count(metrics, "constant_fold.instruction.queries");
        LlvmOpcode opcode = instruction.Opcode;
        if (opcode == LlvmOpcode.FNeg) return ConstantFoldUnaryOpOperand(opcode, operands[0], layout, metrics);
        if (opcode is >= LlvmOpcode.Add and <= LlvmOpcode.Xor)
            return opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem ?
                ConstantFoldFPInstOperands(opcode, operands[0], operands[1], layout, instruction, allowNonDeterministic, metrics) :
                ConstantFoldBinaryOpOperands(opcode, operands[0], operands[1], layout, metrics);
        if (opcode is >= LlvmOpcode.Trunc and <= LlvmOpcode.AddrSpaceCast)
            return ConstantFoldCastOperand(opcode, operands[0], instruction.Type, layout, metrics);
        switch (instruction)
        {
            case LlvmExtractElementInstruction:
                return LlvmConstantExpr.GetExtractElement(operands[0], operands[1]);
            case LlvmInsertElementInstruction:
                return LlvmConstantExpr.GetInsertElement(operands[0], operands[1], operands[2]);
            case LlvmShuffleVectorInstruction shuffle:
                return LlvmConstantExpr.GetShuffleVector(operands[0], operands[1], shuffle.ShuffleMask);
            case LlvmCmpInstruction comparison:
                return ConstantFoldCompareInstOperands(comparison.Predicate, operands[0], operands[1], layout, instruction.Parent?.Parent, metrics);
            case LlvmSelectInstruction:
                return ConstantFoldSelectInstruction(operands[0], operands[1], operands[2], metrics);
            case LlvmExtractValueInstruction extract:
                return ConstantFoldExtractValueInstruction(operands[0], extract.Indices, metrics);
            case LlvmInsertValueInstruction insert:
                return ConstantFoldInsertValueInstruction(operands[0], operands[1], insert.Indices, metrics);
            case LlvmLoadInstruction load:
                return load.IsVolatile ? null : ConstantFoldLoadFromConstPtr(operands[0], load.Type, layout, metrics);
            case LlvmFreezeInstruction:
                return ConstantWellDefined(operands[0], 0) ? operands[0] : null;
            case LlvmGetElementPtrInstruction gep:
                return ConstantFoldGetElementPtr(gep.SourceElementType, operands[0], operands.Skip(1).ToArray(), layout, gep.NoWrapFlags, metrics: metrics);
            case LlvmCallBase call:
                if (operands[^1] is LlvmFunction { IntrinsicDescriptor: { } descriptor })
                    return ConstantFoldIntrinsic(descriptor.Kind, operands.Take(call.ArgumentCount).ToArray(), call.Type, metrics, call);
                Count(metrics, "constant_fold.unported.call_intrinsic_tli"); return null;
            default:
                Count(metrics, "constant_fold.instruction.no_fold"); return null;
        }
    }
    public static LlvmConstant? ConstantFoldFPInstOperands(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right,
        LlvmDataLayout layout, LlvmInstruction? context, bool allowNonDeterministic = true,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.fp.queries");
        if (context?.Parent?.Parent is null && (IsDenormal(left) || IsDenormal(right)))
        { Count(metrics, "constant_fold.fp.dynamic_denormal_input"); return null; }
        if (!allowNonDeterministic && context is ILlvmFastMathInstruction fp &&
            (fp.FastMathFlags & (LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.AllowReassoc |
                LlvmFastMathFlags.AllowContract | LlvmFastMathFlags.AllowReciprocal)) != 0)
        { Count(metrics, "constant_fold.fp.nondeterministic_flags"); return null; }
        LlvmConstant? result = ConstantFoldBinaryOpOperands(opcode, left, right, layout, metrics);
        if (result is null) return null;
        if (context?.Parent?.Parent is null && IsDenormal(result))
        { Count(metrics, "constant_fold.fp.dynamic_denormal_output"); return null; }
        if (!allowNonDeterministic && result is LlvmConstantFP { Value.IsNaN: true })
        { Count(metrics, "constant_fold.fp.nondeterministic_nan"); return null; }
        return result;
    }
    public static LlvmConstant? ConstantFoldGetElementPtr(LlvmType pointee, LlvmConstant pointer,
        IReadOnlyList<LlvmConstant> indices, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.gep.core_queries");
        return LlvmConstantExpr.GetGetElementPtr(pointee, pointer, indices);
    }

    private static bool ConstantWellDefined(LlvmConstant constant, uint depth)
    {
        // ValueTracking.cpp's constant slice, UndefOrPoison and recursion bound6.
        if (depth >= 6 || constant is LlvmUndefValue) return false;
        if (constant is LlvmConstantInt or LlvmGlobalVariable or LlvmConstantFP or
            LlvmConstantPointerNull or LlvmFunction or LlvmConstantData) return true;
        if (constant.Type.IsAggregateType())
        {
            foreach (LlvmUse operand in constant.OperandUses)
                if (!ConstantWellDefined((LlvmConstant)operand.Value!, depth + 1)) return false;
            return true;
        }
        return false;
    }
}
