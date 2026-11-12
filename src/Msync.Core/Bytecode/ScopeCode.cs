namespace Msync.Core;

public sealed record CodeType(TypeKind Kind, int Element = -1, IReadOnlyList<int>? ScopeLayouts = null);
public sealed record CodeField(string Name, int StorageType);
public sealed record CodeLayout(IReadOnlyList<CodeField> Fields);
public readonly record struct CodeFieldCase(int Layout, int Ordinal);
public sealed record CodeFieldProjection(string Name, IReadOnlyList<CodeFieldCase> Cases);
public readonly record struct CodeStaticScope(int Layout);
public readonly record struct CodeCapture(int Type, bool Location);
public sealed record CodeSignature(IReadOnlyList<int> Parameters, int Result, IReadOnlyList<CodeCapture> Captures,
    IReadOnlyList<int>? RequiredStatics = null)
{
    public bool IsTemplate { get; init; }
}
