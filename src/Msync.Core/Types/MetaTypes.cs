using System.Numerics;

namespace Msync.Core;

public static class MetaTypes
{
    private const TypeKind Scalars = TypeKind.Bool | TypeKind.Number | TypeKind.String | TypeKind.Path |
        TypeKind.Connection | TypeKind.Type;

    public static bool IsConvertible(TypeRef source, TypeRef target)
    {
        if (target.Accepts(source)) return true;
        TypeKind text = TypeKind.Path | TypeKind.String;
        TypeKind retained = source.Kind & ~text;
        return (source.Kind & text) != 0 && (target.Kind & text) != 0 &&
            (target.Kind & retained) == retained &&
            (target.Element is null || source.Element is null || target.Element.Accepts(source.Element));
    }

    public static bool Predicate(string operation, TypeRef first, TypeRef? second = null) => operation switch
    {
        "is_null" => first.Kind == TypeKind.Null,
        "is_bool" => first.Kind == TypeKind.Bool,
        "is_number" => first.Kind == TypeKind.Number,
        "is_string" => first.Kind == TypeKind.String,
        "is_path" => first.Kind == TypeKind.Path,
        "is_range" => first.Kind == TypeKind.Range,
        "is_function" => first.Kind == TypeKind.Function,
        "is_scope" => first.Kind == TypeKind.Scope,
        "is_process" => first.Kind == TypeKind.Process,
        "is_connection" => first.Kind == TypeKind.Connection,
        "is_type" => first.Kind == TypeKind.Type,
        "is_never" => first.Kind == TypeKind.Never,
        "is_union" => BitOperations.PopCount((uint)first.Kind) > 1,
        "is_scalar" => first.Kind != TypeKind.Never && (first.Kind & ~Scalars) == 0,
        "is_nullable" => first.Nullable,
        "is_same" => first == Required(second),
        "accepts" => first.Accepts(Required(second)),
        "is_assignable" => IsConvertible(Required(second), first),
        "is_convertible" => IsConvertible(first, Required(second)),
        "is_castable" => ExplicitConversions.Possible(first, Required(second)),
        _ => throw new MsyncException($"Unknown meta predicate '{operation}'")
    };

    public static TypeRef? Derived(string operation, TypeRef first, TypeRef? second = null) => operation switch
    {
        "add_null" => first.WithNull,
        "remove_null" => first.WithoutNull,
        "common_type" => TypeRef.Union(first, Required(second)),
        "range_of" => new(TypeKind.Range, first),
        "element_type" => first.WithoutNull.Kind == TypeKind.Range ? first.Element ?? TypeRef.Any : null,
        _ => throw new MsyncException($"Unknown meta type constructor '{operation}'")
    };

    private static TypeRef Required(TypeRef? type) => type ?? throw new ArgumentException("This metadata operation requires two Type descriptors.");

    public static IEnumerable<TypeRef> Alternatives(TypeRef type)
    {
        for (TypeKind kind = TypeKind.Null; kind <= TypeKind.Number; kind = (TypeKind)((ushort)kind << 1))
            if ((type.Kind & kind) != 0)
                yield return new(kind, kind == TypeKind.Range ? type.Element : null);
    }

    public static int RangeRank(TypeRef type)
    {
        int rank = 0;
        while (type.WithoutNull.Kind == TypeKind.Range)
        {
            rank++;
            if (type.Element is not { } element) break;
            type = element;
        }
        return rank;
    }
}
