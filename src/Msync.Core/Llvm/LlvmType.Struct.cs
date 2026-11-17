// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from DerivedTypes.h and Type.cpp, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
using System.Globalization;
namespace Msync.Core.Llvm;

public sealed class LlvmStructType : LlvmType
{
    private const uint HasBody = 1, Packed = 2, Literal = 4, Sized = 8;
    private const uint ContainsScalable = 16, NotContainsScalable = 32;
    // These aliases deliberately match the pinned upstream SCDB constants.
    private const uint ContainsNonGlobal = 64, NotContainsNonGlobal = 128;
    private const uint ContainsNonLocal = 64, NotContainsNonLocal = 128;
    private uint flags;
    private string name = "";
    private LlvmStructType(LlvmContext context) : base(context, LlvmTypeId.StructTyID) { }
    public IReadOnlyList<LlvmType> Elements => containedTypes;
    public int NumElements => containedTypes.Count;
    public bool IsOpaque => (flags & HasBody) == 0;
    public bool IsPacked => (flags & Packed) != 0;
    public bool IsLiteral => (flags & Literal) != 0;
    // The fields describe the IR ABI, not separately addressable target storage.
    public bool HasNativeRecordStorage { get; private init; }
    public bool HasName => name.Length != 0;
    public string Name { get { Debug.Assert(!IsLiteral, "Literal structs never have names"); return name; } }
    public LlvmType GetElementType(int index) => containedTypes[index];
    public static LlvmStructType Get(LlvmContext context, IReadOnlyList<LlvmType> elements, bool packed = false)
    {
        var key = new LlvmTypeKey(elements, packed);
        if (context.AnonStructTypes.TryGetValue(key, out var type)) return type;
        type = new(context) { flags = Literal };
        type.SetBody(elements, packed);
        context.AnonStructTypes.Add(key.Copy(), type);
        return type;
    }
    public static LlvmStructType Get(LlvmContext context, bool packed = false) => Get(context, [], packed);
    public static LlvmStructType Create(LlvmContext context, string name = "")
    {
        var type = new LlvmStructType(context);
        if (name.Length != 0) type.SetName(name);
        return type;
    }
    public static LlvmStructType Create(LlvmContext context, IReadOnlyList<LlvmType> elements, string name = "", bool packed = false)
    {
        var type = Create(context, name);
        type.SetBody(elements, packed);
        return type;
    }
    public static LlvmStructType CreateNativeRecord(LlvmContext context, IReadOnlyList<LlvmType> elements, string name)
    {
        var type = new LlvmStructType(context) { HasNativeRecordStorage = true };
        type.SetName(name);
        type.SetBody(elements);
        return type;
    }
    public static LlvmStructType? GetTypeByName(LlvmContext context, string name) => context.GetNamedStruct(name);
    public void SetName(string newName)
    {
        if (newName == Name) return;
        if (name.Length != 0) Context.NamedStructTypes.Remove(name);
        if (newName.Length == 0) { name = ""; return; }
        string candidate = newName;
        while (!Context.NamedStructTypes.TryAdd(candidate, this))
            candidate = newName + "." + (Context.NamedStructTypesUniqueId++).ToString(CultureInfo.InvariantCulture);
        name = candidate;
    }
    public bool CheckBody(IReadOnlyList<LlvmType> elements, out string? error)
    {
        var seen = new HashSet<LlvmType>();
        var worklist = new List<LlvmType>();
        foreach (var type in elements) if (seen.Add(type)) worklist.Add(type);
        for (int i = 0; i < worklist.Count; ++i)
        {
            var type = worklist[i];
            if (ReferenceEquals(type, this))
            { error = "identified structure type '" + Name + "' is recursive"; return false; }
            foreach (var subtype in type.Subtypes) if (seen.Add(subtype)) worklist.Add(subtype);
        }
        error = null;
        return true;
    }
    public bool TrySetBody(IReadOnlyList<LlvmType> elements, bool packed, out string? error)
    {
        Debug.Assert(IsOpaque, "Struct body already set!");
        if (!CheckBody(elements, out error)) return false;
        flags |= HasBody;
        if (packed) flags |= Packed;
        containedTypes = Array.AsReadOnly(elements.ToArray());
        return true;
    }
    public void SetBody(IReadOnlyList<LlvmType> elements, bool packed = false)
    {
        if (!TrySetBody(elements, packed, out string? error)) throw new InvalidOperationException(error);
    }
    public bool ContainsHomogeneousTypes() => Elements.Count != 0 && Elements.All(t => ReferenceEquals(t, Elements[0]));
    public bool ContainsHomogeneousScalableVectorTypes() => Elements.Count != 0 && Elements[0] is LlvmScalableVectorType && ContainsHomogeneousTypes();
    public override bool IsSized()
    {
        if ((flags & Sized) != 0) return true;
        if (IsOpaque) return false;
        if (!ContainsHomogeneousScalableVectorTypes())
            foreach (var type in Elements) if (type.IsScalableTy() || !type.IsSized()) return false;
        flags |= Sized;
        return true;
    }
    public override bool IsScalableTy()
    {
        if ((flags & ContainsScalable) != 0) return true;
        if ((flags & NotContainsScalable) != 0) return false;
        foreach (var type in Elements)
            if (type.IsScalableTy()) { flags |= ContainsScalable; return true; }
        if (!IsOpaque) flags |= NotContainsScalable;
        return false;
    }
    public override bool ContainsNonGlobalTargetExtType()
    {
        if ((flags & ContainsNonGlobal) != 0) return true;
        if ((flags & NotContainsNonGlobal) != 0) return false;
        foreach (var type in Elements)
            if (type.ContainsNonGlobalTargetExtType()) { flags |= ContainsNonGlobal; return true; }
        if (!IsOpaque) flags |= NotContainsNonGlobal;
        return false;
    }
    public override bool ContainsNonLocalTargetExtType()
    {
        if ((flags & ContainsNonLocal) != 0) return true;
        if ((flags & NotContainsNonLocal) != 0) return false;
        foreach (var type in Elements)
            if (type.ContainsNonLocalTargetExtType()) { flags |= ContainsNonLocal; return true; }
        if (!IsOpaque) flags |= NotContainsNonLocal;
        return false;
    }
    public bool IsLayoutIdentical(LlvmStructType other) => ReferenceEquals(this, other) ||
        IsPacked == other.IsPacked && Elements.SequenceEqual(other.Elements);
    public bool IndexValid(uint index) => index < (uint)Elements.Count;
    public static bool IsValidElementType(LlvmType type) => !type.IsVoidTy() && !type.IsLabelTy() &&
        !type.IsMetadataTy() && !type.IsFunctionTy() && !type.IsTokenTy();
}
