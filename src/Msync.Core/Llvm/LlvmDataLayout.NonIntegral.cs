// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/DataLayout.cpp ni specification; DataLayout.h pointer-integrality queries.
using System.Globalization;

namespace Msync.Core.Llvm;

public sealed partial class LlvmDataLayout
{
    private readonly List<uint> nonIntegralAddressSpaces = [];

    private bool ParseNonIntegralSpecification(string specification)
    {
        if (!specification.StartsWith("ni", StringComparison.Ordinal)) return false;
        string rest = specification[2..];
        if (!rest.StartsWith(':'))
            throw new FormatException("malformed specification, must be of the form \"ni:<address space>[:<address space>]...\"");
        foreach (string component in rest[1..].Split(':'))
        {
            if (component.Length == 0) throw new FormatException("address space component cannot be empty");
            if (!uint.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out uint addressSpace)
                || addressSpace >= (1U << 24)) throw new FormatException("address space must be a 24-bit integer");
            if (addressSpace == 0) throw new FormatException("address space 0 cannot be non-integral");
            nonIntegralAddressSpaces.Add(addressSpace);
        }
        return true;
    }

    private void FinalizeNonIntegralSpecifications()
    {
        foreach (uint addressSpace in nonIntegralAddressSpaces)
            pointerSpecs[addressSpace] = GetPointerSpec(addressSpace) with
            {
                HasUnstableRepresentation = true,
                HasExternalState = false,
                Name = GetAddressSpaceName(addressSpace)
            };
    }

    public bool HasUnstableRepresentation(uint addressSpace) => GetPointerSpec(addressSpace).HasUnstableRepresentation;
    public bool HasUnstableRepresentation(LlvmType type) => type.GetScalarType() is LlvmPointerType pointer && HasUnstableRepresentation(pointer.AddressSpace);
    public bool HasExternalState(uint addressSpace) => GetPointerSpec(addressSpace).HasExternalState;
    public bool HasExternalState(LlvmType type) => type.GetScalarType() is LlvmPointerType pointer && HasExternalState(pointer.AddressSpace);
    public bool MustNotIntroduceIntToPtr(uint addressSpace) => HasUnstableRepresentation(addressSpace) || HasExternalState(addressSpace);
    public bool MustNotIntroduceIntToPtr(LlvmType type) => type.GetScalarType() is LlvmPointerType pointer && MustNotIntroduceIntToPtr(pointer.AddressSpace);
    public bool MustNotIntroducePtrToInt(uint addressSpace) => HasUnstableRepresentation(addressSpace);
    public bool MustNotIntroducePtrToInt(LlvmType type) => type.GetScalarType() is LlvmPointerType pointer && MustNotIntroducePtrToInt(pointer.AddressSpace);
    public bool IsNonIntegralAddressSpace(uint addressSpace)
    {
        PointerSpec spec = GetPointerSpec(addressSpace);
        return spec.BitWidth != spec.IndexBitWidth || spec.HasUnstableRepresentation || spec.HasExternalState;
    }
    public bool IsNonIntegralPointerType(LlvmType type) =>
        type.GetScalarType() is LlvmPointerType pointer && IsNonIntegralAddressSpace(pointer.AddressSpace);
}
