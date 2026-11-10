using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal static class HirNativeResultTypes
{
    public static HirTypeId Project(NativeResultRule rule, HirTypeId first, HirTypeId second,
        SemanticArena hir, HirFlowTypeAlgebra types)
    {
        return rule switch
        {
            NativeResultRule.RangeEnumerate => Range(Record(
                ("index", hir.Type(TypeRef.Number)), ("value", types.Element(first)))),
            NativeResultRule.RangeConcat => Range(types.Union(types.Element(first), types.Element(second))),
            NativeResultRule.RangeZip => Range(Record(
                ("first", types.Element(first)), ("second", types.Element(second)))),
            _ => throw new InvalidOperationException($"{rule} has no structural native range projection.")
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
