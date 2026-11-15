// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.h/.cpp,
// ConstantFolder.h and Analysis/TargetFolder.h vector folders.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmValue CreateExtractElement(LlvmValue vector, LlvmValue index, string name = "")
    {
        Count("transform_builder.extractelement.queries");
        if (FoldConstants)
        {
            var folded = TargetFolder is not null ? TargetFolder.FoldExtractElement(vector, index)
                : vector is LlvmConstant constant && index is LlvmConstant position ? LlvmConstantExpr.GetExtractElement(constant, position) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmExtractElementInstruction(vector, index), name);
    }
    public LlvmValue CreateExtractElement(LlvmValue vector, ulong index, string name = "") =>
        CreateExtractElement(vector, LlvmConstantInt.Get(LlvmIntegerType.Get(vector.Context, 64), index), name);

    public LlvmValue CreateInsertElement(LlvmValue vector, LlvmValue element, LlvmValue index, string name = "")
    {
        Count("transform_builder.insertelement.queries");
        if (FoldConstants)
        {
            var folded = TargetFolder is not null ? TargetFolder.FoldInsertElement(vector, element, index)
                : vector is LlvmConstant constant && element is LlvmConstant value && index is LlvmConstant position
                    ? LlvmConstantExpr.GetInsertElement(constant, value, position) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmInsertElementInstruction(vector, element, index), name);
    }
    public LlvmValue CreateInsertElement(LlvmValue vector, LlvmValue element, ulong index, string name = "") =>
        CreateInsertElement(vector, element, LlvmConstantInt.Get(LlvmIntegerType.Get(vector.Context, 64), index), name);
    public LlvmValue CreateInsertElement(LlvmType vectorType, LlvmValue element, LlvmValue index, string name = "") =>
        CreateInsertElement(LlvmPoisonValue.Get(vectorType), element, index, name);
    public LlvmValue CreateInsertElement(LlvmType vectorType, LlvmValue element, ulong index, string name = "") =>
        CreateInsertElement(LlvmPoisonValue.Get(vectorType), element, index, name);

    public LlvmValue CreateShuffleVector(LlvmValue left, LlvmValue right, IReadOnlyList<int> mask, string name = "")
    {
        Count("transform_builder.shufflevector.queries");
        if (FoldConstants)
        {
            var folded = TargetFolder is not null ? TargetFolder.FoldShuffleVector(left, right, mask)
                : left is LlvmConstant a && right is LlvmConstant b ? LlvmConstantExpr.GetShuffleVector(a, b, mask) : null;
            if (folded is not null) return Folded(folded);
        }
        return Insert(new LlvmShuffleVectorInstruction(left, right, mask), name);
    }
    public LlvmValue CreateShuffleVector(LlvmValue left, LlvmValue right, LlvmValue mask, string name = "") =>
        CreateShuffleVector(left, right, LlvmShuffleVectorInstruction.GetShuffleMask((LlvmConstant)mask), name);
    public LlvmValue CreateShuffleVector(LlvmValue vector, IReadOnlyList<int> mask, string name = "") =>
        CreateShuffleVector(vector, LlvmPoisonValue.Get(vector.Type), mask, name);

    public LlvmValue CreateVectorSplat(uint elements, LlvmValue value, string name = "") =>
        CreateVectorSplat(LlvmElementCount.Fixed(elements), value, name);
    public LlvmValue CreateVectorSplat(LlvmElementCount count, LlvmValue value, string name = "")
    {
        Debug.Assert(count.KnownMinValue != 0);
        var poison = LlvmPoisonValue.Get(LlvmVectorType.Get(value.Type, count));
        value = CreateInsertElement(poison, value, 0UL, name + ".splatinsert");
        return CreateShuffleVector(value, new int[count.KnownMinValue], name + ".splat");
    }
}
