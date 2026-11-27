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
                scope = new(-1, JoinScopeLayouts(a.Layout, b.Layout), fields, ImmutableDictionary<string, HirLocationId>.Empty,
                    Named: a.Named && b.Named, Dynamic: a.Dynamic || b.Dynamic, Aliases: ScopeAliases(a).Concat(ScopeAliases(b)).Distinct().ToImmutableArray(),
                    FieldDefinitions: JoinFieldDefinitions(a, b, fields.Keys));
            }
        }
        SemanticValue? element = first.Element is null ? second.Element : second.Element is null
            ? first.Element : Join(first.Element, second.Element);
        if (element is not null && (OpaqueElements(first) || OpaqueElements(second)))
            element = WithoutLayoutFacts(element);
        var type = runtime.WithoutNull == TypeRef.Scope
            ? new HirFlowTypeAlgebra(hir).Union(SchemaType(first), SchemaType(second))
            : hir.Type(new HirType(runtime, Shape: scope?.Layout ?? -1,
                Element: element?.Type.Value ?? -1)
            {
                ScopeDomain = HirScopeDomain.Join(hir.Types[first.Type.Value], hir.Types[second.Type.Value])
            });
        var callables = Choices(first).Concat(Choices(second)).Distinct(SemanticCallableIdentity.Instance).ToImmutableArray();
        return new(type, first.Node, first.Literal == second.Literal ? first.Literal : null,
            callables, scope, element, EagerRange: first.EagerRange && second.EagerRange,
            IndexedRegion: payload?.IndexedRegion ?? (first.IndexedRegion == second.IndexedRegion ? first.IndexedRegion : null),
            DenotedType: first.DenotedType == second.DenotedType ? first.DenotedType : null,
            TypeCandidates: runtime == TypeRef.Type && (first.DenotedType is null || first.DenotedType != second.DenotedType)
                ? FlowDenotationDomain.Join(first.DenotationDomain, second.DenotationDomain) : null,
            DeferredEffects: CombineDeferredEffects(first.DeferredEffects, second.DeferredEffects),
            IndexedAliases: payload?.IndexedAliases ?? JoinIndexedResources(first, second),
            OpenSessionCalls: CombineSessionCalls(first.OpenSessionCalls, second.OpenSessionCalls),
            StorageConstraint: first.StorageConstraint is { } leftStorage && second.StorageConstraint is { } rightStorage
                ? new HirFlowTypeAlgebra(hir).Union(InferredStorageType(leftStorage), InferredStorageType(rightStorage))
                : payload?.StorageConstraint,
            RuntimeDependentMetadata: first.RuntimeDependentMetadata || second.RuntimeDependentMetadata,
            CheckedType: first.CheckedType is { } checkedFirst && second.CheckedType is { } checkedSecond
                ? new HirFlowTypeAlgebra(hir).Union(checkedFirst, checkedSecond) : payload?.CheckedType,
            RangeRecipe: first.RangeRecipe == second.RangeRecipe ? first.RangeRecipe : null);
    }

    private int JoinScopeLayouts(int left, int right)
    {
        if (left == right) return left;
        var rightFields = hir.ScopeLayouts[right].Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        HirFlowTypeAlgebra algebra = new(hir);
        return InternShape([.. hir.ScopeLayouts[left].Fields.Where(field => rightFields.ContainsKey(field.Name))
            .Select(field =>
            {
                var other = rightFields[field.Name];
                return field with
                {
                    StorageType = algebra.Union(field.StorageType, other.StorageType),
                    AdmitsNull = field.AdmitsNull || other.AdmitsNull
                };
            })]);
    }

    private HirTypeId SchemaType(SemanticValue value)
    {
        HirType type = hir.Types[value.Type.Value];
        if (type.Shape >= 0 || type.RuntimeType.WithoutNull != TypeRef.Scope || value.StorageConstraint is not { } constraint)
            return value.Type;
        HirType storage = hir.Types[constraint.Value];
        if (storage.Shape < 0 && storage.Alternatives.IsDefaultOrEmpty) return value.Type;
        if (!type.Alternatives.IsDefaultOrEmpty)
        {
            HirFlowTypeAlgebra algebra = new(hir);
            return type.Alternatives.Select(alternative => SchemaType(new(alternative, HirNodeId.None, StorageConstraint: constraint)))
                .Aggregate(algebra.Union);
        }
        return hir.Type(storage with { RuntimeType = type.RuntimeType, Nullable = type.Nullable });
    }

    private bool OpaqueElements(SemanticValue value) => value.Element is null
        && (Runtime(value).Kind & TypeKind.Range) != 0
        && Runtime(value).Element?.Kind != TypeKind.Never;

    private SemanticValue WithoutLayoutFacts(SemanticValue value)
    {
        var type = StructuralStorageType(value.Type);
        var element = value.Element is { } item ? WithoutLayoutFacts(item) : null;
        return type == value.Type && ReferenceEquals(element, value.Element) ? value : value with { Type = type, Element = element };
    }

    // A structural view constrains member types, not the allocation's field offsets.
    private HirTypeId ViewOf(HirTypeId contract, HirTypeId value)
    {
        var view = hir.Types[contract.Value];
        var layouts = hir.Types[value.Value].ScopeDomain;
        if ((view.RuntimeType.Kind & TypeKind.Scope) == 0 || view.ScopeDomain.Equals(layouts)) return contract;
        Trace?.Metric("object.views.physical_domain_transfers", 1);
        return hir.Type(view with { ScopeDomain = layouts });
    }

    private static ImmutableDictionary<string, ImmutableArray<HirSourceRange>>? JoinFieldDefinitions(
        SemanticScope first, SemanticScope second, IEnumerable<string> members)
    {
        if (first.FieldDefinitions is null) return second.FieldDefinitions;
        if (second.FieldDefinitions is null || ReferenceEquals(first.FieldDefinitions, second.FieldDefinitions))
            return first.FieldDefinitions;
        return members.ToImmutableDictionary(name => name, name =>
            first.FieldDefinitions.GetValueOrDefault(name, []).AddRange(second.FieldDefinitions.GetValueOrDefault(name, []))
                .Distinct().ToImmutableArray(), StringComparer.Ordinal);
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
        ? [] : value.Callables.Length == 1 ? value.Callables : value.Callables.Distinct(SemanticCallableIdentity.Instance);

    private int Shape(ImmutableDictionary<string, SemanticValue> fields)
    {
        var layout = fields.Select(pair => new HirField(pair.Key, pair.Value.Type,
            hir.Types[pair.Value.Type.Value].RuntimeType.Nullable)).ToImmutableArray();
        return hir.InternScopeLayout(new(layout));
    }

    private HirTypeId ResolveHirTypeShallow(HirTypeId type, SemanticContext context)
    {
        bool nullable = hir.Types[type.Value].Nullable;
        type = Resolve(type, context);
        if (nullable) type = Nullable(type);
        var description = hir.Types[type.Value];
        if (description.TypeParameter < 0) return type;
        if (program.TypeQueries.ContainsKey(description.TypeParameter) || program.TypeQueryMembers.ContainsKey(description.TypeParameter))
            return ResolveTypeQuery(description.TypeParameter, nullable, context);
        if (program.TypeWitnesses.TryGetValue(description.TypeParameter, out var witness))
        {
            HirLocationId location = context.Location(witness.Location);
            SemanticValue? value = context.Values.GetValueOrDefault(location);
            if (value?.DenotedType is not { } denoted)
                throw Error(witness.Source, $"Type annotation '{witness.Name}' requires a statically known Type value.", "E0202");
            if (value.TypeRead is { } observed) observed.RequiredAt ??= witness.Source;
            HirType represented = hir.Types[ResolveHirType(denoted, context).Value];
            return hir.Type(represented with
            {
                RuntimeType = description.Nullable ? represented.RuntimeType.WithNull : represented.RuntimeType,
                Nullable = description.Nullable || represented.Nullable
            });
        }
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

    private bool TypeWitnessAvailable(HirTypeId type, SemanticContext context)
    {
        var description = hir.Types[type.Value];
        return !program.TypeWitnesses.TryGetValue(description.TypeParameter, out var witness)
            || context.Values.GetValueOrDefault(context.Location(witness.Location))?.DenotedType is not null;
    }

    private bool AdmitsNull(HirLocationId source, HirTypeId type) => hir.Locations[source.Value].AdmitsNull
        || program.TryDeclaration(source, out var declaration) && declaration.Annotation is not null
            && hir.Types[type.Value].RuntimeType.Nullable;

    private HirTypeId StorageTypeFor(HirLocationId location, SemanticValue value, SemanticContext context)
    {
        var definition = hir.Locations[location.Value];
        HirTypeId type = ResolveHirType(definition.StorageType, context);
        var description = hir.Types[type.Value];
        var source = context.Definitions.GetValueOrDefault(location, location);
        bool inferredStorage = program.TryDeclaration(source, out var declaration) && declaration.Annotation is null;
        if (inferredStorage && Runtime(value).WithoutNull.Kind == TypeKind.Never
            && (value.StorageConstraint is not null || value.Location is not null))
        {
            HirTypeId carrier = value.StorageConstraint ?? StorageType(value.Location!.Value, context);
            if (hir.Types[carrier.Value].TypeParameter < 0
                && hir.Types[carrier.Value].RuntimeType.WithoutNull.Kind != TypeKind.Never)
                type = carrier;
        }
        if ((inferredStorage || description.TypeParameter >= 0)
            && Runtime(value).WithoutNull.Kind != TypeKind.Never)
        {
            var inferred = hir.Types[value.Type.Value];
            type = hir.Type(inferred with
            {
                RuntimeType = definition.AdmitsNull ? inferred.RuntimeType.WithNull : inferred.RuntimeType,
                Nullable = definition.AdmitsNull || inferred.Nullable
            });
            if (hir.Types[definition.StorageType.Value].TypeParameter >= 0)
                context.Substitutions[definition.StorageType] = type;
        }
        type = inferredStorage ? InferredStorageType(type) : StructuralStorageType(type);
        return definition.AdmitsNull ? Nullable(type) : type;
    }

    // A by-value input supplies its structural row, not the caller's reaching-
    // definition alternatives. Captured storage constraints remain separate.
    private HirTypeId PlanType(SemanticValue value)
    {
        if (value.Scope is not { } scope) return value.Type;
        HirType type = hir.Types[value.Type.Value];
        return hir.Type(type with { Shape = scope.Layout, ScopeDomain = default, Alternatives = default });
    }

    private HirTypeId InferredStorageType(HirTypeId type)
    {
        HirType description = hir.Types[StructuralStorageType(type).Value];
        return description.RuntimeType.WithoutNull == TypeRef.Scope
            ? hir.Type(new HirType(description.RuntimeType, Nullable: description.Nullable)) : StructuralStorageType(type);
    }

    // Storage admission is structural. A later legal write can replace a
    // value with a wider layout, including inside an admitted Range.
    private HirTypeId StructuralStorageType(HirTypeId id)
    {
        var type = hir.Types[id.Value];
        int element = type.Element < 0 ? -1 : StructuralStorageType(new(type.Element)).Value;
        ImmutableArray<HirTypeId>.Builder? alternatives = null;
        for (int i = 0; i < type.Alternatives.AsSpan().Length; i++)
        {
            var previous = type.Alternatives[i];
            var structural = StructuralStorageType(previous);
            if (structural != previous) (alternatives ??= type.Alternatives.ToBuilder())[i] = structural;
        }
        if (type.ScopeDomain.Count == 0 && element == type.Element && alternatives is null) return id;
        return hir.Type(type with
        {
            ScopeDomain = default,
            Element = element,
            Alternatives = alternatives is null ? type.Alternatives : alternatives.MoveToImmutable()
        });
    }

    private void Require(SemanticValue value, HirTypeId target, HirSourceRange source,
        bool allowImplicit = false, SemanticContext? context = null)
    {
        if (context is not null)
        {
            target = ResolveHirType(target, context);
            if (TypeWitnessAvailable(value.Type, context)) value = value with { Type = ResolveHirType(value.Type, context) };
        }
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
        RequireStructure(value, target, source, allowImplicit, [], context);
    }

    private static bool AcceptsRuntime(TypeRef expected, TypeRef actual, bool implicitConversion) =>
        expected.Accepts(actual) || implicitConversion && MetaTypes.IsConvertible(actual, expected);

    private void RequireStructure(SemanticValue value, HirTypeId target, HirSourceRange source,
        bool allowImplicit, HashSet<(HirTypeId Expected, HirTypeId Actual)> visited, SemanticContext? context)
    {
        if (!visited.Add((target, value.Type))) return;
        var expected = hir.Types[target.Value];
        if (Runtime(value) == TypeRef.Null && expected.RuntimeType.Nullable) return;
        HirType supplied = hir.Types[value.Type.Value];
        if (!supplied.Alternatives.IsDefaultOrEmpty)
        {
            foreach (HirTypeId alternative in supplied.Alternatives)
                RequireStructure(ValueForType(alternative), target, source, allowImplicit, visited, context);
            return;
        }
        if (expected.Shape >= 0)
        {
            var actual = value.Scope;
            if (actual is not null && context?.Objects.TryGetValue(actual.Region, out var current) == true) actual = current;
            var actualType = hir.Types[value.Type.Value];
            foreach (var field in hir.ScopeLayouts[expected.Shape].Fields)
            {
                SemanticValue? member = actual?.Fields.GetValueOrDefault(field.Name);
                if (actualType.Shape >= 0)
                {
                    var found = hir.ScopeLayouts[actualType.Shape].Fields.FirstOrDefault(item => item.Name == field.Name);
                    if (found is not null && (member is null || hir.Types[found.StorageType.Value].Shape >= 0))
                        member = ValueForType(found.AdmitsNull ? Nullable(found.StorageType) : found.StorageType);
                }
                if (member is null) throw Error(source, $"Object is missing required field '{field.Name}'.", "E0205");
                HirTypeId requiredType = field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType;
                var required = hir.Types[requiredType.Value];
                if (!AcceptsRuntime(required.RuntimeType, Runtime(member), implicitConversion: false))
                    throw Error(source, $"Field '{field.Name}' requires {required.RuntimeType}, found {Runtime(member)}.", "E0203");
                // Scope references admit compatible carriers here. Nested layout
                // obligations belong to member projection, not a heap-graph walk:
                // aliases may change the referent after this admission.
            }
        }
        if (expected.Element >= 0 && value.Element is { } element)
            RequireStructure(element, new(expected.Element), source, false, visited, context);
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
        SemanticValue value = new(type, HirNodeId.None, StorageConstraint: type);
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
            if (description.RuntimeType.WithoutNull == TypeRef.Scope)
                value = value with { Scope = AlternativeScope(type, visited) ?? value.Scope };
        }
        if (description.Shape >= 0) value = value with { Scope = ScopeForLayout(description.Shape, visited) };
        if (description.Element >= 0) value = value with { Element = ValueForType(new(description.Element), visited) };
        visited[type] = value;
        return value;

    }

    private SemanticScope ScopeForLayout(int layout, Dictionary<HirTypeId, SemanticValue> visited)
    {
        var fields = hir.ScopeLayouts[layout].Fields.ToImmutableDictionary(field => field.Name,
            field => ValueForType(field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType, visited), StringComparer.Ordinal);
        return new(-1, layout, fields, ImmutableDictionary<string, HirLocationId>.Empty);
    }

    private SemanticScope? AlternativeScope(HirTypeId type, Dictionary<HirTypeId, SemanticValue> visited)
    {
        List<HirScopeLayout> rows = [];
        if (!CollectScopeRows(type, rows) || rows.Count == 0) return null;
        HirFlowTypeAlgebra algebra = new(hir);
        ImmutableArray<HirField>.Builder fields = ImmutableArray.CreateBuilder<HirField>();
        foreach (HirField first in rows[0].Fields)
        {
            HirField?[] members = rows.Select(row => row.Fields.FirstOrDefault(field => field.Name == first.Name)).ToArray();
            if (members.Any(member => member is null)) continue;
            HirTypeId storage = members.Select(member => member!.AdmitsNull ? Nullable(member.StorageType) : member.StorageType)
                .Aggregate(algebra.Union);
            fields.Add(new(first.Name, storage, members.Any(member => member!.AdmitsNull)));
        }
        return ScopeForLayout(hir.InternScopeLayout(new(fields.ToImmutable())), visited);
    }

    private bool CollectScopeRows(HirTypeId type, List<HirScopeLayout> rows)
    {
        HirType description = hir.Types[type.Value];
        if (description.RuntimeType == TypeRef.Null) return true;
        if (!description.Alternatives.IsDefaultOrEmpty)
            return description.Alternatives.All(alternative => CollectScopeRows(alternative, rows));
        if (description.Shape < 0) return false;
        rows.Add(hir.ScopeLayouts[description.Shape]);
        return true;
    }
}
