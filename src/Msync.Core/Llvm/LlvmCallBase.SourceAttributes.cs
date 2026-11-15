// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/InstrTypes.h, Instructions.cpp.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public abstract partial class LlvmCallBase
{
    private Dictionary<int, LlvmParameterAttributes>? parameterAttributes;
    public LlvmParameterAttributes ReturnAttributes { get; set; }
    public bool NoCreateUndefOrPoison { get; set; }
    public bool HasNoCreateUndefOrPoison => NoCreateUndefOrPoison || CalledFunction?.HasNoCreateUndefOrPoison == true;
    public bool NoUnwind { get; set; }
    public bool StrictFP { get; set; }
    public bool NoImplicitFloat { get; set; }
    public bool NoBuiltin { get; set; }
    public bool WillReturnAttribute { get; set; }
    public bool NoFree { get; set; }
    public bool NoSync { get; set; }
    public bool Speculatable { get; set; }
    public bool DoesNotThrow => NoUnwind || CalledFunction?.DoesNotThrow == true;
    public bool HasWillReturn => WillReturnAttribute || CalledFunction?.HasWillReturn == true;
    public bool DoesNotFreeMemory => NoFree || CalledFunction?.DoesNotFreeMemory == true;
    public bool DoesNotSync => NoSync || CalledFunction?.DoesNotSync == true;
    public bool IsSpeculatable => Speculatable || CalledFunction?.IsSpeculatable == true;
    public bool IsStrictFP => StrictFP || CalledFunction?.StrictFP == true;
    public bool IsNoBuiltin => NoBuiltin || CalledFunction?.NoBuiltin == true;
    public LlvmParameterAttributes GetParamAttributes(int index) => parameterAttributes?.GetValueOrDefault(index) ?? default;
    public LlvmParameterAttributes GetOwnParamAttributes(int index) => GetParamAttributes(index);
    public void SetParamAttributes(int index, LlvmParameterAttributes attributes)
    {
        Debug.Assert((uint)index < (uint)ArgumentCount);
        if (attributes.IsEmpty) { parameterAttributes?.Remove(index); return; }
        (parameterAttributes ??= [])[index] = attributes;
    }
    public void AddParamAttr(int index, LlvmAttributeKind attribute) => SetParamAttributes(index, GetParamAttributes(index).Add(attribute));
    public void RemoveParamAttr(int index, LlvmAttributeKind attribute) => SetParamAttributes(index, GetParamAttributes(index).Remove(attribute));
    public bool ParamHasAttr(int index, LlvmAttributeKind attribute)
    {
        if (GetParamAttributes(index).Has(attribute)) return true;
        if (CalledFunction?.ParamHasAttr(index, attribute) != true) return false;
        return attribute switch
        {
            LlvmAttributeKind.ReadNone => !LlvmIntrinsic.HasReadingOperandBundles(this) && !LlvmIntrinsic.HasClobberingOperandBundles(this),
            LlvmAttributeKind.ReadOnly => !LlvmIntrinsic.HasClobberingOperandBundles(this),
            LlvmAttributeKind.WriteOnly => !LlvmIntrinsic.HasReadingOperandBundles(this),
            _ => true
        };
    }
    public bool HasRetAttr(LlvmAttributeKind attribute) => ReturnAttributes.Has(attribute) || CalledFunction?.HasRetAttr(attribute) == true;
    public bool IsCallee(LlvmUse use) => ReferenceEquals(use.User, this) && use.OperandNo == OperandCount - 1;
    public bool IsArgOperand(LlvmUse use) => ReferenceEquals(use.User, this) && use.OperandNo < ArgumentCount;
    public bool IsDataOperand(LlvmUse use) => ReferenceEquals(use.User, this) && use.OperandNo < OperandCount - 1;
    public int GetDataOperandNo(LlvmUse use) { Debug.Assert(IsDataOperand(use)); return use.OperandNo; }
    public int GetArgOperandNo(LlvmUse use) { Debug.Assert(IsArgOperand(use)); return use.OperandNo; }
    public bool IsInAllocaArgument(int argument) => ParamHasAttr(argument, LlvmAttributeKind.InAlloca);
    public bool IsByValArgument(int argument) => ParamHasAttr(argument, LlvmAttributeKind.ByVal);
    public bool DataOperandHasImpliedAttr(int operand, LlvmAttributeKind attribute)
    {
        Debug.Assert((uint)operand < (uint)(OperandCount - 1));
        if (operand < ArgumentCount) return ParamHasAttr(operand, attribute);
        return attribute == LlvmAttributeKind.ReadOnly && GetOperand(operand)!.Type.IsPointerTy() && BundleTagForOperand(operand) == "deopt";
    }
    public LlvmCaptureInfo GetCaptureInfo(int operand)
    {
        Debug.Assert((uint)operand < (uint)(OperandCount - 1));
        if (operand >= ArgumentCount)
            return CalledFunction?.IntrinsicDescriptor?.IsAssume == true || BundleTagForOperand(operand) == "deopt"
                ? LlvmCaptureInfo.None() : LlvmCaptureInfo.All();
        if (IsByValArgument(operand)) return LlvmCaptureInfo.None();
        return (GetParamAttributes(operand).CaptureInfo ?? LlvmCaptureInfo.All()) &
            (CalledFunction?.GetParamCaptureInfo(operand) ?? LlvmCaptureInfo.All());
    }
    public bool HasArgumentWithAdditionalReturnCaptureComponents()
    {
        for (int index = 0; index < ArgumentCount; ++index)
        {
            if (!GetArgument(index).Type.IsPointerTy()) continue;
            var captures = (GetParamAttributes(index).CaptureInfo ?? LlvmCaptureInfo.All()) &
                (CalledFunction?.GetParamCaptureInfo(index) ?? LlvmCaptureInfo.All());
            if ((captures.RetComponents & ~captures.OtherComponents) != LlvmCaptureComponents.None) return true;
        }
        return false;
    }
    public LlvmCaptureInfo GetParamCaptureInfo(int argument) => GetCaptureInfo(argument);
    public bool DoesNotCapture(int operand) => GetCaptureInfo(operand).CapturesNothing;
    public bool DoesNotAccessMemory(int operand) => DataOperandHasImpliedAttr(operand, LlvmAttributeKind.ReadNone);
    public bool OnlyReadsMemory(int operand) => operand < ArgumentCount && IsByValArgument(operand) ||
        DataOperandHasImpliedAttr(operand, LlvmAttributeKind.ReadOnly) || DoesNotAccessMemory(operand);
    public bool OnlyWritesMemory(int operand) => DataOperandHasImpliedAttr(operand, LlvmAttributeKind.WriteOnly) || DoesNotAccessMemory(operand);
    public bool DoesNotAccessMemory() => GetMemoryEffects().Raw == 0;
    public bool OnlyReadsMemory() => (GetMemoryEffects().Raw & LlvmMemoryEffectsAttribute.WriteRaw) == 0;
    public bool OnlyWritesMemory() => (GetMemoryEffects().Raw & LlvmMemoryEffectsAttribute.ReadRaw) == 0;
    public LlvmAlign? GetParamAlign(int argument) => GetParamAttributes(argument).Alignment;
    public void SetParamAlign(int argument, LlvmAlign? alignment) => SetParamAttributes(argument, GetParamAttributes(argument) with { Alignment = alignment });
    public LlvmAlign? GetRetAlign() => ReturnAttributes.Alignment ?? CalledFunction?.ReturnAttributes.Alignment;
    public ulong GetParamDereferenceableBytes(int argument) => GetParamAttributes(argument).DereferenceableBytes ?? 0;
    public ulong GetParamDereferenceableOrNullBytes(int argument) => GetParamAttributes(argument).DereferenceableOrNullBytes ?? 0;
    public ulong GetRetDereferenceableBytes() => Math.Max(ReturnAttributes.DereferenceableBytes ?? 0, CalledFunction?.ReturnAttributes.DereferenceableBytes ?? 0);
    public ulong GetRetDereferenceableOrNullBytes() => Math.Max(ReturnAttributes.DereferenceableOrNullBytes ?? 0, CalledFunction?.ReturnAttributes.DereferenceableOrNullBytes ?? 0);
    public LlvmFPClassTest GetParamNoFPClass(int argument) => GetParamAttributes(argument).NoFPClass | (CalledFunction?.GetParamAttributes(argument).NoFPClass ?? LlvmFPClassTest.None);
    public LlvmFPClassTest GetRetNoFPClass() => ReturnAttributes.NoFPClass | (CalledFunction?.ReturnAttributes.NoFPClass ?? LlvmFPClassTest.None);
    public LlvmValue? GetReturnedArgOperand()
    {
        for (int index = 0; index < ArgumentCount; ++index)
            if (GetParamAttributes(index).Returned) return GetArgument(index);
        for (int index = 0; index < ArgumentCount; ++index)
            if (CalledFunction?.GetParamAttributes(index).Returned == true) return GetArgument(index);
        return null;
    }
    public bool ReturnDoesNotAlias() => ReturnAttributes.Has(LlvmAttributeKind.NoAlias);
    public LlvmConstantRange? GetRange()
    {
        var own = ReturnAttributes.Range?.ToConstantRange();
        var declared = CalledFunction?.ReturnAttributes.Range?.ToConstantRange();
        return own is null ? declared : declared is null ? own : own.IntersectWith(declared);
    }
    public void AddRangeRetAttr(LlvmRangeAttribute range) => ReturnAttributes = ReturnAttributes with { Range = range };
    public void AddRangeRetAttr(LlvmAPInt lower, LlvmAPInt upper) => AddRangeRetAttr(new(lower, upper));
    public void CopyAttributesFrom(LlvmCallBase original)
    {
        MemoryEffectsAttribute = original.MemoryEffectsAttribute;
        CallingConvention = original.CallingConvention;
        NoInline = original.NoInline; NoDuplicate = original.NoDuplicate; Convergent = original.Convergent;
        ReturnsTwice = original.ReturnsTwice; NoMerge = original.NoMerge; NoReturn = original.NoReturn;
        CopyAdditionalAttributesFrom(original);
    }
    private void CopyAdditionalAttributesFrom(LlvmCallBase original)
    {
        parameterAttributes = original.parameterAttributes is null ? null : new(original.parameterAttributes);
        ReturnAttributes = original.ReturnAttributes;
        NoCreateUndefOrPoison = original.NoCreateUndefOrPoison;
        NoUnwind = original.NoUnwind; StrictFP = original.StrictFP; NoImplicitFloat = original.NoImplicitFloat;
        NoBuiltin = original.NoBuiltin; WillReturnAttribute = original.WillReturnAttribute;
        NoFree = original.NoFree; NoSync = original.NoSync; Speculatable = original.Speculatable;
    }
    private string BundleTagForOperand(int operand)
    {
        foreach (var bundle in OperandBundles)
            if (operand >= bundle.Begin && operand < bundle.End) return bundle.Tag;
        throw new InvalidOperationException("Operand is not a bundle input.");
    }
    internal void SetBundleTagForOperand(int operand, string tag)
    {
        for (int index = 0; index < operandBundleStorage.Length; ++index)
        {
            var bundle = operandBundleStorage[index];
            if (operand >= bundle.Begin && operand < bundle.End)
            { operandBundleStorage[index] = new(this, tag, bundle.Begin, bundle.End); return; }
        }
        throw new InvalidOperationException("Operand is not a bundle input.");
    }
}
