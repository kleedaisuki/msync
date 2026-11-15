// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp hoistArithmetics;
// ScalarOptions.td reassociation limits; IRBuilder.h / ConstantFolder.h creation.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicmArithmetic
{
    private readonly record struct Query(LlvmLoop Loop, LlvmDominatorTree Tree,
        Action<LlvmInstruction> Erase, uint IntLimit, uint FpLimit, Dictionary<string, ulong>? Metrics)
    {
        internal LlvmDataLayout Layout => Loop.Header.Parent!.Parent!.DataLayout;
        internal LlvmInstruction InsertPoint => Loop.GetLoopPreheader()!.GetTerminator();
    }
    public static bool HoistArithmetics(LlvmInstruction instruction, LlvmLoop loop, LlvmDominatorTree tree,
        Action<LlvmInstruction> erase, uint intReassociationLimit = 5, uint fpReassociationLimit = 5,
        Dictionary<string, ulong>? metrics = null)
    {
        Query query = new(loop, tree, erase, intReassociationLimit, fpReassociationLimit, metrics);
        Count(query, "queries");
        if (HoistMinMax(instruction, query)) { Count(query, "minmax_hoisted"); return true; }
        if (HoistGep(instruction, query)) { Count(query, "gep_hoisted"); return true; }
        if (HoistAddSub(instruction, query)) { Count(query, "addsub_hoisted"); return true; }
        if (HoistMulAddAssociation(instruction, query))
        { Count(query, instruction.Type.IsIntOrIntVectorTy() ? "int_association_hoisted" : "fp_association_hoisted"); return true; }
        if (HoistBinaryAssociation(instruction, query)) { Count(query, "binary_association_hoisted"); return true; }
        if (HoistSubAddAssociation(instruction, query)) { Count(query, "binary_association_hoisted"); return true; }
        return false;
    }
    private static void Count(in Query query, string key, ulong amount = 1)
    {
        if (query.Metrics is null) return;
        key = "licm.arithmetic." + key;
        query.Metrics[key] = query.Metrics.GetValueOrDefault(key) + amount;
    }
    private static LlvmValue CreateBinary(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        LlvmInstruction before, in Query query, string name, LlvmFastMathFlags flags = LlvmFastMathFlags.None,
        bool nuw = false, bool nsw = false)
    {
        // ConstantFolder::FoldBinOpFMF deliberately delegates to FoldBinOp.
        if (left is LlvmConstant lhs && right is LlvmConstant rhs &&
            LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, lhs, rhs, query.Layout, query.Metrics) is { } folded)
        { Count(query, "builder.constant_folded"); return folded; }
        var result = new LlvmBinaryOperator(opcode, left, right)
        { FastMathFlags = flags, HasNoUnsignedWrap = nuw, HasNoSignedWrap = nsw, Source = before.Source };
        result.SetName(name);
        before.Parent!.InsertBefore(result, before);
        Count(query, "builder.instructions_created");
        return result;
    }
    private static void DropPoisonFlags(LlvmBinaryOperator operation)
    {
        operation.HasNoUnsignedWrap = false; operation.HasNoSignedWrap = false;
        operation.IsExact = false; operation.IsDisjoint = false;
        operation.FastMathFlags &= ~(LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs);
    }
    private static bool IsReassociable(LlvmBinaryOperator operation, LlvmOpcode integer, LlvmOpcode floating) =>
        operation.Opcode == integer || operation.Opcode == floating &&
        (operation.FastMathFlags & (LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros)) ==
            (LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros);
}
