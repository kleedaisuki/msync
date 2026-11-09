using System.Collections.Frozen;
using Msync.Core;

namespace Msync.Compiler;

public static class LanguageTypes
{
    private static readonly (string Name, TypeRef Type)[] entries =
    [
        ("Bool", TypeRef.Bool), ("String", TypeRef.String), ("Path", TypeRef.Path),
        ("Function", TypeRef.Function), ("Scope", TypeRef.Scope), ("Connection", TypeRef.Connection),
        ("Process", TypeRef.Process), ("Null", TypeRef.Null), ("Number", TypeRef.Number),
        ("Type", TypeRef.Type), ("Range", new(TypeKind.Range)), ("Any", TypeRef.Any)
    ];

    private static readonly FrozenDictionary<string, TypeRef> types =
        entries.ToFrozenDictionary(entry => entry.Name, entry => entry.Type, StringComparer.Ordinal);

    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(entries.Select(entry => entry.Name).ToArray());
    public static bool Contains(string name) => types.ContainsKey(name);
    public static bool TryResolve(string name, out TypeRef type) => types.TryGetValue(name, out type!);
}
