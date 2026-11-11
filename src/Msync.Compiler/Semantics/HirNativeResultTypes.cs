using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal static class HirNativeResultTypes
{
    public static bool IsStructuralRange(NativeResultRule rule) =>
        rule is NativeResultRule.RangeEnumerate or NativeResultRule.RangeZip or NativeResultRule.RangeConcat
            or NativeResultRule.RangeNested or NativeResultRule.RangeRepeat or NativeResultRule.RangeOfType
            or NativeResultRule.RangeMerge or NativeResultRule.RangeMergeLeft;

    public static HirTypeId? Project(NativeResultRule rule, HirTypeId first, HirTypeId? second,
        HirTypeId? denoted, SemanticArena hir, HirFlowTypeAlgebra types)
    {
        return rule switch
        {
            NativeResultRule.RangeEnumerate => Range(Record(
                ("index", hir.Type(TypeRef.Number)), ("value", types.Element(first)))),
            NativeResultRule.RangeConcat or NativeResultRule.RangeMerge when second is { } right =>
                Range(types.Union(types.Element(first), types.Element(right))),
            NativeResultRule.RangeZip when second is { } right => Range(Record(
                ("first", types.Element(first)), ("second", types.Element(right)))),
            NativeResultRule.RangeNested or NativeResultRule.RangeRepeat => Range(first),
            NativeResultRule.RangeMergeLeft => first,
            NativeResultRule.RangeOfType when denoted is { } element => Range(element),
            _ => null
        };

        HirTypeId Range(HirTypeId element) => hir.Type(new HirType(
            new(TypeKind.Range, hir.Types[element.Value].RuntimeType), Element: element.Value));

        HirTypeId Record(params (string Name, HirTypeId Type)[] fields)
        {
            int layout = hir.InternScopeLayout(new([.. fields.Select(field =>
                new HirField(field.Name, field.Type, hir.Types[field.Type.Value].RuntimeType.Nullable))]));
            return hir.Type(new HirType(TypeRef.Scope, Shape: layout));
        }
    }
}
