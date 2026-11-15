// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.h/.cpp and
// ConstantFolder.h scalar FoldBinOp/FoldCmp/FoldSelect/FoldCast and aggregate folders.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    private static bool IsFloatingBinary(LlvmOpcode opcode) => opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem;

    public LlvmValue CreateBinary(LlvmOpcode opcode, LlvmValue left, LlvmValue right, string name = "",
        bool nuw = false, bool nsw = false, bool exact = false, LlvmFastMathFlags? fastMathFlags = null)
    {
        Count("transform_builder.binary.queries");
        if (FoldConstants)
        {
            LlvmConstant? folded = TargetFolder is { } folder
                ? opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl
                    ? folder.FoldNoWrapBinOp(opcode, left, right, nuw, nsw)
                    : opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.LShr or LlvmOpcode.AShr
                        ? folder.FoldExactBinOp(opcode, left, right, exact)
                        : IsFloatingBinary(opcode) ? folder.FoldBinOpFMF(opcode, left, right, fastMathFlags ?? FastMathFlags)
                        : folder.FoldBinOp(opcode, left, right)
                : left is LlvmConstant a && right is LlvmConstant b
                    ? LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, a, b, Layout, Metrics) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmBinaryOperator(opcode, left, right)
        { HasNoUnsignedWrap = nuw, HasNoSignedWrap = nsw, IsExact = exact, FastMathFlags = IsFloatingBinary(opcode) ? fastMathFlags ?? FastMathFlags : LlvmFastMathFlags.None }, name);
    }
    public LlvmValue CreateBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right, string name = "") => CreateBinary(opcode, left, right, name);
    public LlvmValue CreateAdd(LlvmValue left, LlvmValue right, string name = "", bool nuw = false, bool nsw = false) => CreateBinary(LlvmOpcode.Add, left, right, name, nuw, nsw);
    public LlvmValue CreateSub(LlvmValue left, LlvmValue right, string name = "", bool nuw = false, bool nsw = false) => CreateBinary(LlvmOpcode.Sub, left, right, name, nuw, nsw);
    public LlvmValue CreateMul(LlvmValue left, LlvmValue right, string name = "", bool nuw = false, bool nsw = false) => CreateBinary(LlvmOpcode.Mul, left, right, name, nuw, nsw);
    public LlvmValue CreateAnd(LlvmValue left, LlvmValue right, string name = "") => CreateBinary(LlvmOpcode.And, left, right, name);
    public LlvmValue CreateOr(LlvmValue left, LlvmValue right, string name = "") => CreateBinary(LlvmOpcode.Or, left, right, name);
    public LlvmValue CreateXor(LlvmValue left, LlvmValue right, string name = "") => CreateBinary(LlvmOpcode.Xor, left, right, name);
    public LlvmValue CreateFNeg(LlvmValue value, string name = "", LlvmFastMathFlags? fastMathFlags = null)
    {
        if (FoldConstants)
        {
            LlvmConstant? folded = TargetFolder is { } folder
                ? folder.FoldUnOpFMF(LlvmOpcode.FNeg, value, fastMathFlags ?? FastMathFlags)
                : value is LlvmConstant constant ? LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, constant, Layout, Metrics) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmUnaryOperator(value) { FastMathFlags = fastMathFlags ?? FastMathFlags }, name);
    }
    public LlvmValue CreateNot(LlvmValue value, string name = "") => CreateXor(value, LlvmConstant.GetAllOnesValue(value.Type), name);
    public LlvmValue CreateNeg(LlvmValue value, string name = "", bool nuw = false, bool nsw = false) => CreateSub(LlvmConstant.GetNullValue(value.Type), value, name, nuw, nsw);
    public LlvmValue CreateLogicalAnd(LlvmValue left, LlvmValue right, string name = "") => CreateSelect(left, right, LlvmConstant.GetNullValue(right.Type), name);
    public LlvmValue CreateLogicalOr(LlvmValue left, LlvmValue right, string name = "") => CreateSelect(left, LlvmConstantInt.Get(right.Context, LlvmAPInt.GetAllOnes(right.Type.GetIntegerBitWidth())), right, name);

    public LlvmValue CreateICmp(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right, string name = "", bool sameSign = false)
    {
        Count("transform_builder.icmp.queries");
        if (FoldComparison(predicate, left, right) is { } folded) return Folded(folded);
        return Insert(new LlvmICmpInstruction(predicate, left, right) { SameSign = sameSign }, name);
    }
    public LlvmValue CreateFCmp(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right, string name = "", LlvmFastMathFlags? fastMathFlags = null)
    {
        Count("transform_builder.fcmp.queries");
        if (FoldComparison(predicate, left, right) is { } folded) return Folded(folded);
        return Insert(new LlvmFCmpInstruction(predicate, left, right) { FastMathFlags = fastMathFlags ?? FastMathFlags }, name);
    }
    public LlvmValue CreateSelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse, string name = "", LlvmFastMathFlags? fastMathFlags = null)
    {
        Count("transform_builder.select.queries");
        if (FoldConstants)
        {
            LlvmConstant? folded = TargetFolder is { } folder
                ? folder.FoldSelect(condition, whenTrue, whenFalse, fastMathFlags ?? FastMathFlags)
                : condition is LlvmConstant c && whenTrue is LlvmConstant a && whenFalse is LlvmConstant b
                    ? LlvmConstantFolding.ConstantFoldSelectInstruction(c, a, b, Metrics) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmSelectInstruction(condition, whenTrue, whenFalse) { FastMathFlags = whenTrue.Type.IsFPOrFPVectorTy() ? fastMathFlags ?? FastMathFlags : LlvmFastMathFlags.None }, name);
    }
    public LlvmValue CreateCast(LlvmOpcode opcode, LlvmValue value, LlvmType destination, string name = "")
    {
        if (ReferenceEquals(value.Type, destination)) return value;
        Count("transform_builder.cast.queries");
        if (FoldConstants)
        {
            LlvmConstant? folded = TargetFolder is { } folder ? folder.FoldCast(opcode, value, destination)
                : value is LlvmConstant constant ? LlvmConstantFolding.ConstantFoldCastOperand(opcode, constant, destination, Layout, Metrics) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmCastInstruction(opcode, value, destination) { FastMathFlags = opcode is LlvmOpcode.FPExt or LlvmOpcode.FPTrunc or LlvmOpcode.SIToFP or LlvmOpcode.UIToFP ? FastMathFlags : LlvmFastMathFlags.None }, name);
    }
    public LlvmValue CreateBitCast(LlvmValue value, LlvmType destination, string name = "") => CreateCast(LlvmOpcode.BitCast, value, destination, name);
    public LlvmValue CreatePtrToInt(LlvmValue value, LlvmType destination, string name = "") => CreateCast(LlvmOpcode.PtrToInt, value, destination, name);
    public LlvmValue CreateIntToPtr(LlvmValue value, LlvmType destination, string name = "") => CreateCast(LlvmOpcode.IntToPtr, value, destination, name);
    public LlvmValue CreateZExt(LlvmValue value, LlvmType destination, string name = "") => CreateCast(LlvmOpcode.ZExt, value, destination, name);
    public LlvmValue CreateSExt(LlvmValue value, LlvmType destination, string name = "") => CreateCast(LlvmOpcode.SExt, value, destination, name);
    public LlvmValue CreateTrunc(LlvmValue value, LlvmType destination, string name = "") => CreateCast(LlvmOpcode.Trunc, value, destination, name);
    public LlvmValue CreateZExtOrTrunc(LlvmValue value, LlvmType destination, string name = "") =>
        CreateCast(value.Type.GetScalarSizeInBits() < destination.GetScalarSizeInBits() ? LlvmOpcode.ZExt : LlvmOpcode.Trunc, value, destination, name);
    public LlvmValue CreateSExtOrTrunc(LlvmValue value, LlvmType destination, string name = "") =>
        CreateCast(value.Type.GetScalarSizeInBits() < destination.GetScalarSizeInBits() ? LlvmOpcode.SExt : LlvmOpcode.Trunc, value, destination, name);
    public LlvmValue CreateIntCast(LlvmValue value, LlvmType destination, bool isSigned, string name = "") =>
        isSigned ? CreateSExtOrTrunc(value, destination, name) : CreateZExtOrTrunc(value, destination, name);
    public LlvmValue CreateExtractValue(LlvmValue aggregate, uint index, string name = "") => CreateExtractValue(aggregate, [index], name);
    public LlvmValue CreateInsertValue(LlvmValue aggregate, LlvmValue value, uint index, string name = "") => CreateInsertValue(aggregate, value, [index], name);
    public LlvmValue CreateExtractValue(LlvmValue aggregate, IReadOnlyList<uint> indices, string name = "")
    {
        if (FoldConstants)
        {
            LlvmConstant? folded = TargetFolder is { } folder ? folder.FoldExtractValue(aggregate, indices)
                : aggregate is LlvmConstant constant ? LlvmConstantFolding.ConstantFoldExtractValueInstruction(constant, indices, Metrics) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmExtractValueInstruction(aggregate, indices), name);
    }
    public LlvmValue CreateInsertValue(LlvmValue aggregate, LlvmValue value, IReadOnlyList<uint> indices, string name = "")
    {
        if (FoldConstants)
        {
            LlvmConstant? folded = TargetFolder is { } folder ? folder.FoldInsertValue(aggregate, value, indices)
                : aggregate is LlvmConstant a && value is LlvmConstant b
                    ? LlvmConstantFolding.ConstantFoldInsertValueInstruction(a, b, indices, Metrics) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmInsertValueInstruction(aggregate, value, indices), name);
    }

    private LlvmConstant? FoldComparison(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
    {
        if (!FoldConstants) return null;
        if (TargetFolder is { } folder) return folder.FoldCmp(predicate, left, right);
        return left is LlvmConstant a && right is LlvmConstant b
            ? LlvmConstantFolding.ConstantFoldCompareInstruction(predicate, a, b) : null;
    }
}
