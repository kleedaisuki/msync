namespace Msync.Core.Native;

internal sealed record LegacyV6NativeSignature(IReadOnlyList<TypeRef> Parameters, TypeRef Result);

/// <summary>Historical import contracts, never registered as source or runtime capabilities.</summary>
internal static partial class LegacyV6NativeSignatures
{
    private static readonly IReadOnlyDictionary<string, LegacyV6NativeSignature> signatures = Create();
    internal static LegacyV6NativeSignature Get(string name) => signatures.TryGetValue(name, out var signature)
        ? signature : throw new MsyncException($"Unknown format-6 allocator operation '{name}'");
    private static IReadOnlyDictionary<string, LegacyV6NativeSignature> Create()
    {
        Dictionary<string, LegacyV6NativeSignature> result = new(StringComparer.Ordinal);
        RegisterVectorStorage(result); RegisterArena(result); RegisterSlots(result);
        return result;
    }
    private static void Add(IDictionary<string, LegacyV6NativeSignature> result, string name, TypeRef output,
        params TypeRef[] parameters) => result.Add(name, new(parameters, output));
}
