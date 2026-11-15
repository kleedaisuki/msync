// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/TargetFolder.h value-based scalar/aggregate folders.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmTargetFolder(LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
{
    public LlvmDataLayout Layout => layout;

    private LlvmConstant Fold(LlvmConstant constant) => LlvmConstantFolding.ConstantFoldConstant(constant, layout, metrics);

    public LlvmConstant? FoldBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right)
    {
        Count("binary.queries");
        if (left is not LlvmConstant a || right is not LlvmConstant b) return null;
        return Result("binary", LlvmConstantExpr.IsDesirableBinOp(opcode)
            ? Fold(LlvmConstantExpr.Get(opcode, a, b))
            : LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, a, b, layout, metrics));
    }

    public LlvmConstant? FoldExactBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right, bool exact)
    {
        Count("exact_binary.queries");
        if (left is not LlvmConstant a || right is not LlvmConstant b) return null;
        return Result("exact_binary", LlvmConstantExpr.IsDesirableBinOp(opcode)
            ? Fold(LlvmConstantExpr.Get(opcode, a, b, exact ? 1U : 0U))
            : LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, a, b, layout, metrics));
    }

    public LlvmConstant? FoldNoWrapBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right, bool nuw, bool nsw)
    {
        Count("nowrap_binary.queries");
        if (left is not LlvmConstant a || right is not LlvmConstant b) return null;
        uint flags = (nuw ? 1U : 0U) | (nsw ? 2U : 0U);
        return Result("nowrap_binary", LlvmConstantExpr.IsDesirableBinOp(opcode)
            ? Fold(LlvmConstantExpr.Get(opcode, a, b, flags))
            : LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, a, b, layout, metrics));
    }

    public LlvmConstant? FoldBinOpFMF(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmFastMathFlags flags) =>
        FoldBinOp(opcode, left, right);

    public LlvmConstant? FoldCmp(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
    {
        Count("compare.queries");
        return left is LlvmConstant a && right is LlvmConstant b
            ? Result("compare", LlvmConstantFolding.ConstantFoldCompareInstOperands(predicate, a, b, layout, metrics: metrics)) : null;
    }

    public LlvmConstant? FoldUnOpFMF(LlvmOpcode opcode, LlvmValue value, LlvmFastMathFlags flags)
    {
        Count("unary.queries");
        return value is LlvmConstant constant
            ? Result("unary", LlvmConstantFolding.ConstantFoldUnaryOpOperand(opcode, constant, layout, metrics)) : null;
    }

    public LlvmConstant? FoldGep(LlvmType type, LlvmValue pointer, IReadOnlyList<LlvmValue> indices, LlvmGepNoWrapFlags flags)
    {
        Count("gep.queries");
        if (!LlvmConstantExpr.IsSupportedGetElementPtr(type) || pointer is not LlvmConstant constant ||
            indices.Any(value => value is not LlvmConstant)) return null;
        var values = new LlvmConstant[indices.Count];
        for (int index = 0; index < values.Length; ++index) values[index] = (LlvmConstant)indices[index];
        LlvmConstant? expression = LlvmConstantExpr.GetGetElementPtr(layout, type, constant, values, flags);
        return Result("gep", expression is null ? null : Fold(expression));
    }

    public LlvmConstant? FoldSelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse, LlvmFastMathFlags flags)
    {
        Count("select.queries");
        return condition is LlvmConstant c && whenTrue is LlvmConstant a && whenFalse is LlvmConstant b
            ? Result("select", LlvmConstantFolding.ConstantFoldSelectInstruction(c, a, b, metrics)) : null;
    }

    public LlvmConstant? FoldExtractValue(LlvmValue aggregate, IReadOnlyList<uint> indices)
    {
        Count("extractvalue.queries");
        return aggregate is LlvmConstant constant
            ? Result("extractvalue", LlvmConstantFolding.ConstantFoldExtractValueInstruction(constant, indices, metrics)) : null;
    }

    public LlvmConstant? FoldInsertValue(LlvmValue aggregate, LlvmValue value, IReadOnlyList<uint> indices)
    {
        Count("insertvalue.queries");
        return aggregate is LlvmConstant a && value is LlvmConstant b
            ? Result("insertvalue", LlvmConstantFolding.ConstantFoldInsertValueInstruction(a, b, indices, metrics)) : null;
    }

    public LlvmConstant? FoldBitInsert(LlvmValue @base, LlvmValue value, LlvmValue offset)
    {
        Count("bitinsert.queries");
        return @base is LlvmConstant a && value is LlvmConstant b && offset is LlvmConstant c
            ? Result("bitinsert", LlvmConstantFolding.ConstantFoldBitInsertOperands(a, b, c, layout, metrics)) : null;
    }

    public LlvmConstant? FoldExtractElement(LlvmValue vector, LlvmValue index)
    {
        Count("extractelement.queries");
        return vector is LlvmConstant a && index is LlvmConstant b
            ? Result("extractelement", Fold(LlvmConstantExpr.GetExtractElement(a, b))) : null;
    }

    public LlvmConstant? FoldInsertElement(LlvmValue vector, LlvmValue element, LlvmValue index)
    {
        Count("insertelement.queries");
        return vector is LlvmConstant a && element is LlvmConstant b && index is LlvmConstant c
            ? Result("insertelement", Fold(LlvmConstantExpr.GetInsertElement(a, b, c))) : null;
    }

    public LlvmConstant? FoldShuffleVector(LlvmValue left, LlvmValue right, IReadOnlyList<int> mask)
    {
        Count("shufflevector.queries");
        return left is LlvmConstant a && right is LlvmConstant b
            ? Result("shufflevector", Fold(LlvmConstantExpr.GetShuffleVector(a, b, mask))) : null;
    }

    public LlvmConstant? FoldBitExtract(LlvmType type, LlvmValue source, LlvmValue offset)
    {
        Count("bitextract.queries");
        return source is LlvmConstant a && offset is LlvmConstant b
            ? Result("bitextract", LlvmConstantFolding.ConstantFoldBitExtractOperands(type, a, b, layout, metrics)) : null;
    }

    public LlvmConstant? FoldCast(LlvmOpcode opcode, LlvmValue value, LlvmType destination)
    {
        Count("cast.queries");
        return value is LlvmConstant constant
            ? Result("cast", LlvmConstantFolding.ConstantFoldCastOperand(opcode, constant, destination, layout, metrics)) : null;
    }

    public LlvmConstant? FoldIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> operands,
        LlvmType type, LlvmFastMathFlags flags = default, LlvmFunction? function = null)
    {
        Count("intrinsic.queries");
        if (operands.Any(value => value is not LlvmConstant)) return null;
        var values = new LlvmConstant[operands.Count];
        for (int index = 0; index < values.Length; ++index) values[index] = (LlvmConstant)operands[index];
        return Result("intrinsic", LlvmConstantFolding.ConstantFoldIntrinsic(kind, values, type, layout,
            contextFunction: function, metrics: metrics));
    }

    public LlvmConstant CreatePointerCast(LlvmConstant value, LlvmType destination)
    {
        Count("pointercast.queries");
        return ReferenceEquals(value.Type, destination) ? value : Fold(LlvmConstantExpr.GetPointerCast(value, destination));
    }

    public LlvmConstant CreatePointerBitCastOrAddrSpaceCast(LlvmConstant value, LlvmType destination)
    {
        Count("pointerbitcast.queries");
        return ReferenceEquals(value.Type, destination) ? value : Fold(LlvmConstantExpr.GetPointerBitCastOrAddrSpaceCast(value, destination));
    }

    private LlvmConstant? Result(string family, LlvmConstant? result)
    {
        Count(family + (result is null ? ".not_folded" : ".folded"));
        return result;
    }

    private void Count(string key)
    {
        if (metrics is null) return;
        key = "target_folder." + key;
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }
}
