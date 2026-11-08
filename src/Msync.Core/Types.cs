namespace Msync.Core;

[Flags]
public enum TypeKind : ushort
{
    Never = 0, Null = 1, Bool = 2, String = 4, Path = 8, Range = 16,
    Function = 32, Scope = 64, Process = 128, Connection = 256, Type = 512, Number = 1024,
    Any = Null | Bool | String | Path | Range | Function | Scope | Process | Connection | Type | Number
}

public sealed record TypeRef(TypeKind Kind, TypeRef? Element = null)
{
    public static readonly TypeRef Null = new(TypeKind.Null);
    public static readonly TypeRef Bool = new(TypeKind.Bool);
    public static readonly TypeRef String = new(TypeKind.String);
    public static readonly TypeRef Path = new(TypeKind.Path);
    public static readonly TypeRef Function = new(TypeKind.Function);
    public static readonly TypeRef Scope = new(TypeKind.Scope);
    public static readonly TypeRef Process = new(TypeKind.Process);
    public static readonly TypeRef Connection = new(TypeKind.Connection);
    public static readonly TypeRef Type = new(TypeKind.Type);
    public static readonly TypeRef Number = new(TypeKind.Number);
    public static readonly TypeRef Any = new(TypeKind.Any);
    public bool Nullable => (Kind & TypeKind.Null) != 0;
    public TypeRef WithoutNull => new(Kind & ~TypeKind.Null, Element);
    public TypeRef WithNull => new(Kind | TypeKind.Null, Element);
    public bool Accepts(TypeRef other) => (Kind & other.Kind) == other.Kind && (Element is null || other.Element is null || Element.Accepts(other.Element));
    public static TypeRef Union(TypeRef left, TypeRef right) => new(left.Kind | right.Kind,
        left.Element is null ? right.Element : right.Element is null ? left.Element : Union(left.Element, right.Element));
    public override string ToString()
    {
        var core = Kind & ~TypeKind.Null;
        if (core == TypeKind.Never) return Kind == TypeKind.Null ? "Null" : "Never";
        var names = Enum.GetValues<TypeKind>().Where(k => k != TypeKind.Never && k != TypeKind.Null && k != TypeKind.Any && (core & k) != 0)
            .Select(k => k == TypeKind.Range && Element is not null ? $"Range<{Element}>" : k.ToString());
        return string.Join(" | ", names) + (Nullable ? "?" : "");
    }
}
