using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// Keep structural alternatives by canonical type identity. Eagerly rebuilding
// recursive rows at every join would allocate new "equal" shapes indefinitely.
internal sealed class HirFlowTypeAlgebra(SemanticArena hir) : IFlowTypeAlgebra
{
    private readonly Dictionary<(HirTypeId Left, HirTypeId Right), HirTypeId> joins = [];
    private readonly Dictionary<(HirTypeId Source, TypeRef Tested, bool Positive), HirTypeId> refinements = [];

    public FlowFact Join(FlowFact left, FlowFact right)
    {
        HirTypeId first = left.SemanticType ?? hir.Type(left.Type);
        HirTypeId second = right.SemanticType ?? hir.Type(right.Type);
        HirTypeId type = Union(first, second);
        return new(hir.Types[type.Value].RuntimeType, left.Assigned && right.Assigned,
            left.Boolean == right.Boolean ? left.Boolean : null, type,
            FlowCallableDomain.Join(left.Callables, right.Callables),
            FlowDenotationDomain.Join(left.Denotations, right.Denotations));
    }

    public FlowFact Refine(FlowFact value, TypeRef tested, bool positive)
    {
        if ((value.Type.Kind & TypeKind.Type) != 0)
        {
            FlowDenotationDomain? denotations = value.Denotations is { } known
                ? new(known.Types.Where(type =>
                {
                    TypeRef actual = hir.Types[type.Value].RuntimeType;
                    bool? matches = tested.Accepts(actual) ? true : (tested.Kind & actual.Kind) == 0 ? false : null;
                    return matches is null || matches == positive;
                })) : null;
            TypeKind kind = positive ? value.Type.Kind & tested.Kind : value.Type.Kind & ~tested.Kind;
            if (denotations is null || !denotations.Types.IsEmpty) kind |= TypeKind.Type;
            else kind &= ~TypeKind.Type;
            HirTypeId represented = hir.Type(new TypeRef(kind, value.Type.Element));
            return value with { Type = hir.Types[represented.Value].RuntimeType, SemanticType = represented, Denotations = denotations };
        }
        HirTypeId source = value.SemanticType ?? hir.Type(value.Type);
        HirTypeId type = Narrow(source, tested, positive);
        return value with { Type = hir.Types[type.Value].RuntimeType, SemanticType = type };
    }

    public HirTypeId Union(HirTypeId first, HirTypeId second)
    {
        if (first == second) return first;
        if (first.Value > second.Value) (first, second) = (second, first);
        if (joins.TryGetValue((first, second), out var known)) return known;
        HirType left = hir.Types[first.Value], right = hir.Types[second.Value];
        if (left.RuntimeType.Kind == TypeKind.Never) return second;
        if (right.RuntimeType.Kind == TypeKind.Never) return first;
        TypeRef runtime = TypeRef.Union(left.RuntimeType, right.RuntimeType);
        HirTypeId result;
        if (left.RuntimeType == TypeRef.Null || right.RuntimeType == TypeRef.Null)
        {
            HirType payload = left.RuntimeType == TypeRef.Null ? right : left;
            result = hir.Type(payload with { RuntimeType = runtime, Nullable = true });
        }
        else if (left.Shape == right.Shape && left.Signature == right.Signature
            && left.Element == right.Element && left.TypeParameter == right.TypeParameter
            && left.Concept == right.Concept && left.Alternatives.IsDefaultOrEmpty && right.Alternatives.IsDefaultOrEmpty)
            result = hir.Type(left with { RuntimeType = runtime, Nullable = runtime.Nullable });
        else
        {
            var alternatives = Alternatives(first).Concat(Alternatives(second)).Distinct().OrderBy(item => item.Value).ToImmutableArray();
            result = hir.Type(new HirType(runtime, Alternatives: alternatives));
        }
        joins[(first, second)] = result;
        return result;
    }

    public HirTypeId Narrow(HirTypeId source, TypeRef tested, bool positive)
    {
        var key = (source, tested, positive);
        if (refinements.TryGetValue(key, out var known)) return known;
        HirType description = hir.Types[source.Value];
        HirTypeId result;
        if (!description.Alternatives.IsDefaultOrEmpty)
        {
            result = hir.Type(new TypeRef(TypeKind.Never));
            foreach (HirTypeId alternative in description.Alternatives)
                result = Union(result, Narrow(alternative, tested, positive));
        }
        else
        {
            TypeKind kind = positive ? description.RuntimeType.Kind & tested.Kind
                : description.RuntimeType.Kind & ~tested.Kind;
            if (!positive && tested.Element is not null && (description.RuntimeType.Kind & TypeKind.Range) != 0)
                kind |= TypeKind.Range;
            TypeRef runtime = new(kind, description.RuntimeType.Element);
            result = hir.Type(description with { RuntimeType = runtime, Nullable = runtime.Nullable });
        }
        refinements[key] = result;
        return result;
    }

    public HirTypeId Element(HirTypeId source)
    {
        HirType type = hir.Types[source.Value];
        if (!type.Alternatives.IsDefaultOrEmpty)
        {
            HirTypeId result = hir.Type(new TypeRef(TypeKind.Never));
            foreach (HirTypeId alternative in type.Alternatives) result = Union(result, Element(alternative));
            return result;
        }
        return type.Element >= 0 ? new(type.Element) : hir.Type(type.RuntimeType.Element ?? TypeRef.Any);
    }

    public FlowFact Element(FlowFact value)
    {
        HirTypeId type = Element(value.SemanticType ?? hir.Type(value.Type));
        return new(hir.Types[type.Value].RuntimeType, SemanticType: type, Denotations: value.Denotations);
    }

    public FlowFact Constrain(FlowFact value, FlowLocationDomain domain)
    {
        HirTypeId source = value.SemanticType ?? hir.Type(value.Type);
        HirType projected = hir.Types[source.Value];
        HirType storage = hir.Types[(domain.StorageSemanticType ?? hir.Type(domain.StorageType)).Value];
        TypeRef runtime = new(value.Type.Kind & domain.StorageType.Kind,
            domain.StorageType.Element ?? value.Type.Element);
        HirTypeId result = hir.Type(projected with { RuntimeType = runtime, Nullable = runtime.Nullable,
            Shape = projected.Shape >= 0 ? projected.Shape : storage.Shape,
            Signature = projected.Signature >= 0 ? projected.Signature : storage.Signature,
            Element = projected.Element >= 0 ? projected.Element : storage.Element });
        return value with { Type = runtime, SemanticType = result };
    }

    public HirTypeId Member(HirTypeId source, string name)
    {
        HirType description = hir.Types[source.Value];
        if (!description.Alternatives.IsDefaultOrEmpty)
        {
            HirTypeId result = hir.Type(new TypeRef(TypeKind.Never));
            foreach (HirTypeId alternative in description.Alternatives) result = Union(result, Member(alternative, name));
            return result;
        }
        if (description.Shape >= 0)
            foreach (HirField field in hir.ScopeLayouts[description.Shape].Fields)
                if (field.Name == name) return field.StorageType;
        return hir.Type(TypeRef.Any);
    }

    private IEnumerable<HirTypeId> Alternatives(HirTypeId type)
    {
        var alternatives = hir.Types[type.Value].Alternatives;
        return alternatives.IsDefaultOrEmpty ? [type] : alternatives;
    }
}
