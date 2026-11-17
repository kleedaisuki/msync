using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private void RequireArgumentConcepts(HirTemplate template, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext caller)
    {
        for (int index = 0; index < template.Parameters.Length; index++)
        {
            var parameter = hir.Locations[template.Parameters[index].Value];
            var type = hir.Types[parameter.StorageType.Value];
            if (type.Concept < 0) continue;
            var argument = arguments[index];
            var argumentSource = argument.Node.Value >= 0 ? hir.Nodes[argument.Node.Value].Source : source;
            RequireConcept(argument, type.Concept, argumentSource, caller,
                parameter.AdmitsNull || type.Nullable || type.RuntimeType.Nullable);
        }
    }

    private ImmutableDictionary<HirTypeId, HirTypeId> RequireConcept(SemanticValue value, int conceptId, HirSourceRange source,
        SemanticContext context, bool nullable = false)
    {
        if (nullable && Runtime(value) == TypeRef.Null) return ImmutableDictionary<HirTypeId, HirTypeId>.Empty;
        if (Runtime(value).Nullable && !nullable)
            throw ConceptError(conceptId, source, "a non-null Scope", Runtime(value).ToString());
        if (value.Scope is not { } shape)
            throw ConceptError(conceptId, source, "a structural Scope", Runtime(value).ToString());
        if (context.Objects.TryGetValue(shape.Region, out var current)) shape = current;
        var definition = program.Concepts[conceptId];
        var associated = ImmutableDictionary.CreateBuilder<HirTypeId, HirTypeId>();
        Dictionary<HirTypeId, SemanticValue>? witnesses = definition.HasBounds ? [] : null;
        foreach (var (name, parameter) in definition.AssociatedTypes)
            if (TryScopeField(shape, name, context, out var witness) && witness.DenotedType is { } type)
                associated[parameter] = type;

        List<(HirTypeId Type, SemanticValue Value)>? nullOnly = null;
        foreach (var (_, member) in ConceptRequirements(conceptId))
        {
            var actual = RequiredMember(shape, member, conceptId, source, context);
            if (member.Signature >= 0) InferOperationParameters(member, actual, associated, context, witnesses);
            else if (InferAssociated(member.Type, actual, associated, context, witnesses, includeNull: false) is { } candidate)
                (nullOnly ??= []).Add(candidate);
        }
        // Null-only evidence is a fallback, after fields and operation
        // parameters have both contributed their non-null evidence.
        if (nullOnly is not null)
            foreach (var (type, evidence) in nullOnly)
                InferAssociated(type, evidence, associated, context, witnesses, includeNull: true);

        var substitutions = associated.ToImmutable();
        CheckConceptBounds(conceptId, substitutions, source, context, witnesses);
        foreach (var (owner, member) in ConceptRequirements(conceptId))
        {
            try { shape = EstablishConceptStorage(shape, member, substitutions, source, context); }
            catch (CompileException error)
            { throw ConceptError(owner, source, $"member '{member.Name}'", error.Diagnostic.Message); }
            var actual = RequiredMember(shape, member, owner, source, context);
            CheckConceptMember(actual, member, owner, substitutions, source, context);
            var contract = new SemanticMemberContract(owner, member, substitutions);
            foreach (int region in ScopeAliases(shape))
            {
                var key = (region, member.Name);
                var contracts = context.MemberContracts.GetValueOrDefault(key, []);
                if (!contracts.Contains(contract)) context.MemberContracts[key] = contracts.Add(contract);
            }
        }
        return substitutions;
    }

    private void CheckConceptBounds(int concept, ImmutableDictionary<HirTypeId, HirTypeId> associated,
        HirSourceRange source, SemanticContext context, Dictionary<HirTypeId, SemanticValue>? witnesses)
    {
        var definition = program.Concepts[concept];
        if (!definition.ChecksArguments) return;
        foreach (int parent in definition.Bases)
            CheckConceptBounds(parent, associated, source, context, witnesses);
        if (definition.Parameters.IsEmpty && definition.Bounds.IsEmpty) return;
        if (Trace is null) CheckOwnConceptBounds(definition, associated, source, context, witnesses);
        else Trace.Run("compiler.semantic.concept_arguments", (self: this, definition, associated, source, context, witnesses),
            static state => state.self.CheckOwnConceptBounds(state.definition, state.associated, state.source, state.context, state.witnesses),
            new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, source)) { ["concept"] = definition.Name });
    }

    private void CheckOwnConceptBounds(BoundConcept definition, ImmutableDictionary<HirTypeId, HirTypeId> associated,
        HirSourceRange source, SemanticContext context, Dictionary<HirTypeId, SemanticValue>? witnesses)
    {
        Trace?.Metric("concept.parameters.inferred", definition.Parameters.Length);
        Trace?.Metric("concept.bounds", definition.Bounds.Length);
        var probe = context.QueryFork();
        foreach (var pair in associated) probe.Substitutions[pair.Key] = pair.Value;
        foreach (var parameter in definition.Parameters)
        {
            var resolved = ResolveHirType(parameter, probe);
            if (hir.Types[resolved.Value].TypeParameter >= 0)
                throw ConceptError(definition.Id, source, "a concrete type argument", "no type evidence");
        }
        foreach (var (parameter, bound) in definition.Bounds)
        {
            var argument = ResolveHirType(parameter, probe);
            var constraint = ResolveHirType(bound, probe);
            try
            {
                var actual = hir.Types[argument.Value];
                var expected = hir.Types[constraint.Value];
                if (actual.Concept < 0 || expected.Concept < 0
                    || !expected.RuntimeType.Accepts(actual.RuntimeType)
                    || !DerivesConcept(actual.Concept, expected.Concept))
                    Require(witnesses?.GetValueOrDefault(argument) ?? ValueForType(argument), constraint, source, context: probe);
            }
            catch (CompileException error)
            {
                Trace?.Metric("concept.bounds.rejected", 1);
                throw ConceptError(definition.Id, source, $"type argument satisfying {hir.Types[constraint.Value].RuntimeType}",
                    error.Diagnostic.Message);
            }
        }
    }

    private bool DerivesConcept(int concept, int expected) => concept == expected
        || program.Concepts[concept].Bases.Any(parent => DerivesConcept(parent, expected));

    private IEnumerable<(int Concept, HirConceptMember Member)> ConceptRequirements(int concept)
    {
        var definition = hir.Concepts[concept];
        foreach (int parent in definition.Bases)
            foreach (var member in ConceptRequirements(parent)) yield return member;
        foreach (var member in definition.Members) yield return (concept, member);
    }

    private SemanticValue RequiredMember(SemanticScope scope, HirConceptMember requirement,
        int concept, HirSourceRange source, SemanticContext context)
    {
        if (!TryScopeField(scope, requirement.Name, context, out var actual))
            throw ConceptError(concept, source, $"member '{requirement.Name}'", "missing member");
        int field = FieldOffset(scope.Layout, requirement.Name);
        if (field >= 0)
        {
            var declared = hir.ScopeLayouts[scope.Layout].Fields[field];
            var type = scope.Locations.TryGetValue(requirement.Name, out var location)
                ? StorageType(location, context) : ResolveHirType(declared.StorageType, context);
            if (declared.AdmitsNull) type = Nullable(type);
            actual = Runtime(actual) == TypeRef.Null && requirement.Nullable
                ? actual with { StorageConstraint = type } : actual with { Type = type, StorageConstraint = type };
        }
        return actual;
    }

    private SemanticScope EstablishConceptStorage(SemanticScope scope, HirConceptMember member,
        ImmutableDictionary<HirTypeId, HirTypeId> associated, HirSourceRange source, SemanticContext context)
    {
        if (member.Signature >= 0 || !TryScopeField(scope, member.Name, context, out var current)
            || Runtime(current) != TypeRef.Null) return scope;
        scope = context.Objects.GetValueOrDefault(scope.Region, scope);
        int offset = FieldOffset(scope.Layout, member.Name);
        if (offset < 0) return scope;
        var field = hir.ScopeLayouts[scope.Layout].Fields[offset];
        HirLocationId? location = scope.Locations.TryGetValue(member.Name, out var cell) ? cell : null;
        var storage = location is { } actual ? StorageType(actual, context) : ResolveHirType(field.StorageType, context);
        if (hir.Types[storage.Value].TypeParameter < 0) return scope;
        var requirements = context.QueryFork();
        foreach (var pair in associated) requirements.Substitutions[pair.Key] = pair.Value;
        var target = ResolveHirType(member.Type, requirements);
        if (member.Nullable) target = Nullable(target);
        var expected = hir.Types[target.Value];
        if (expected.TypeParameter >= 0 || expected.RuntimeType.WithoutNull == TypeRef.Any.WithoutNull
            || !ClosedConceptConstraint(target)) return scope;
        // A Null-only initializer leaves a cell inference variable, not an Any
        // annotation. Seal that cell without changing Null or another instance.
        var constraint = field.AdmitsNull ? Nullable(target) : target;
        Require(current, target, source, context: requirements);
        if (location is { } pending)
        {
            RegisterStorageAlias(pending, pending, context);
            SealStorage(pending, constraint, context);
        }
        var fields = hir.ScopeLayouts[scope.Layout].Fields.SetItem(offset, field with { StorageType = constraint });
        scope = scope with { Layout = InternShape(fields) };
        if (scope.Region >= 0) context.Objects[scope.Region] = scope;
        return scope;
    }

    private bool ClosedConceptConstraint(HirTypeId type)
    {
        var description = hir.Types[type.Value];
        if (description.TypeParameter >= 0) return false;
        if (description.Element < 0 && description.Shape < 0 && description.Signature < 0
            && description.Alternatives.IsDefaultOrEmpty) return true;
        return ClosedConceptConstraint(type, []);
    }

    private bool ClosedConceptConstraint(HirTypeId type, HashSet<HirTypeId> visited)
    {
        if (!visited.Add(type)) return true;
        var description = hir.Types[type.Value];
        if (description.TypeParameter >= 0) return false;
        if (description.Element >= 0 && !ClosedConceptConstraint(new(description.Element), visited)) return false;
        if (description.Shape >= 0)
            foreach (var field in hir.ScopeLayouts[description.Shape].Fields)
                if (!ClosedConceptConstraint(field.StorageType, visited)) return false;
        if (description.Signature >= 0)
        {
            var signature = hir.Signatures[description.Signature];
            if (!ClosedConceptConstraint(signature.Result, visited)) return false;
            foreach (var parameter in signature.Parameters)
                if (!ClosedConceptConstraint(parameter, visited)) return false;
        }
        if (!description.Alternatives.IsDefault)
            foreach (var alternative in description.Alternatives)
                if (!ClosedConceptConstraint(alternative, visited)) return false;
        return true;
    }

    private (HirTypeId Type, SemanticValue Value)? InferAssociated(HirTypeId requirement, SemanticValue actual,
        ImmutableDictionary<HirTypeId, HirTypeId>.Builder associated, SemanticContext context,
        Dictionary<HirTypeId, SemanticValue>? witnesses, bool includeNull)
    {
        var type = hir.Types[requirement.Value];
        if (hir.Types[actual.Type.Value].TypeParameter >= 0) return null;
        if (type.TypeParameter >= 0)
        {
            foreach (var candidate in associated.Keys)
                if (hir.Types[candidate.Value].TypeParameter == type.TypeParameter) return null;
            var runtime = Runtime(actual);
            if (!includeNull && runtime.WithoutNull.Kind == TypeKind.Never) return (requirement, actual);
            HirTypeId witness = actual.Type;
            // A sample's writable carrier can be Scope-only while its logical witness has fields.
            if (actual.Scope is { } scope && hir.Types[witness.Value].Shape < 0)
                witness = hir.Type(hir.Types[witness.Value] with { Shape = scope.Layout });
            if (runtime != TypeRef.Null && (type.Nullable || type.RuntimeType.Nullable)) witness = NonNullable(witness);
            associated[requirement] = witness;
            if (actual.Scope is not null) witnesses?.TryAdd(witness, actual with { Type = witness });
            return null;
        }
        if (type.Element >= 0 && actual.Element is { } element)
            return InferAssociated(new(type.Element), element, associated, context, witnesses, includeNull);
        if (type.Concept >= 0 && actual.Scope is { } row && !program.Concepts[type.Concept].Arguments.IsEmpty)
            foreach (var (_, member) in ConceptRequirements(type.Concept))
            {
                if (!TryScopeField(row, member.Name, context, out var field)) continue;
                if (member.Signature >= 0) InferOperationParameters(member, field, associated, context, witnesses);
                else InferAssociated(member.Type, field, associated, context, witnesses, includeNull);
            }
        return null;
    }

    private void InferOperationParameters(HirConceptMember member, SemanticValue actual,
        ImmutableDictionary<HirTypeId, HirTypeId>.Builder associated, SemanticContext context,
        Dictionary<HirTypeId, SemanticValue>? witnesses)
    {
        var signature = hir.Signatures[member.Signature];
        foreach (var callable in Choices(actual))
        {
            if (callable.Template is not { } templateId) continue;
            var template = hir.Templates[templateId.Value];
            for (int index = 0; index < Math.Min(signature.Parameters.Length, template.Parameters.Length); index++)
            {
                var parameter = hir.Locations[template.Parameters[index].Value];
                if (hir.Types[parameter.StorageType.Value].TypeParameter >= 0) continue;
                InferAssociated(signature.Parameters[index], ValueForType(parameter.StorageType), associated, context, witnesses, false);
            }
            if (template.ResultAnnotation is { } result)
                InferAssociated(signature.Result, ValueForType(result), associated, context, witnesses, false);
        }
    }

    private void CheckConceptMember(SemanticValue actual, HirConceptMember member, int concept,
        ImmutableDictionary<HirTypeId, HirTypeId> associated, HirSourceRange source, SemanticContext context)
    {
        SemanticContext probe = context.QueryFork();
        foreach (var pair in associated) probe.Substitutions[pair.Key] = pair.Value;
        if (member.Signature < 0)
        {
            var target = ResolveHirType(member.Type, probe);
            if (member.Nullable) target = Nullable(target);
            try
            {
                if (Runtime(actual) == TypeRef.Null && actual.StorageConstraint is { } carrier
                    && hir.Types[carrier.Value].TypeParameter < 0)
                {
                    HirType storage = hir.Types[carrier.Value];
                    if (!hir.Types[target.Value].RuntimeType.Accepts(storage.RuntimeType) || storage.Shape >= 0)
                        Require(ValueForType(carrier), target, source, context: probe);
                }
                Require(hir.Types[target.Value].Shape >= 0 && actual.Scope is not null
                    ? actual with { Type = PlanType(actual) } : actual, target, source, context: probe);
                if (program.Concepts[concept].InvariantFields.Contains(member.Name) && actual.StorageConstraint is { } declaredStorage
                    && hir.Types[declaredStorage.Value].TypeParameter < 0)
                {
                    var expected = hir.Types[target.Value];
                    var supplied = hir.Types[declaredStorage.Value];
                    if (!expected.RuntimeType.Accepts(supplied.RuntimeType) || !supplied.RuntimeType.Accepts(expected.RuntimeType)
                        || expected.Concept >= 0 && supplied.Concept >= 0 && expected.Concept != supplied.Concept)
                        throw Error(source, $"Writable member '{member.Name}' must keep its declared storage type.", "E0214");
                    if (expected.Shape >= 0 && supplied.Shape >= 0 && expected.Shape != supplied.Shape)
                        RequireStructure(ValueForType(target), declaredStorage, source, false, [], probe);
                }
            }
            catch (CompileException error)
            {
                throw ConceptError(concept, source, $"'{member.Name}: {hir.Types[target.Value].RuntimeType}'", error.Diagnostic.Message);
            }
            return;
        }
        if (Runtime(actual).WithoutNull != TypeRef.Function || Runtime(actual).Nullable && !member.Nullable)
            throw ConceptError(concept, source, $"callable member '{member.Name}'", Runtime(actual).ToString());
        var signature = hir.Signatures[member.Signature];
        var arguments = signature.Parameters.Select(type => ValueForType(ResolveHirType(type, probe))).ToArray();
        SemanticValue result;
        try { result = InvokeType(actual, arguments, source, probe); }
        catch (CompileException error)
        {
            throw ConceptError(concept, source, $"'{member.Name}' accepting ({string.Join(", ", arguments.Select(Runtime))})", error.Diagnostic.Message);
        }
        var returnType = ResolveHirType(signature.Result, probe);
        try { Require(result, returnType, source, context: probe); }
        catch (CompileException error)
        {
            throw ConceptError(concept, source, $"'{member.Name}' returning {hir.Types[returnType.Value].RuntimeType}", error.Diagnostic.Message);
        }
    }

    private void CheckMemberContracts(SemanticScope scope, string name, SemanticValue value,
        HirSourceRange source, SemanticContext context)
    {
        foreach (int region in ScopeAliases(scope))
            foreach (var contract in context.MemberContracts.GetValueOrDefault((region, name), []))
                CheckConceptMember(value, contract.Requirement, contract.Concept, contract.Associated, source, context);
    }

    private static IEnumerable<int> ScopeAliases(SemanticScope scope)
    {
        if (scope.Region >= 0) yield return scope.Region;
        if (!scope.Aliases.IsDefault)
            foreach (int alias in scope.Aliases)
                if (alias != scope.Region) yield return alias;
    }

    private void InvalidateScopeAliases(SemanticScope written, string field, SemanticValue assigned, SemanticContext context)
    {
        if (!written.Dynamic && written.Aliases.IsDefaultOrEmpty && !context.AliasViews.ContainsKey(written.Region)) return;
        var aliases = ScopeAliases(written).ToHashSet();
        HashSet<int> affected = [];
        foreach (int alias in aliases)
        {
            if (context.AliasViews.TryGetValue(alias, out var views)) affected.UnionWith(views);
            if (written.Dynamic) affected.Add(alias);
        }
        foreach (int region in affected)
        {
            if (region == written.Region || !context.Objects.TryGetValue(region, out var candidate)) continue;
            if (!TryScopeField(candidate, field, context, out var current)) continue;
            candidate = context.Objects.GetValueOrDefault(region, candidate);
            var value = Join(current, assigned) with { Literal = null, DenotedType = null,
                TypeCandidates = null, BranchValue = null, Observation = null };
            context.Objects[region] = candidate with { Fields = candidate.Fields.SetItem(field, value) };
            if (candidate.Locations.TryGetValue(field, out var location)) context.Values[location] = value;
        }
    }

    private CompileException ConceptError(int concept, HirSourceRange source, string expected, string actual)
    {
        var definition = hir.Concepts[concept];
        var error = SemanticDiagnosticFactory.Concept(hir.Sources[source.Source.Value], new(source.Start, source.Length),
            $"Value does not satisfy concept '{definition.Name}': expected {expected}, found {actual}.");
        var origin = hir.Sources[definition.Source.Source.Value];
        error.Diagnostic.Related.Add(new(origin.Name, origin.Text ?? "",
            new(definition.Source.Start, definition.Source.Length), $"concept '{definition.Name}' declared here"));
        return error;
    }
}
