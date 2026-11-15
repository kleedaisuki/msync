// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/DataLayout.cpp parsePointerSpec/parseAddrSpaceAndName/getPointerSpec.
using System.Globalization;
using System.Numerics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmDataLayout
{
    private sealed record PointerSpec(uint BitWidth, LlvmAlign ABIAlign, LlvmAlign PrefAlign, uint IndexBitWidth,
        bool HasUnstableRepresentation, bool HasExternalState, string Name, LlvmAPInt NullValue);
    private readonly Dictionary<uint, PointerSpec> pointerSpecs = new()
    {
        [0] = new(64, new(8), new(8), 64, false, false, "", LlvmAPInt.GetZero(64))
    };
    private readonly HashSet<string> pointerAddressSpaceNames = new(StringComparer.Ordinal);
    private PointerSpec GetPointerSpec(uint addressSpace) => pointerSpecs.GetValueOrDefault(addressSpace, pointerSpecs[0]);
    public LlvmAPInt GetNullPtrValue(uint addressSpace) => GetPointerSpec(addressSpace).NullValue;
    public string GetAddressSpaceName(uint addressSpace) => GetPointerSpec(addressSpace).Name;

    private void ParsePointerSpecification(string specification)
    {
        string[] components = specification[1..].Split(':');
        if (components.Length is < 3 or > 5)
            throw new FormatException("malformed specification, must be of the form \"p[<n>]:<size>:<abi>[:<pref>[:<idx>]]\"");
        string address = components[0];
        bool externalState = false, unstableRepresentation = false;
        char nullFlag = '\0';
        int index = 0;
        while (index < address.Length && char.IsAsciiLetter(address[index]))
        {
            char flag = address[index++];
            switch (flag)
            {
                case 'e': externalState = true; break;
                case 'u': unstableRepresentation = true; break;
                case 'z': case 'o':
                    if (nullFlag != '\0') throw new FormatException("only one of 'z' or 'o' may be specified");
                    nullFlag = flag;
                    break;
                default: throw new FormatException($"'{flag}' is not a valid pointer specification flag");
            }
        }
        (uint addressSpace, string name) = index == address.Length ? (0U, "") : ParsePointerAddressSpace(address[index..]);
        if (addressSpace == 0 && (externalState || unstableRepresentation))
            throw new FormatException("address space 0 cannot be unstable or have external state");
        if (name.Length != 0 && !pointerAddressSpaceNames.Add(name))
            throw new FormatException($"address space name `{name}` already used");
        uint bits = ParsePointerSize(components[1], "pointer size");
        LlvmAlign abi = ParsePointerAlignment(components[2], "ABI");
        LlvmAlign preferred = components.Length > 3 ? ParsePointerAlignment(components[3], "preferred") : abi;
        if (preferred < abi) throw new FormatException("preferred alignment cannot be less than the ABI alignment");
        uint indexBits = components.Length > 4 ? ParsePointerSize(components[4], "index size") : bits;
        if (indexBits > bits) throw new FormatException("index size cannot be larger than the pointer size");
        pointerSpecs[addressSpace] = new(bits, abi, preferred, indexBits, unstableRepresentation, externalState,
            name, nullFlag == 'o' ? LlvmAPInt.GetAllOnes(bits) : LlvmAPInt.GetZero(bits));
    }

    private static (uint AddressSpace, string Name) ParsePointerAddressSpace(string text)
    {
        int count = 0;
        while (count < text.Length && char.IsAsciiDigit(text[count])) count++;
        uint addressSpace = 0;
        if (count != 0 && (!uint.TryParse(text[..count], NumberStyles.None, CultureInfo.InvariantCulture, out addressSpace)
            || addressSpace >= (1U << 24))) throw new FormatException("address space must be a 24-bit integer");
        string rest = text[count..];
        if (rest.Length == 0) return (addressSpace, "");
        if (rest[0] != '(') throw new FormatException("address space must be a 24-bit integer");
        if (rest[^1] != ')' || rest.Length == 2) throw new FormatException("Expected `( address space name )`");
        string name = rest[1..^1];
        if (name is "P" or "G" or "A") throw new FormatException("Cannot use predefined address space names P/G/A in data layout");
        return (addressSpace, name);
    }

    private static uint ParsePointerSize(string text, string name)
    {
        if (text.Length == 0) throw new FormatException(name + " component cannot be empty");
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value == 0 || value >= (1U << 24))
            throw new FormatException(name + " must be a non-zero 24-bit integer");
        return value;
    }

    private static LlvmAlign ParsePointerAlignment(string text, string name)
    {
        if (text.Length == 0) throw new FormatException(name + " alignment component cannot be empty");
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint bits) || bits >= (1U << 16))
            throw new FormatException(name + " alignment must be a 16-bit integer");
        if (bits == 0) throw new FormatException(name + " alignment must be non-zero");
        if (bits % 8 != 0 || !BitOperations.IsPow2(bits / 8))
            throw new FormatException(name + " alignment must be a power of two times the byte width");
        return new(bits / 8);
    }
}
