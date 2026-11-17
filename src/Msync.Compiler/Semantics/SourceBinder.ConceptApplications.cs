using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private HirTypeId ConceptType(int concept, TypeSyntax syntax)
    {
        if (syntax.Arguments.Count == 0) return hir.Type(new HirType(TypeRef.Scope, Concept: concept));
        if (Trace is null) return BindConceptApplication(concept, syntax);
        return Trace.MeasureDetailed("compiler.bind.concept_application", (self: this, concept, syntax),
            static state => state.self.BindConceptApplication(state.concept, state.syntax),
            attributes: new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, Range(syntax.Span)))
            { ["concept"] = program.Concepts[concept].Name });
    }

    private HirTypeId BindConceptApplication(int concept, TypeSyntax syntax)
    {
        Trace?.Metric("concept.arguments", syntax.Arguments.Count);
        var definition = program.Concepts[concept];
        if (syntax.Arguments.Count != definition.Parameters.Length)
            throw Error($"Concept '{definition.Name}' expects {definition.Parameters.Length} type arguments.", syntax.Span, "E0214");
        var arguments = syntax.Arguments.Select(Type).ToImmutableArray();
        int applied = ApplyConcept(concept, arguments);
        CheckRuntimeBounds(applied, syntax.Span);
        return hir.Type(new HirType(TypeRef.Scope, Concept: applied));
    }

    private void CheckRuntimeBounds(int concept, SourceSpan span)
    {
        var definition = program.Concepts[concept];
        foreach (var (parameter, bound) in definition.Bounds)
        {
            var argument = hir.Types[parameter.Value];
            var expected = hir.Types[bound.Value];
            if (!HasOpenCarrier(parameter) && !HasOpenCarrier(bound)
                && !expected.RuntimeType.Accepts(argument.RuntimeType))
            {
                Trace?.Metric("concept.bounds.rejected", 1);
                throw Error($"Type argument {argument.RuntimeType} does not satisfy {expected.RuntimeType} in concept '{definition.Name}'.", span, "E0214");
            }
        }
        foreach (int parent in definition.Bases) CheckRuntimeBounds(parent, span);
    }

    private bool HasOpenCarrier(HirTypeId id)
    {
        var type = hir.Types[id.Value];
        return type.TypeParameter >= 0 || type.Element >= 0 && HasOpenCarrier(new(type.Element))
            || !type.Alternatives.IsDefaultOrEmpty && type.Alternatives.Any(HasOpenCarrier);
    }

    private int ApplyConcept(int definitionId, ImmutableArray<HirTypeId> arguments)
    {
        var key = (definitionId, string.Join(',', arguments.Select(argument => argument.Value)));
        if (program.ConceptApplications.TryGetValue(key, out int known))
        {
            Trace?.Metric("concept.applications.reused", 1);
            return known;
        }

        var definition = program.Concepts[definitionId];
        var template = hir.Concepts[definitionId];
        var substitutions = definition.Parameters.Zip(arguments).ToDictionary(
            pair => hir.Types[pair.First.Value].TypeParameter, pair => pair.Second);
        var memo = new Dictionary<HirTypeId, HirTypeId>();

        HirTypeId Replace(HirTypeId id)
        {
            if (memo.TryGetValue(id, out var knownType)) return knownType;
            var type = hir.Types[id.Value];
            if (type.TypeParameter >= 0 && substitutions.TryGetValue(type.TypeParameter, out var replacement))
            {
                var substituted = hir.Types[replacement.Value];
                return type.Nullable ? hir.Type(substituted with { Nullable = true, RuntimeType = substituted.RuntimeType.WithNull }) : replacement;
            }
            var revised = type;
            if (type.Element >= 0)
            {
                var element = Replace(new(type.Element));
                revised = revised with { Element = element.Value, RuntimeType = new(type.RuntimeType.Kind, hir.Types[element.Value].RuntimeType) };
            }
            if (!type.Alternatives.IsDefaultOrEmpty)
            {
                var alternatives = type.Alternatives.Select(Replace).ToImmutableArray();
                revised = revised with
                {
                    Alternatives = alternatives,
                    RuntimeType = alternatives.Select(item => hir.Types[item.Value].RuntimeType).Aggregate(TypeRef.Union)
                };
            }
            if (type.Nullable) revised = revised with { RuntimeType = revised.RuntimeType.WithNull };
            if (type.Signature >= 0) revised = revised with { Signature = ReplaceSignature(type.Signature) };
            if (type.Concept >= 0) revised = revised with { Concept = ReplaceConcept(type.Concept) };
            return memo[id] = revised == type ? id : hir.Type(revised);
        }

        int ReplaceSignature(int signatureId)
        {
            var signature = hir.Signatures[signatureId];
            var inputs = signature.Parameters.Select(Replace).ToImmutableArray();
            var result = Replace(signature.Result);
            if (result == signature.Result && inputs.AsSpan().SequenceEqual(signature.Parameters.AsSpan())) return signatureId;
            int index = hir.Signatures.Count;
            hir.Signatures.Add(signature with { Parameters = inputs, Result = result });
            return index;
        }

        int ReplaceConcept(int id)
        {
            var applied = program.Concepts[id];
            if (applied.GenericDefinition < 0) return id;
            var replaced = applied.Arguments.Select(Replace).ToImmutableArray();
            return replaced.AsSpan().SequenceEqual(applied.Arguments.AsSpan())
                ? id : ApplyConcept(applied.GenericDefinition, replaced);
        }

        var bases = definition.Bases.Select(ReplaceConcept).ToImmutableArray();
        var members = template.Members.Select(member =>
        {
            var type = Replace(member.Type);
            return member with { Type = type, Signature = member.Signature >= 0 ? hir.Types[type.Value].Signature : -1 };
        }).ToImmutableArray();
        var requirements = definition.Requirements.Select(member => member with
        {
            Type = Replace(member.Type),
            Parameters = member.Parameters.IsDefault ? default : member.Parameters.Select(Replace).ToImmutableArray(),
            Result = member.Result is { } result ? Replace(result) : null
        }).ToImmutableArray();
        var associated = definition.AssociatedTypes
            .Where(item => !substitutions.ContainsKey(hir.Types[item.Value.Value].TypeParameter))
            .ToImmutableDictionary();
        var bounds = definition.Bounds.Select(pair => (Replace(pair.Argument), Replace(pair.Bound))).ToImmutableArray();
        int index = AddConcept(definition with
        {
            Id = hir.Concepts.Count, Bases = bases, Requirements = requirements, AssociatedTypes = associated,
            Parameters = [], Bounds = bounds, GenericDefinition = definitionId, Arguments = arguments
        }, members);
        program.ConceptApplications.Add(key, index);
        Trace?.Metric("concept.applications.created", 1);
        Trace?.Metric("concept.application_members", members.Length);
        return index;
    }

    private int AddConcept(BoundConcept concept, ImmutableArray<HirConceptMember> members)
    {
        bool hasBounds = !concept.Bounds.IsEmpty || concept.Bases.Any(parent => program.Concepts[parent].HasBounds);
        concept = concept with
        {
            HasBounds = hasBounds,
            ChecksArguments = hasBounds || !concept.Parameters.IsEmpty || concept.Bases.Any(parent => program.Concepts[parent].ChecksArguments)
        };
        hir.Concepts.Add(new(concept.Name, concept.Source, concept.Bases,
            concept.AssociatedTypes.Values.ToImmutableArray(), members));
        program.Concepts.Add(concept);
        return concept.Id;
    }

    private bool DependsOn(HirTypeId id, ImmutableArray<HirTypeId> parameters)
    {
        if (parameters.IsEmpty) return false;
        if (parameters.Contains(id)) return true;
        var type = hir.Types[id.Value];
        return type.TypeParameter >= 0 && parameters.Any(parameter => hir.Types[parameter.Value].TypeParameter == type.TypeParameter)
            || type.Element >= 0 && DependsOn(new(type.Element), parameters)
            || !type.Alternatives.IsDefaultOrEmpty && type.Alternatives.Any(item => DependsOn(item, parameters))
            || type.Concept >= 0 && program.Concepts[type.Concept].Arguments.Any(item => DependsOn(item, parameters));
    }
}
