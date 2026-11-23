using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal enum RangeRecipeKind : byte
{
    Inline, Empty, Window, Map, Merge, Union, Intersection, Difference, SymmetricDifference, Concat, Sort, Filter
}

internal sealed record RangeRecipe(RangeRecipeKind Kind, RangeRecipe? Left = null, RangeRecipe? Right = null,
    HirFunctionId? Callback = null)
{
    internal bool TracksSortedSource => Kind is >= RangeRecipeKind.Merge and <= RangeRecipeKind.SymmetricDifference
        || Left?.TracksSortedSource == true;

    internal bool RequiresPhysicalSpecialization => TracksSortedSource
        || Kind is RangeRecipeKind.Concat or RangeRecipeKind.Sort or RangeRecipeKind.Filter;

    internal void AppendKey(StringBuilder key)
    {
        key.Append('[').Append((byte)Kind);
        if (Callback is { } callback) key.Append('@').Append(callback.Value);
        Left?.AppendKey(key);
        Right?.AppendKey(key);
        key.Append(']');
    }
}

internal sealed partial class SemanticAnalyzer
{
    private static readonly RangeRecipe InlineRange = new(RangeRecipeKind.Inline);
    private static readonly RangeRecipe EmptyRange = new(RangeRecipeKind.Empty);
    private static readonly RangeRecipe SplitRange = new(RangeRecipeKind.Window);

    private RangeRecipe? NativeRangeRecipe(string name, IReadOnlyList<SemanticValue> inputs, HirFunctionId? callback)
    {
        if (sessionDispatch is not null || inputs.Count == 0) return null;
        if (name == "ranges.empty_of") return EmptyRange;
        var left = inputs[0].RangeRecipe;
        if (name is "ranges.identity" or "ranges.all") return left;
        if (name == "ranges.materialize") return left?.Kind == RangeRecipeKind.Empty ? EmptyRange : InlineRange;
        if (left is null) return null;
        if (name is "ranges.sort_with" or "ranges.filter" && callback is { } compare
            && left.Kind is RangeRecipeKind.Inline or RangeRecipeKind.Window or RangeRecipeKind.Empty)
            return new(name == "ranges.sort_with" ? RangeRecipeKind.Sort : RangeRecipeKind.Filter, Callback: compare);
        if (name == "ranges.concat" && inputs[1].RangeRecipe is { } suffix)
        {
            if (left.Kind == RangeRecipeKind.Empty) return suffix;
            if (suffix.Kind == RangeRecipeKind.Empty) return left;
            if (Segment(left) && Segment(suffix))
                return new(RangeRecipeKind.Concat, Shape(left), Shape(suffix));
        }
        if (name == "ranges.take" && left.TracksSortedSource)
            return left.Kind != RangeRecipeKind.Window
                && inputs[1].Literal is { Kind: HirLiteralKind.Number, Number: 0 }
                ? new(RangeRecipeKind.Window, left) : left;
        if (name is "ranges.take" or "ranges.drop" && left.Kind < RangeRecipeKind.Map)
            return left.Kind == RangeRecipeKind.Empty ? left : SplitRange;
        if (name == "ranges.transform" && callback.HasValue && left.Kind != RangeRecipeKind.Map)
            return new(RangeRecipeKind.Map, left, Callback: callback);
        var kind = name switch
        {
            "ranges.merge" or "ranges.merge_with" => RangeRecipeKind.Merge,
            "ranges.set_union" => RangeRecipeKind.Union,
            "ranges.set_intersection" => RangeRecipeKind.Intersection,
            "ranges.set_difference" => RangeRecipeKind.Difference,
            "ranges.set_symmetric_difference" => RangeRecipeKind.SymmetricDifference,
            _ => (RangeRecipeKind?)null
        };
        if (kind is null || inputs.Count < 2 || inputs[1].RangeRecipe is not { } right
            || left.TracksSortedSource || right.TracksSortedSource
            || inputs.Count == 3 && callback is null) return null;
        TypeKind item = Runtime(Item(inputs[0])).Kind | Runtime(Item(inputs[1])).Kind;
        return item == TypeKind.Number || kind == RangeRecipeKind.Merge && callback.HasValue && item == TypeKind.Scope
            ? new(kind.Value, left, right, callback) : null;

        static bool Segment(RangeRecipe item) => item.Kind is RangeRecipeKind.Inline or RangeRecipeKind.Window or RangeRecipeKind.Concat;
        static RangeRecipe Shape(RangeRecipe item) => item.Kind == RangeRecipeKind.Concat ? item : InlineRange;
    }
}
