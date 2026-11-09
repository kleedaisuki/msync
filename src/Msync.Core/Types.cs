using System.Text;

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
    public TypeRef WithoutNull => Nullable ? new(Kind & ~TypeKind.Null, Element) : this;
    public TypeRef WithNull => Nullable ? this : new(Kind | TypeKind.Null, Element);
    public bool Accepts(TypeRef other) => (Kind & other.Kind) == other.Kind && (Element is null || other.Element is null || Element.Accepts(other.Element));
    public static TypeRef Union(TypeRef left, TypeRef right)
    {
        if (ReferenceEquals(left, right)) return left;
        var kind = left.Kind | right.Kind;
        var element = left.Element is null ? right.Element : right.Element is null ? left.Element : Union(left.Element, right.Element);
        if (kind == left.Kind && element == left.Element) return left;
        if (kind == right.Kind && element == right.Element) return right;
        return new(kind, element);
    }
    public override string ToString()
    {
        string? scalar = Kind switch
        {
            TypeKind.Never => "Never",
            TypeKind.Null => "Null",
            TypeKind.Bool => "Bool",
            TypeKind.Bool | TypeKind.Null => "Bool?",
            TypeKind.String => "String",
            TypeKind.String | TypeKind.Null => "String?",
            TypeKind.Path => "Path",
            TypeKind.Path | TypeKind.Null => "Path?",
            TypeKind.Function => "Function",
            TypeKind.Function | TypeKind.Null => "Function?",
            TypeKind.Scope => "Scope",
            TypeKind.Scope | TypeKind.Null => "Scope?",
            TypeKind.Process => "Process",
            TypeKind.Process | TypeKind.Null => "Process?",
            TypeKind.Connection => "Connection",
            TypeKind.Connection | TypeKind.Null => "Connection?",
            TypeKind.Type => "Type",
            TypeKind.Type | TypeKind.Null => "Type?",
            TypeKind.Number => "Number",
            TypeKind.Number | TypeKind.Null => "Number?",
            _ => null
        };
        if (scalar is not null) return scalar;
        if ((Kind & ~TypeKind.Null) == TypeKind.Range)
            return Element is null ? Nullable ? "Range?" : "Range" : $"Range<{Element}>{(Nullable ? "?" : "")}";
        return UnionText();
    }

    private string UnionText()
    {
        StringBuilder text = new();
        for (TypeKind bit = TypeKind.Bool; bit <= TypeKind.Number; bit = (TypeKind)((ushort)bit << 1))
        {
            if ((Kind & bit) == 0) continue;
            if (text.Length != 0) text.Append(" | ");
            if (bit == TypeKind.Range && Element is not null) text.Append("Range<").Append(Element).Append('>');
            else text.Append(bit switch
            {
                TypeKind.Bool => "Bool",
                TypeKind.String => "String",
                TypeKind.Path => "Path",
                TypeKind.Range => "Range",
                TypeKind.Function => "Function",
                TypeKind.Scope => "Scope",
                TypeKind.Process => "Process",
                TypeKind.Connection => "Connection",
                TypeKind.Type => "Type",
                _ => "Number"
            });
        }
        if (Nullable) text.Append('?');
        return text.ToString();
    }
}
