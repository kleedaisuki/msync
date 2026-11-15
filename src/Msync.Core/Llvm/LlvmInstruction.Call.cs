// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/InstrTypes.h CallBase,
// IR/Instructions.h CallInst; lib/IR/Instructions.cpp populateBundleOperandInfos/init.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public enum LlvmTailCallKind : byte { None = 0, Tail = 1, MustTail = 2, NoTail = 3 }

public abstract partial class LlvmCallBase : LlvmInstruction
{
    private readonly LlvmOperandBundleUse[] operandBundleStorage;
    protected LlvmCallBase(LlvmFunctionType functionType, LlvmOpcode opcode, LlvmValue calledOperand,
        IReadOnlyList<LlvmValue> arguments) : this(functionType, opcode, calledOperand, arguments, null) { }

    protected LlvmCallBase(LlvmFunctionType functionType, LlvmOpcode opcode, LlvmValue calledOperand,
        IReadOnlyList<LlvmValue> arguments, IReadOnlyList<LlvmOperandBundleDef>? bundles)
        : base(functionType.ReturnType, opcode, arguments.Count + (bundles?.Sum(bundle => bundle.Inputs.Count) ?? 0) + 1)
    {
        Debug.Assert(arguments.Count == functionType.NumParams || functionType.IsVarArg && arguments.Count >= functionType.NumParams);
        FunctionType = functionType;
        ArgumentCount = arguments.Count;
        for (int index = 0; index < ArgumentCount; ++index) SetOperand(index, arguments[index]);
        CalledOperand = calledOperand;
        var views = new LlvmOperandBundleUse[bundles?.Count ?? 0];
        int current = ArgumentCount;
        for (int index = 0; index < views.Length; ++index)
        {
            LlvmOperandBundleDef bundle = bundles![index];
            int begin = current;
            foreach (LlvmValue input in bundle.Inputs) SetOperand(current++, input);
            views[index] = new LlvmOperandBundleUse(this, bundle.Tag, begin, current);
        }
        Debug.Assert(current + 1 == OperandCount);
        operandBundleStorage = views;
        OperandBundles = Array.AsReadOnly(operandBundleStorage);
    }

    protected LlvmCallBase(LlvmCallBase original) : base(original.Type, original.Opcode, original.OperandCount)
    {
        FunctionType = original.FunctionType;
        MemoryEffectsAttribute = original.MemoryEffectsAttribute;
        ArgumentCount = original.ArgumentCount;
        CallingConvention = original.CallingConvention;
        NoInline = original.NoInline;
        NoDuplicate = original.NoDuplicate;
        Convergent = original.Convergent;
        ReturnsTwice = original.ReturnsTwice;
        NoMerge = original.NoMerge;
        NoReturn = original.NoReturn;
        for (int index = 0; index < OperandCount; ++index) SetOperand(index, original.GetOperand(index));
        operandBundleStorage = original.OperandBundles.Select(bundle =>
            new LlvmOperandBundleUse(this, bundle.Tag, bundle.Begin, bundle.End)).ToArray();
        OperandBundles = Array.AsReadOnly(operandBundleStorage);
        CopyAdditionalAttributesFrom(original);
    }

    public LlvmFunctionType FunctionType { get; }
    public int ArgumentCount { get; }
    public LlvmValue GetArgument(int index)
    {
        Debug.Assert((uint)index < (uint)ArgumentCount);
        return GetOperand(index)!;
    }
    public void SetArgument(int index, LlvmValue value)
    {
        Debug.Assert((uint)index < (uint)ArgumentCount);
        SetOperand(index, value);
    }
    public IEnumerable<LlvmValue> Arguments => Enumerable.Range(0, ArgumentCount).Select(GetArgument);
    public LlvmValue CalledOperand { get => GetOperand(OperandCount - 1)!; set => SetOperand(OperandCount - 1, value); }
    public LlvmFunction? CalledFunction => CalledOperand as LlvmFunction;
    public uint CallingConvention { get; set; }
    public bool NoInline { get; set; }
    public bool NoDuplicate { get; set; }
    public bool Convergent { get; set; }
    public bool ReturnsTwice { get; set; }
    public bool NoMerge { get; set; }
    public bool NoReturn { get; set; }
    public bool IsNoInline => NoInline || CalledFunction?.NoInline == true;
    public bool CannotDuplicate => NoDuplicate || CalledFunction?.NoDuplicate == true;
    public bool IsConvergent => Convergent || CalledFunction?.Convergent == true;
    public bool HasReturnsTwice => ReturnsTwice || CalledFunction?.ReturnsTwice == true;
    public bool CannotMerge => NoMerge || CalledFunction?.NoMerge == true;
    public bool DoesNotReturn => NoReturn || CalledFunction?.NoReturn == true;
    public IReadOnlyList<LlvmOperandBundleUse> OperandBundles { get; }
    public int OperandBundleCount => OperandBundles.Count;
    public bool HasOperandBundles => OperandBundleCount != 0;
    public int BundleOperandCount => OperandCount - ArgumentCount - 1;
    public bool IsBundleOperand(int operandIndex) => operandIndex >= ArgumentCount && operandIndex < ArgumentCount + BundleOperandCount;
    public LlvmOperandBundleUse GetOperandBundleAt(int index) => OperandBundles[index];
    public int CountOperandBundlesOfType(string tag)
    {
        int count = 0;
        foreach (LlvmOperandBundleUse bundle in OperandBundles)
            if (string.Equals(bundle.Tag, tag, StringComparison.Ordinal)) ++count;
        return count;
    }
    public LlvmOperandBundleUse? GetOperandBundle(string tag)
    {
        Debug.Assert(CountOperandBundlesOfType(tag) < 2);
        foreach (LlvmOperandBundleUse bundle in OperandBundles)
            if (string.Equals(bundle.Tag, tag, StringComparison.Ordinal)) return bundle;
        return null;
    }
    public LlvmValue? GetConvergenceControlToken() => GetOperandBundle("convergencectrl")?.GetInput(0);
    public IReadOnlyList<LlvmOperandBundleDef> GetOperandBundlesAsDefs() =>
        OperandBundles.Select(bundle => new LlvmOperandBundleDef(bundle)).ToArray();
}

public sealed class LlvmCallInstruction : LlvmCallBase, ILlvmFastMathInstruction
{
    public LlvmCallInstruction(LlvmFunctionType functionType, LlvmValue calledOperand, IReadOnlyList<LlvmValue> arguments)
        : this(functionType, calledOperand, arguments, null) { }
    public LlvmCallInstruction(LlvmFunctionType functionType, LlvmValue calledOperand, IReadOnlyList<LlvmValue> arguments,
        IReadOnlyList<LlvmOperandBundleDef>? bundles = null)
        : base(functionType, LlvmOpcode.Call, calledOperand, arguments, bundles) { }
    private LlvmCallInstruction(LlvmCallInstruction original) : base(original)
    {
        TailCallKind = original.TailCallKind;
        FastMathFlags = original.FastMathFlags;
    }
    public LlvmTailCallKind TailCallKind { get; set; }
    public LlvmFastMathFlags FastMathFlags { get; set; }
    protected override LlvmInstruction CloneImpl() => new LlvmCallInstruction(this);
}
