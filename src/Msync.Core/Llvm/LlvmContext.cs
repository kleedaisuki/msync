// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from LLVMContextImpl.h/.cpp and Type.cpp, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
namespace Msync.Core.Llvm;

public sealed partial class LlvmContext
{
    private readonly Dictionary<LlvmTypeId, LlvmType> primitives = new();
    internal readonly Dictionary<uint, LlvmIntegerType> IntegerTypes = new();
    internal readonly Dictionary<uint, LlvmByteType> ByteTypes = new();
    internal readonly Dictionary<uint, LlvmIntegerType> BuiltinIntegers = new();
    internal readonly Dictionary<uint, LlvmByteType> BuiltinBytes = new();
    internal readonly Dictionary<LlvmTypeKey, LlvmFunctionType> FunctionTypes = new();
    internal readonly Dictionary<LlvmTypeKey, LlvmStructType> AnonStructTypes = new();
    internal readonly Dictionary<string, LlvmStructType> NamedStructTypes = new(StringComparer.Ordinal);
    internal uint NamedStructTypesUniqueId;
    internal readonly Dictionary<(LlvmType, ulong), LlvmArrayType> ArrayTypes = new();
    internal readonly Dictionary<(LlvmType, LlvmElementCount), LlvmVectorType> VectorTypes = new();
    internal LlvmPointerType? As0PointerType;
    internal readonly Dictionary<uint, LlvmPointerType> PointerTypes = new();
    internal readonly Dictionary<(LlvmType, uint), LlvmTypedPointerType> TypedPointerTypes = new();
    internal readonly Dictionary<LlvmTypeKey, LlvmTargetExtensionType> TargetExtTypes = new();
    internal readonly Dictionary<LlvmValue, LlvmValueName> ValueNames = new();
    // IROptions.td: non-global-value-max-name-size.
    public int NonGlobalValueMaxNameSize { get; set; } = 1024;
    public bool ShouldDiscardValueNames { get; private set; }
    public void SetDiscardValueNames(bool discard) => ShouldDiscardValueNames = discard;

    public LlvmContext()
    {
        foreach (LlvmTypeId id in new[] { LlvmTypeId.VoidTyID, LlvmTypeId.LabelTyID,
            LlvmTypeId.HalfTyID, LlvmTypeId.BFloatTyID, LlvmTypeId.FloatTyID,
            LlvmTypeId.DoubleTyID, LlvmTypeId.MetadataTyID, LlvmTypeId.TokenTyID,
            LlvmTypeId.X86_FP80TyID, LlvmTypeId.FP128TyID, LlvmTypeId.PPC_FP128TyID,
            LlvmTypeId.X86_AMXTyID })
            primitives.Add(id, new LlvmPrimitiveType(this, id));
        foreach (uint bits in new uint[] { 1, 8, 16, 32, 64, 128 })
        {
            BuiltinIntegers.Add(bits, new LlvmIntegerType(this, bits));
            BuiltinBytes.Add(bits, new LlvmByteType(this, bits));
        }
    }

    public LlvmType? GetPrimitiveType(LlvmTypeId id) => primitives.GetValueOrDefault(id);
    public LlvmStructType? GetNamedStruct(string name) => NamedStructTypes.GetValueOrDefault(name);
}

// LLVM's DenseSet heterogeneous keys use type pointer identity and ordered ArrayRef contents.
// The managed key borrows the lookup list; only inserted keys own a copied array.
internal readonly struct LlvmTypeKey : IEquatable<LlvmTypeKey>
{
    internal readonly string Name;
    internal readonly LlvmType? Result;
    internal readonly IReadOnlyList<LlvmType> Types;
    internal readonly IReadOnlyList<uint> Ints;
    internal readonly bool Flag;
    internal LlvmTypeKey(IReadOnlyList<LlvmType> types, bool flag = false,
        LlvmType? result = null, string name = "", IReadOnlyList<uint>? ints = null)
        => (Types, Flag, Result, Name, Ints) = (types, flag, result, name, ints ?? Array.Empty<uint>());
    internal LlvmTypeKey Copy() => new(Types.ToArray(), Flag, Result, Name, Ints.ToArray());
    public bool Equals(LlvmTypeKey other)
    {
        if (Flag != other.Flag || !ReferenceEquals(Result, other.Result) || Name != other.Name ||
            Types.Count != other.Types.Count || Ints.Count != other.Ints.Count) return false;
        for (int i = 0; i < Types.Count; ++i)
            if (!ReferenceEquals(Types[i], other.Types[i])) return false;
        for (int i = 0; i < Ints.Count; ++i)
            if (Ints[i] != other.Ints[i]) return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is LlvmTypeKey key && Equals(key);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal); hash.Add(Result); hash.Add(Flag);
        foreach (LlvmType type in Types) hash.Add(type);
        foreach (uint value in Ints) hash.Add(value);
        return hash.ToHashCode();
    }
}
