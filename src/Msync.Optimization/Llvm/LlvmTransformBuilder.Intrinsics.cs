// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder CreateIntrinsic,
// ConstantFolder::FoldIntrinsic returns nullptr (not TargetFolder).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmCallInstruction CreateAssumption(LlvmValue condition, IReadOnlyList<LlvmOperandBundleDef>? bundles = null) =>
        Insert(LlvmIntrinsic.CreateAssumption(condition, InsertBlock!.Module!, bundles, Metrics));
    public LlvmValue CreateIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments, string name = "") =>
        LlvmIntrinsic.IsMemoryFamilyIntrinsic(kind) ? Insert(LlvmIntrinsic.CreateMemoryIntrinsic(kind, arguments, InsertBlock!.Module!, Metrics), name) :
        CreateIntrinsic(kind, arguments, kind == LlvmIntrinsicKind.Assume ? LlvmType.GetVoidTy(arguments[0].Context)
            : LlvmIntrinsic.IsFloatingIntrinsic(kind) ? LlvmIntrinsic.GetFloatingIntrinsicReturnType(kind, arguments[0].Type) : arguments[0].Type, name);
    public LlvmValue CreateIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        LlvmIntegerType resultType, string name = "") => CreateIntrinsic(kind, arguments, (LlvmType)resultType, name);
    public LlvmValue CreateIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        LlvmType resultType, string name = "")
    {
        if (FoldConstants && TargetFolder?.FoldIntrinsic(kind, arguments, resultType,
            FastMathFlags, InsertBlock!.Parent) is { } folded) return Folded(folded);
        LlvmCallInstruction call = LlvmIntrinsic.CreateIntrinsic(kind, arguments, resultType, InsertBlock!.Module!, Metrics);
        if (resultType.IsFPOrFPVectorTy()) call.FastMathFlags = FastMathFlags;
        return Insert(call, name);
    }
}
