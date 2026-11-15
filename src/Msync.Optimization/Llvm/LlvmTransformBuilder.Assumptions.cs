// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.cpp
// CreateAssumption (572), alignment/dereferenceable/nonnull/range helpers (1390–1444).
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public sealed partial class LlvmTransformBuilder
{
    public LlvmCallInstruction CreateAssumption(IReadOnlyList<LlvmOperandBundleDef> bundles) =>
        CreateAssumption(LlvmConstantInt.GetTrue(InsertBlock!.Context), bundles);
    public LlvmCallInstruction CreateAssumption(LlvmOperandBundleDef bundle) => CreateAssumption([bundle]);
    public LlvmCallInstruction CreateAlignmentAssumption(LlvmDataLayout dataLayout, LlvmValue pointer, ulong alignment, LlvmValue? offset = null)
    {
        Debug.Assert(pointer.Type.IsPointerTy() && alignment != 0);
        return CreateAlignmentAssumption(dataLayout, pointer, LlvmConstantInt.Get(LlvmType.GetIntNTy(pointer.Context, 64), alignment), offset);
    }
    public LlvmCallInstruction CreateAlignmentAssumption(LlvmDataLayout dataLayout, LlvmValue pointer, LlvmValue alignment, LlvmValue? offset = null)
    {
        Debug.Assert(pointer.Type.IsPointerTy());
        Count("transform_builder.assumption.align");
        return CreateAssumption(new LlvmOperandBundleDef("align", offset is null ? [pointer, alignment] : [pointer, alignment, offset]));
    }
    public LlvmCallInstruction CreateDereferenceableAssumption(LlvmValue pointer, LlvmValue size)
    {
        Debug.Assert(pointer.Type.IsPointerTy()); Count("transform_builder.assumption.dereferenceable");
        return CreateAssumption(new LlvmOperandBundleDef("dereferenceable", [pointer, size]));
    }
    public LlvmCallInstruction CreateNonnullAssumption(LlvmValue pointer)
    {
        Debug.Assert(pointer.Type.IsPointerTy()); Count("transform_builder.assumption.nonnull");
        return CreateAssumption(new LlvmOperandBundleDef("nonnull", [pointer]));
    }
    public LlvmCallInstruction CreateRangeAssumption(LlvmValue value, LlvmConstantRange range)
    {
        Debug.Assert(value.Type.IsIntegerTy() && !range.IsFullSet); Count("transform_builder.assumption.range");
        return CreateAssumption(new LlvmOperandBundleDef("range", [value,
            LlvmConstantInt.Get(value.Context, range.Lower), LlvmConstantInt.Get(value.Context, range.Upper)]));
    }
}
