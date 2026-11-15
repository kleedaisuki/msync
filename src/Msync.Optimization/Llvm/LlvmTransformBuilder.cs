// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.h/.cpp,
// IRBuilderDefaultInserter and IRBuilder<ConstantFolder> insertion-point handling.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    private LlvmBasicBlock? block;
    private LlvmInstruction? before;
    private readonly Action<LlvmInstruction>? onInsert;
    private readonly LlvmDataLayout layout;
    private LlvmContext? context;
    public LlvmDataLayout Layout => InsertBlock?.Module?.DataLayout ?? layout;
    public bool FoldConstants { get; }
    public LlvmTargetFolder? TargetFolder { get; }
    public Dictionary<string, ulong>? Metrics { get; }
    public SourceSpan? CurrentSource { get; set; }
    public LlvmFastMathFlags FastMathFlags { get; set; }
    public LlvmBasicBlock? InsertBlock => before is null ? block : before.Parent;
    public LlvmInstruction? InsertBefore => before;
    public bool CanonicalizeGeps { get; private set; } = true;
    public readonly record struct InsertionPoint(LlvmBasicBlock? Block, LlvmInstruction? Before);

    public LlvmTransformBuilder(LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null,
        Action<LlvmInstruction>? onInsert = null, bool foldConstants = true, LlvmTargetFolder? targetFolder = null) =>
        (this.layout, Metrics, this.onInsert, FoldConstants, TargetFolder) = (layout, metrics, onInsert, foldConstants, targetFolder);
    public LlvmTransformBuilder(LlvmContext context, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null,
        Action<LlvmInstruction>? onInsert = null, bool foldConstants = true, LlvmTargetFolder? targetFolder = null) :
        this(layout, metrics, onInsert, foldConstants, targetFolder) => this.context = context;
    public LlvmTransformBuilder(LlvmInstruction beforeInstruction, Dictionary<string, ulong>? metrics = null,
        Action<LlvmInstruction>? onInsert = null, bool foldConstants = true, LlvmTargetFolder? targetFolder = null) :
        this(beforeInstruction.Parent!.Module!.DataLayout, metrics, onInsert, foldConstants, targetFolder) => SetInsertPoint(beforeInstruction);
    public LlvmTransformBuilder(LlvmBasicBlock block, Dictionary<string, ulong>? metrics = null,
        Action<LlvmInstruction>? onInsert = null, bool foldConstants = true, LlvmTargetFolder? targetFolder = null) :
        this(block.Module!.DataLayout, metrics, onInsert, foldConstants, targetFolder) => SetInsertPoint(block);

    public void SetInsertPoint(LlvmInstruction instruction)
    { block = instruction.Parent!; before = instruction; context ??= instruction.Context; CurrentSource = instruction.Source; }
    public void SetInsertPoint(LlvmBasicBlock insertionBlock) { block = insertionBlock; before = null; context ??= insertionBlock.Context; }
    public void SetInsertPoint(LlvmBasicBlock insertionBlock, LlvmInstruction? instruction)
    { if (instruction is null) SetInsertPoint(insertionBlock); else SetInsertPoint(instruction); }
    public void ClearInsertionPoint() { block = null; before = null; }
    public InsertionPoint SaveInsertionPoint() => new(InsertBlock, before);
    public void RestoreInsertionPoint(InsertionPoint point)
    { if (point.Block is null) ClearInsertionPoint(); else SetInsertPoint(point.Block, point.Before); }
    public void DisableGepCanonicalization() => CanonicalizeGeps = false;
    public void DisableGEPCanonicalization() => DisableGepCanonicalization();

    public T Insert<T>(T instruction, string name = "") where T : LlvmInstruction
    {
        Debug.Assert(instruction.Parent is null);
        instruction.SetName(name);
        InsertBlock?.InsertBefore(instruction, before);
        instruction.Source = CurrentSource ?? instruction.Source;
        onInsert?.Invoke(instruction);
        if (Metrics is not null)
        {
            string key = "transform_builder.insert." + instruction.Opcode;
            Metrics[key] = Metrics.GetValueOrDefault(key) + 1;
        }
        return instruction;
    }
    public LlvmValue Insert(LlvmValue value, string name = "")
    {
        if (value is LlvmInstruction instruction) return Insert(instruction, name);
        Debug.Assert(value is LlvmConstant);
        return value;
    }
    private LlvmValue Folded(LlvmConstant value)
    { Count("transform_builder.constant_folded"); return value; }
    private void Count(string name)
    { if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1; }

    public LlvmUncondBrInstruction CreateBr(LlvmBasicBlock destination) => Insert(new LlvmUncondBrInstruction(destination));
    public LlvmCondBrInstruction CreateCondBr(LlvmValue condition, LlvmBasicBlock whenTrue, LlvmBasicBlock whenFalse) =>
        Insert(new LlvmCondBrInstruction(condition, whenTrue, whenFalse));
    public LlvmSwitchInstruction CreateSwitch(LlvmValue condition, LlvmBasicBlock defaultBlock, int reservedCases = 0) =>
        Insert(new LlvmSwitchInstruction(condition, defaultBlock, reservedCases));
    public LlvmReturnInstruction CreateRet(LlvmValue value) => Insert(new LlvmReturnInstruction(value.Context, value));
    public LlvmReturnInstruction CreateRetVoid() => Insert(new LlvmReturnInstruction(context!));
    public LlvmUnreachableInstruction CreateUnreachable() => Insert(new LlvmUnreachableInstruction(context!));
    public LlvmPhiInstruction CreatePhi(LlvmType type, int reservedValues = 0, string name = "") =>
        Insert(new LlvmPhiInstruction(type, reservedValues), name);
    public LlvmPhiInstruction CreatePHI(LlvmType type, int reservedValues = 0, string name = "") => CreatePhi(type, reservedValues, name);
    public LlvmFreezeInstruction CreateFreeze(LlvmValue value, string name = "") => Insert(new LlvmFreezeInstruction(value), name);
    public LlvmValue CreateBinaryIntrinsic(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right, string name = "")
    {
        LlvmFunction declaration = left.Type is LlvmIntegerType integer
            ? LlvmIntrinsic.GetDeclaration(InsertBlock!.Module!, kind, integer, Metrics)
            : LlvmIntrinsic.GetDeclaration(InsertBlock!.Module!, kind, left.Type, Metrics);
        if (FoldConstants && TargetFolder?.FoldIntrinsic(kind, [left, right], declaration.ReturnType,
            FastMathFlags, InsertBlock.Parent) is { } folded) return Folded(folded);
        var call = new LlvmCallInstruction(declaration.FunctionType, declaration, [left, right]);
        if (call.Type.IsFPOrFPVectorTy()) call.FastMathFlags = FastMathFlags;
        return Insert(call, name);
    }
}
