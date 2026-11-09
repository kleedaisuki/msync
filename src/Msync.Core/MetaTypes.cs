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

    public static Value Evaluate(string operation, IReadOnlyList<TypeRef> types)
    {
        TypeRef first = types[0];
        return operation switch
        {
            "is_null" => Boolean(first.Kind == TypeKind.Null),
            "is_bool" => Boolean(first.Kind == TypeKind.Bool),
            "is_number" => Boolean(first.Kind == TypeKind.Number),
            "is_string" => Boolean(first.Kind == TypeKind.String),
            "is_path" => Boolean(first.Kind == TypeKind.Path),
            "is_range" => Boolean(first.Kind == TypeKind.Range),
            "is_function" => Boolean(first.Kind == TypeKind.Function),
            "is_scope" => Boolean(first.Kind == TypeKind.Scope),
            "is_process" => Boolean(first.Kind == TypeKind.Process),
            "is_connection" => Boolean(first.Kind == TypeKind.Connection),
            "is_type" => Boolean(first.Kind == TypeKind.Type),
            "is_never" => Boolean(first.Kind == TypeKind.Never),
            "is_union" => Boolean(BitOperations.PopCount((uint)first.Kind) > 1),
            "is_scalar" => Boolean(first.Kind != TypeKind.Never && (first.Kind & ~Scalars) == 0),
            "is_nullable" => Boolean(first.Nullable),
            "is_same" => Boolean(first == types[1]),
            "accepts" => Boolean(first.Accepts(types[1])),
            "is_assignable" => Boolean(IsConvertible(types[1], first)),
            "is_convertible" => Boolean(IsConvertible(first, types[1])),
            "is_castable" => Boolean(ExplicitConversions.Possible(first, types[1])),
            "add_null" => new TypeValue(first.WithNull),
            "remove_null" => new TypeValue(first.WithoutNull),
            "common_type" => new TypeValue(TypeRef.Union(first, types[1])),
            "range_of" => new TypeValue(new(TypeKind.Range, first)),
            "element_type" => first.WithoutNull.Kind == TypeKind.Range
                ? new TypeValue(first.Element ?? TypeRef.Any) : Value.Null,
            "range_rank" => new NumberValue(RangeRank(first)),
            "type_name" => new StringValue(first.ToString()),
            "alternatives" => new RangeValue(Alternatives(first), TypeRef.Type),
            _ => throw new MsyncException($"Unknown meta native '{operation}'")
        };
    }

    private static Value Boolean(bool value) => value ? Value.True : Value.False;

    private static int RangeRank(TypeRef type)
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

    private static IEnumerable<Value> Alternatives(TypeRef type)
    {
        for (TypeKind bit = TypeKind.Null; bit <= TypeKind.Number; bit = (TypeKind)((ushort)bit << 1))
            if ((type.Kind & bit) != 0) yield return new TypeValue(new(bit, bit == TypeKind.Range ? type.Element : null));
    }
}
