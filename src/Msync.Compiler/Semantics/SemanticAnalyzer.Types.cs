using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Join(SemanticValue first, SemanticValue second)
    {
        if (Runtime(first).Kind == TypeKind.Never) return second;
        if (Runtime(second).Kind == TypeKind.Never) return first;
        if (first == second) return first;
        var runtime = TypeRef.Union(Runtime(first), Runtime(second));
        SemanticValue? payload = Runtime(first) == TypeRef.Null ? second
            : Runtime(second) == TypeRef.Null ? first : null;
        SemanticScope? scope = payload?.Scope;
        if (payload is null && first.Scope is { } a && second.Scope is { } b)
        {
            if (a.Region >= 0 && a.Region == b.Region) scope = a;
            else
            {
                var fields = a.Fields.Where(pair => b.Fields.ContainsKey(pair.Key)).ToImmutableDictionary(
                    pair => pair.Key, pair => Join(pair.Value, b.Fields[pair.Key]), StringComparer.Ordinal);
                scope = new(-1, Shape(fields), fields, ImmutableDictionary<string, HirLocationId>.Empty,
                    Dynamic: a.Dynamic || b.Dynamic, Aliases: ScopeAliases(a).Concat(ScopeAliases(b)).Distinct().ToImmutableArray());
            }
        }
        SemanticValue? element = first.Element is null ? second.Element : second.Element is null
            ? first.Element : Join(first.Element, second.Element);
        var type = hir.Type(new HirType(runtime, Shape: scope?.Layout ?? -1,
            Element: element?.Type.Value ?? -1));
        var callables = Choices(first).Concat(Choices(second)).Distinct().ToImmutableArray();
        return new(type, first.Node, first.Literal == second.Literal ? first.Literal : null,
            callables, scope, element, EagerRange: first.EagerRange && second.EagerRange,
            Alternatives: [first, second],
            IndexedRegion: payload?.IndexedRegion ?? (first.IndexedRegion == second.IndexedRegion ? first.IndexedRegion : null),
            DenotedType: first.DenotedType == second.DenotedType ? first.DenotedType : null,
            DeferredEffects: CombineDeferredEffects(first.DeferredEffects, second.DeferredEffects),
            IndexedAliases: payload?.IndexedAliases ?? JoinIndexedResources(first, second),
            OpenSessionCalls: CombineSessionCalls(first.OpenSessionCalls, second.OpenSessionCalls));
    }

    private static int IndexedResourceCount(SemanticValue value) => value.IndexedRegion is not null ? 1
        : value.IndexedAliases.IsDefault ? 0 : value.IndexedAliases.Length;

    private static int IndexedResourceAt(SemanticValue value, int index) => value.IndexedRegion ?? value.IndexedAliases[index];

    private static int IndexedCarrierCount(SemanticIndexed storage) => storage.FixedCarrier is not null ? 1
        : storage.CarrierConstraints.IsDefault ? 0 : storage.CarrierConstraints.Length;

    private static HirTypeId IndexedCarrierAt(SemanticIndexed storage, int index) =>
        storage.FixedCarrier ?? storage.CarrierConstraints[index];

    private SemanticIndexed JoinIndexed(SemanticIndexed first, SemanticIndexed second)
    {
        var element = Join(first.Element, second.Element);
        int firstCount = IndexedCarrierCount(first), secondCount = IndexedCarrierCount(second);
        if (firstCount == 0) return second with { Element = element };
        if (secondCount == 0) return first with { Element = element };
        if (first.FixedCarrier is { } carrier && second.FixedCarrier == carrier)
            return new(element, carrier);
        var constraints = ImmutableArray.CreateBuilder<HirTypeId>(firstCount + secondCount);
        for (int index = 0; index < firstCount; index++) constraints.Add(IndexedCarrierAt(first, index));
        for (int index = 0; index < secondCount; index++)
        {
            var next = IndexedCarrierAt(second, index);
            if (!constraints.Contains(next)) constraints.Add(next);
        }
        constraints.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return constraints.Count == 1 ? new(element, constraints[0]) : new(element, CarrierConstraints: constraints.ToImmutable());
    }

    private static ImmutableArray<int> JoinIndexedResources(SemanticValue first, SemanticValue second)
    {
        int firstCount = IndexedResourceCount(first), secondCount = IndexedResourceCount(second);
        if (firstCount == 0 || secondCount == 0 || first.IndexedRegion is { } region && second.IndexedRegion == region) return [];
        if (first.IndexedRegion is null && second.IndexedRegion is null
            && first.IndexedAliases.AsSpan().SequenceEqual(second.IndexedAliases.AsSpan())) return first.IndexedAliases;
        var regions = ImmutableArray.CreateBuilder<int>(firstCount + secondCount);
        int left = 0, right = 0;
        while (left < firstCount || right < secondCount)
        {
            if (right == secondCount || left < firstCount && IndexedResourceAt(first, left) < IndexedResourceAt(second, right))
                regions.Add(IndexedResourceAt(first, left++));
            else if (left == firstCount || IndexedResourceAt(second, right) < IndexedResourceAt(first, left))
                regions.Add(IndexedResourceAt(second, right++));
            else
            {
                regions.Add(IndexedResourceAt(first, left++));
                right++;
            }
        }
        return regions.ToImmutable();
    }

    private static IEnumerable<SemanticCallable> Choices(SemanticValue value) => value.Callables.IsDefaultOrEmpty
        ? [] : value.Callables.Length == 1 ? value.Callables : value.Callables.Distinct(NativeCallableIdentity.Instance);

    private sealed class NativeCallableIdentity : IEqualityComparer<SemanticCallable>
    {
        public static NativeCallableIdentity Instance { get; } = new();
        public bool Equals(SemanticCallable? first, SemanticCallable? second) =>
            first?.NativeName is { } name && second?.NativeName is not null
                ? name == second.NativeName && first.Template == second.Template : first == second;
        public int GetHashCode(SemanticCallable value) => value.NativeName is { } name
            ? HashCode.Combine(name, value.Template) : value.GetHashCode();
    }

    private int Shape(ImmutableDictionary<string, SemanticValue> fields)
    {
        var layout = fields.Select(pair => new HirField(pair.Key, pair.Value.Type,
            hir.Types[pair.Value.Type.Value].RuntimeType.Nullable)).ToImmutableArray();
        return hir.InternScopeLayout(new(layout));
    }

    private HirTypeId ResolveHirType(HirTypeId type, SemanticContext context)
    {
        type = Resolve(type, context);
        var description = hir.Types[type.Value];
        if (description.TypeParameter < 0) return type;
        foreach (var (parameter, result) in context.Substitutions)
        {
            if (hir.Types[parameter.Value].TypeParameter != description.TypeParameter) continue;
            var resolved = hir.Types[Resolve(result, context).Value];
            return hir.Type(resolved with
            {
                RuntimeType = description.Nullable ? resolved.RuntimeType.WithNull : resolved.RuntimeType,
                Nullable = description.Nullable || resolved.Nullable
            });
        }
        return type;
    }

    private HirTypeId StorageTypeFor(HirLocationId location, SemanticValue value, SemanticContext context)
    {
        var definition = hir.Locations[location.Value];
        HirTypeId type = ResolveHirType(definition.StorageType, context);
        var description = hir.Types[type.Value];
        if (description.TypeParameter >= 0 && Runtime(value).WithoutNull.Kind != TypeKind.Never)
        {
            var inferred = hir.Types[value.Type.Value];
            type = hir.Type(inferred with
            {
                RuntimeType = definition.AdmitsNull ? inferred.RuntimeType.WithNull : inferred.RuntimeType,
                Nullable = definition.AdmitsNull || inferred.Nullable
            });
            context.Substitutions[definition.StorageType] = type;
        }
        return definition.AdmitsNull ? Nullable(type) : type;
    }

    private void Require(SemanticValue value, HirTypeId target, HirSourceRange source,
        bool allowImplicit = false, SemanticContext? context = null)
    {
        if (context is not null) target = ResolveHirType(target, context);
        var expected = hir.Types[target.Value];
        if (expected.TypeParameter >= 0)
        {
            if (context is not null && Runtime(value).WithoutNull.Kind != TypeKind.Never)
                context.Substitutions[target] = expected.Nullable ? NonNullable(value.Type) : value.Type;
            return;
        }
        if (!expected.Alternatives.IsDefaultOrEmpty)
        {
            foreach (var alternative in expected.Alternatives)
                if (AcceptsRuntime(hir.Types[alternative.Value].RuntimeType, Runtime(value), allowImplicit))
                {
                    Require(value, alternative, source, allowImplicit, context);
                    return;
                }
        }
        if (!AcceptsRuntime(expected.RuntimeType, Runtime(value), allowImplicit))
            throw SemanticDiagnosticFactory.TypeMismatch(hir.Sources[source.Source.Value],
                new(source.Start, source.Length), Runtime(value), expected.RuntimeType);
        if (expected.Concept >= 0)
        {
            RequireConcept(value, expected.Concept, source, context ?? new(new(-1)), expected.RuntimeType.Nullable);
            return;
        }
        RequireStructure(value, target, source, allowImplicit, []);
    }

    private static bool AcceptsRuntime(TypeRef expected, TypeRef actual, bool implicitConversion) =>
        expected.Accepts(actual) || implicitConversion && MetaTypes.IsConvertible(actual, expected);

    private void RequireStructure(SemanticValue value, HirTypeId target, HirSourceRange source,
        bool allowImplicit, HashSet<(HirTypeId Expected, HirTypeId Actual)> visited)
    {
        if (!visited.Add((target, value.Type))) return;
        var expected = hir.Types[target.Value];
        if (Runtime(value) == TypeRef.Null && expected.RuntimeType.Nullable) return;
        if (expected.Shape >= 0)
        {
            var actual = value.Scope;
            var actualType = hir.Types[value.Type.Value];
            foreach (var field in hir.ScopeLayouts[expected.Shape].Fields)
            {
                SemanticValue? member = actual?.Fields.GetValueOrDefault(field.Name);
                if (member is null && actualType.Shape >= 0)
                {
                    var found = hir.ScopeLayouts[actualType.Shape].Fields.FirstOrDefault(item => item.Name == field.Name);
                    if (found is not null) member = ValueForType(found.StorageType);
                }
                if (member is null) throw Error(source, $"Scope is missing required field '{field.Name}'.", "E0205");
                var required = hir.Types[field.StorageType.Value];
                if (!AcceptsRuntime(required.RuntimeType, Runtime(member), implicitConversion: false))
                    throw Error(source, $"Field '{field.Name}' requires {required.RuntimeType}, found {Runtime(member)}.", "E0203");
                RequireStructure(member, field.StorageType, source, false, visited);
            }
        }
        if (expected.Element >= 0 && value.Element is { } element)
            RequireStructure(element, new(expected.Element), source, false, visited);
    }

    private SemanticValue Convert(SemanticValue value, HirTypeId target, HirSourceRange source, SemanticContext context)
    {
        target = ResolveHirType(target, context);
        var expected = hir.Types[target.Value];
        if (expected.TypeParameter >= 0 || expected.RuntimeType.Accepts(Runtime(value))) return value;
        if (!MetaTypes.IsConvertible(Runtime(value), expected.RuntimeType))
            throw Error(source, $"Cannot implicitly convert {Runtime(value)} to {expected.RuntimeType}.", "E0203");
        return value with
        {
            Type = target,
            Node = context.Publish ? context.Output!.Add(HirKind.Coerce, target, source, [value.Node], data: target.Value) : HirNodeId.None,
            Literal = value.Literal is { Kind: HirLiteralKind.String } text && expected.RuntimeType.WithoutNull == TypeRef.Path
                ? new(HirLiteralKind.Path, Text: text.Text)
                : value.Literal is { Kind: HirLiteralKind.Path } path && expected.RuntimeType.WithoutNull == TypeRef.String
                    ? new(HirLiteralKind.String, Text: path.Text) : null,
            Location = null
        };
    }

    private HirTypeId NonNullable(HirTypeId type)
    {
        var description = hir.Types[type.Value];
        return hir.Type(description with { RuntimeType = description.RuntimeType.WithoutNull, Nullable = false });
    }

    private SemanticValue ValueForType(HirTypeId type) => ValueForType(type, []);

    private SemanticValue ValueForType(HirTypeId type, Dictionary<HirTypeId, SemanticValue> visited)
    {
        if (visited.TryGetValue(type, out var known)) return known;
        var description = hir.Types[type.Value];
        SemanticValue value = new(type, HirNodeId.None);
        visited[type] = value;
        if (!description.Alternatives.IsDefaultOrEmpty)
        {
            SemanticValue? joined = null;
            foreach (HirTypeId alternative in description.Alternatives)
            {
                SemanticValue item = ValueForType(alternative, visited);
                joined = joined is null ? item : Join(joined, item);
            }
            value = joined! with { Type = type };
        }
        if (description.Shape >= 0)
        {
            var fields = hir.ScopeLayouts[description.Shape].Fields.ToImmutableDictionary(
                field => field.Name, field => ValueForType(field.StorageType, visited), StringComparer.Ordinal);
            value = value with { Scope = new(-1, description.Shape, fields, ImmutableDictionary<string, HirLocationId>.Empty) };
        }
        if (description.Element >= 0) value = value with { Element = ValueForType(new(description.Element), visited) };
        visited[type] = value;
        return value;
    }
}
