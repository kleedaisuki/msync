// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Function.h/.cpp, Attributes.h.
namespace Msync.Core.Llvm;

public readonly record struct LlvmVScaleRange(uint Minimum, uint Maximum = 0);

public sealed partial class LlvmFunction
{
    private Dictionary<int, LlvmParameterAttributes>? parameterAttributes;
    public LlvmParameterAttributes ReturnAttributes { get; set; }
    public uint CallingConvention { get; set; }
    public bool NoCreateUndefOrPoison { get; set; }
    public bool HasNoCreateUndefOrPoison => NoCreateUndefOrPoison || IntrinsicDescriptor?.NoCreateUndefOrPoison == true;
    public bool NoUnwind { get; set; }
    public bool StrictFP { get; set; }
    public bool NoImplicitFloat { get; set; }
    public bool Naked { get; set; }
    public bool Thunk { get; set; }
    public bool NoBuiltin { get; set; }
    public bool WillReturnAttribute { get; set; }
    public bool NoFree { get; set; }
    public bool NoSync { get; set; }
    public bool Speculatable { get; set; }
    public LlvmVScaleRange? VScaleRange { get; set; }
    public LlvmDenormalFPEnv? DenormalFPEnvironment { get; set; }
    public bool DoesNotThrow => NoUnwind || IntrinsicDescriptor?.NoUnwind == true;
    public bool HasWillReturn => WillReturnAttribute || IntrinsicDescriptor?.WillReturn == true;
    public bool DoesNotFreeMemory => NoFree || IntrinsicDescriptor?.NoFree == true;
    public bool DoesNotSync => NoSync || IntrinsicDescriptor?.NoSync == true;
    public bool IsSpeculatable => Speculatable || IntrinsicDescriptor?.Speculatable == true;
    public LlvmParameterAttributes GetParamAttributes(int index) =>
        parameterAttributes?.GetValueOrDefault(index) ?? default;
    public void SetParamAttributes(int index, LlvmParameterAttributes attributes)
    {
        System.Diagnostics.Debug.Assert(index >= 0);
        if (attributes.IsEmpty) { parameterAttributes?.Remove(index); return; }
        (parameterAttributes ??= [])[index] = attributes;
    }
    public void AddParamAttr(int index, LlvmAttributeKind attribute) => SetParamAttributes(index, GetParamAttributes(index).Add(attribute));
    public void RemoveParamAttr(int index, LlvmAttributeKind attribute) => SetParamAttributes(index, GetParamAttributes(index).Remove(attribute));
    public bool ParamHasAttr(int index, LlvmAttributeKind attribute) => GetParamAttributes(index).Has(attribute) ||
        attribute == LlvmAttributeKind.NoUndef && IntrinsicDescriptor?.IsNoUndefArgument(index) == true;
    public bool HasRetAttr(LlvmAttributeKind attribute) => ReturnAttributes.Has(attribute);
    public LlvmCaptureInfo GetParamCaptureInfo(int index) => GetParamAttributes(index).CaptureInfo ?? LlvmCaptureInfo.All();
    public ulong GetRetDereferenceableBytes() => ReturnAttributes.DereferenceableBytes ?? 0;
    public ulong GetRetDereferenceableOrNullBytes() => ReturnAttributes.DereferenceableOrNullBytes ?? 0;
    public LlvmAlign? GetRetAlign() => ReturnAttributes.Alignment;
    public uint GetVScaleValue() => VScaleRange is { Minimum: > 0 } range && range.Minimum == range.Maximum ? range.Minimum : 0;
    public LlvmDenormalFPEnv GetDenormalFPEnv() => DenormalFPEnvironment ?? LlvmDenormalFPEnv.Default;
    public LlvmDenormalModeInfo GetDenormalMode(LlvmAPFloatSemantics semantics) =>
        (byte)semantics == 2 ? GetDenormalFPEnv().F32Mode : GetDenormalFPEnv().DefaultMode;
    public LlvmDenormalModeInfo GetDenormalMode(LlvmType type) => type.GetScalarType().IsFloatTy()
        ? GetDenormalFPEnv().F32Mode : GetDenormalFPEnv().DefaultMode;
}
