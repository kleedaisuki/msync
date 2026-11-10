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

    private void RequireConcept(SemanticValue value, int conceptId, HirSourceRange source,
        SemanticContext context, bool nullable = false)
    {
        if (nullable && Runtime(value) == TypeRef.Null) return;
        if (Runtime(value).Nullable && !nullable)
            throw ConceptError(conceptId, source, "a non-null Scope", Runtime(value).ToString());
        if (value.Scope is not { } shape)
            throw ConceptError(conceptId, source, "a structural Scope", Runtime(value).ToString());
        if (context.Objects.TryGetValue(shape.Region, out var current)) shape = current;
        var associated = ImmutableDictionary.CreateBuilder<HirTypeId, HirTypeId>();
        var definition = hir.Concepts[conceptId];
        if (conceptId < program.Concepts.Count)
            foreach (var (name, parameter) in program.Concepts[conceptId].AssociatedTypes)
                if (shape.Fields.TryGetValue(name, out var witness) && witness.DenotedType is { } type)
                    associated[parameter] = type;

        foreach (var (_, member) in ConceptRequirements(conceptId))
        {
            var actual = RequiredMember(shape, member, conceptId, source, context);
            if (member.Signature < 0) InferAssociated(member.Type, actual, associated, includeNull: false);
            else InferOperationParameters(member, actual, associated);
        }
        foreach (var (_, member) in ConceptRequirements(conceptId))
            if (member.Signature < 0)
                InferAssociated(member.Type, RequiredMember(shape, member, conceptId, source, context), associated, includeNull: true);

        var substitutions = associated.ToImmutable();
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
    }

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
        if (!scope.Fields.TryGetValue(requirement.Name, out var actual))
            throw ConceptError(concept, source, $"member '{requirement.Name}'", "missing member");
        int field = FieldOffset(scope.Layout, requirement.Name);
        if (field >= 0)
        {
            var declared = hir.ScopeLayouts[scope.Layout].Fields[field];
            var type = scope.Locations.TryGetValue(requirement.Name, out var location)
                ? StorageType(location, context) : ResolveHirType(declared.StorageType, context);
            if (declared.AdmitsNull) type = Nullable(type);
            actual = actual with { Type = type };
        }
        return actual;
    }

    private SemanticScope EstablishConceptStorage(SemanticScope scope, HirConceptMember member,
        ImmutableDictionary<HirTypeId, HirTypeId> associated, HirSourceRange source, SemanticContext context)
    {
        if (member.Signature >= 0 || !scope.Fields.TryGetValue(member.Name, out var current)
            || Runtime(current) != TypeRef.Null) return scope;
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

    private void InferAssociated(HirTypeId requirement, SemanticValue actual,
        ImmutableDictionary<HirTypeId, HirTypeId>.Builder associated, bool includeNull)
    {
        var type = hir.Types[requirement.Value];
        if (hir.Types[actual.Type.Value].TypeParameter >= 0) return;
        if (type.TypeParameter >= 0)
        {
            if (!includeNull && Runtime(actual).WithoutNull.Kind == TypeKind.Never) return;
            var resolved = Runtime(actual) == TypeRef.Null ? actual.Type
                : type.Nullable || type.RuntimeType.Nullable ? NonNullable(actual.Type) : actual.Type;
            foreach (var candidate in associated.Keys)
                if (hir.Types[candidate.Value].TypeParameter == type.TypeParameter) return;
            associated[requirement] = resolved;
            return;
        }
        if (type.Element >= 0 && actual.Element is { } element)
            InferAssociated(new(type.Element), element, associated, includeNull);
    }

    private void InferOperationParameters(HirConceptMember member, SemanticValue actual,
        ImmutableDictionary<HirTypeId, HirTypeId>.Builder associated)
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
                InferAssociated(signature.Parameters[index], ValueForType(parameter.StorageType), associated, false);
            }
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
            try { Require(actual, target, source, context: probe); }
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

    private void InvalidateScopeAliases(SemanticScope written, SemanticContext context)
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
            var fields = candidate.Fields.ToImmutableDictionary(pair => pair.Key, pair =>
            {
                int offset = FieldOffset(candidate.Layout, pair.Key);
                var type = hir.ScopeLayouts[candidate.Layout].Fields[offset];
                return pair.Value with { Type = type.AdmitsNull ? Nullable(type.StorageType) : type.StorageType, Literal = null, DenotedType = null };
            }, StringComparer.Ordinal);
            context.Objects[region] = candidate with { Fields = fields };
            foreach (var (name, location) in candidate.Locations)
                context.Values[location] = fields[name];
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
