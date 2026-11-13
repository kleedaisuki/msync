using System.Numerics;
using Msync.Core.Native;

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

    public static NativeValue Evaluate(string operation, IReadOnlyList<TypeRef> types) =>
        Evaluate(operation, types[0], types.Count > 1 ? types[1] : null);

    public static NativeValue Evaluate(string operation, TypeRef first, TypeRef? second = null)
    {
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
            "is_same" => Boolean(first == Required(second)),
            "accepts" => Boolean(first.Accepts(Required(second))),
            "is_assignable" => Boolean(IsConvertible(Required(second), first)),
            "is_convertible" => Boolean(IsConvertible(first, Required(second))),
            "is_castable" => Boolean(ExplicitConversions.Possible(first, Required(second))),
            "add_null" => NativeValue.FromType(first.WithNull),
            "remove_null" => NativeValue.FromType(first.WithoutNull),
            "common_type" => NativeValue.FromType(TypeRef.Union(first, Required(second))),
            "range_of" => NativeValue.FromType(new(TypeKind.Range, first)),
            "element_type" => first.WithoutNull.Kind == TypeKind.Range
                ? NativeValue.FromType(first.Element ?? TypeRef.Any) : NativeValue.Null,
            "range_rank" => NativeValue.FromNumber(RangeRank(first)),
            "type_name" => NativeValue.FromString(first.ToString()),
            "alternatives" => NativeValue.FromRange(NativeRange.Snapshot(Alternatives(first), TypeRef.Type)),
            _ => throw new MsyncException($"Unknown meta native '{operation}'")
        };
    }

    private static TypeRef Required(TypeRef? type) => type ?? throw new ArgumentException("This metadata operation requires two Type descriptors.");

    private static NativeValue Boolean(bool value) => NativeValue.FromBool(value);

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

    private static IEnumerable<NativeValue> Alternatives(TypeRef type)
    {
        for (TypeKind bit = TypeKind.Null; bit <= TypeKind.Number; bit = (TypeKind)((ushort)bit << 1))
            if ((type.Kind & bit) != 0) yield return NativeValue.FromType(new(bit, bit == TypeKind.Range ? type.Element : null));
    }
}
