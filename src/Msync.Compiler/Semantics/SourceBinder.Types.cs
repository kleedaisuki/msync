using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private HirTypeId Type(TypeSyntax syntax)
    {
        try { return TypeCore(syntax); }
        catch (CompileException error) when (editorMode)
        {
            program.Diagnostics.Add(error.Diagnostic);
            return OpenType(syntax.Name);
        }
    }

    private HirTypeId TypeCore(TypeSyntax syntax)
    {
        if (Trace is not null && (syntax.Complement is not null || syntax.Intersections.Count != 0 || syntax.Alternatives.Count != 0))
            Trace.Metric("type_operator.operands", (syntax.Complement is null ? 0 : 1) + syntax.Intersections.Count + syntax.Alternatives.Count);
        var context = annotationScope ?? scope;
        HirTypeId result;
        if (syntax.Query is { } query) result = TypeQuery(query);
        else if (syntax.Complement is { } complement) result = TypeComplement(Type(complement), complement.Span);
        else if (syntax.Group is { } group) result = Type(group);
        else if (syntax.Name == "_")
        {
            result = OpenType("_");
            program.LocalTypeHoles.Add(hir.Types[result.Value].TypeParameter);
        }
        else if (context.Associated.TryGetValue(syntax.Name, out var associated)) result = associated;
        else if (context.Concepts.TryGetValue(syntax.Name, out int concept))
        {
            result = ConceptType(concept, syntax);
        }
        else if (LanguageTypes.TryResolve(syntax.Name, out var builtin))
        {
            if (syntax.Name == "Range" && syntax.Arguments.Count == 1)
            {
                var element = Type(syntax.Arguments[0]);
                builtin = new(TypeKind.Range, hir.Types[element.Value].RuntimeType);
                result = hir.Type(new HirType(builtin, Element: element.Value));
            }
            else result = hir.Type(builtin);
        }
        else if (syntax.Name.StartsWith("$std::concepts::", StringComparison.Ordinal))
        {
            if (context.Values.TryGetValue("std", out var shadow))
            {
                var definition = program.Declaration(Original(shadow));
                var document = hir.Sources[definition.Source.Source.Value];
                throw SemanticDiagnosticFactory.ShadowedStandardType(hir.Sources[source.Value], syntax.Span,
                    $"Unknown type '{syntax.Name}': std is a lexical value here.",
                    new(document.Name, document.Text ?? "", new(definition.Source.Start, definition.Source.Length),
                        "this binding shadows the standard namespace"));
            }
            var concepts = BindStandardModule("concepts");
            string name = syntax.Name["$std::concepts::".Length..];
            if (!concepts.Concepts.TryGetValue(name, out int declared))
                throw Error($"Unknown concept '{syntax.Name}'.", syntax.Span, "E0202");
            result = ConceptType(declared, syntax);
        }
        else if (context.Values.TryGetValue(syntax.Name, out var witness))
        {
            result = OpenType(syntax.Name);
            HirLocationId location = Capture(witness, syntax.Name);
            program.TypeWitnesses.Add(hir.Types[result.Value].TypeParameter, new(location, Range(syntax.Span), syntax.Name));
            Observe(syntax.Span, location);
        }
        else throw Error($"Unknown type '{syntax.Name}'.", syntax.Span, "E0202");

        if (syntax.Nullable) result = NullableType(result);
        foreach (var intersection in syntax.Intersections)
            result = TypeIntersection(result, Type(intersection), syntax.Span);
        if (syntax.Alternatives.Count != 0)
            result = TypeUnion(syntax.Alternatives.Select(Type).Prepend(result));
        if (observations) program.TypeUses.Add(new(Range(syntax.Span), result));
        return result;
    }

    private HirTypeId NullableType(HirTypeId type)
    {
        var current = hir.Types[type.Value];
        return hir.Type(current with { RuntimeType = current.RuntimeType.WithNull, Nullable = true });
    }

    private HirTypeId TypeUnion(IEnumerable<HirTypeId> operands)
    {
        var alternatives = operands.Where(type => hir.Types[type.Value].RuntimeType.Kind != TypeKind.Never)
            .Distinct().ToImmutableArray();
        if (alternatives.Length == 0) return hir.Type(new TypeRef(TypeKind.Never));
        if (alternatives.Length == 1) return alternatives[0];
        if (alternatives.Length == 2)
        {
            if (hir.Types[alternatives[0].Value].RuntimeType == TypeRef.Null) return NullableType(alternatives[1]);
            if (hir.Types[alternatives[1].Value].RuntimeType == TypeRef.Null) return NullableType(alternatives[0]);
        }
        var runtime = alternatives.Select(type => hir.Types[type.Value].RuntimeType).Aggregate(TypeRef.Union);
        return hir.Type(new HirType(runtime, Alternatives: alternatives));
    }

    private HirTypeId TypeComplement(HirTypeId type, SourceSpan span)
    {
        if (!IsCarrierDomain(type))
            throw Error("Type complement requires a closed domain of builtin carriers.", span, "E0202");
        return hir.Type(new TypeRef(TypeKind.Any & ~hir.Types[type.Value].RuntimeType.Kind));
    }

    private bool IsCarrierDomain(HirTypeId type)
    {
        var description = hir.Types[type.Value];
        return description is { TypeParameter: < 0, Shape: < 0, Signature: < 0, Concept: < 0, Element: < 0 }
            && (description.Alternatives.IsDefaultOrEmpty || description.Alternatives.All(IsCarrierDomain));
    }

    private HirTypeId TypeIntersection(HirTypeId first, HirTypeId second, SourceSpan span)
    {
        if (first == second) return first;
        var left = hir.Types[first.Value];
        var right = hir.Types[second.Value];
        if (!left.Alternatives.IsDefaultOrEmpty)
            return TypeUnion(TypeAlternatives(left).Select(item => TypeIntersection(item, second, span)));
        if (!right.Alternatives.IsDefaultOrEmpty)
            return TypeUnion(TypeAlternatives(right).Select(item => TypeIntersection(first, item, span)));
        var kind = left.RuntimeType.Kind & right.RuntimeType.Kind;
        if (kind == TypeKind.Never || kind == TypeKind.Null) return hir.Type(new TypeRef(kind));
        if (left.TypeParameter >= 0 || right.TypeParameter >= 0)
            throw Error("This type intersection requires resolved operand types.", span, "E0202");
        if (IsCarrierDomain(first)) return WithCarrier(right, kind);
        if (IsCarrierDomain(second)) return WithCarrier(left, kind);
        if (left.Concept >= 0 && right.Concept >= 0)
        {
            int concept = IntersectConcepts(left.Concept, right.Concept, span);
            return WithCarrier(new(TypeRef.Scope, Concept: concept), kind);
        }
        if (left.Element >= 0 && right.Element >= 0)
        {
            var element = TypeIntersection(new(left.Element), new(right.Element), span);
            return WithCarrier(new(new(TypeKind.Range, hir.Types[element.Value].RuntimeType), Element: element.Value), kind);
        }
        throw Error("This type intersection requires builtin, Range, or concept operands.", span, "E0202");
    }

    private IEnumerable<HirTypeId> TypeAlternatives(HirType type) => type.Nullable
        ? type.Alternatives.Append(hir.Type(TypeRef.Null)) : type.Alternatives;

    private HirTypeId WithCarrier(HirType type, TypeKind kind) => hir.Type(type with
    {
        RuntimeType = type.RuntimeType with { Kind = kind },
        Nullable = (kind & TypeKind.Null) != 0
    });

    private readonly Dictionary<(int, int), int> intersectedConcepts = [];

    private int IntersectConcepts(int left, int right, SourceSpan span)
    {
        if (left == right) return left;
        if (left > right) (left, right) = (right, left);
        if (intersectedConcepts.TryGetValue((left, right), out int existing)) return existing;
        var first = program.Concepts[left];
        var second = program.Concepts[right];
        int id = AddConcept(new(hir.Concepts.Count, first.Name + " and " + second.Name, Range(span),
            [left, right], [], first.AssociatedTypes.SetItems(second.AssociatedTypes)), []);
        intersectedConcepts.Add((left, right), id);
        return id;
    }

    private HirNodeId Concept(ConceptDeclaration declaration)
    {
        var before = scope.Associated;
        var previousQueries = conceptQueryTypes;
        conceptQueryTypes = null;
        try
        {
            var parameters = ImmutableArray.CreateBuilder<HirTypeId>(declaration.Parameters.Count);
            foreach (var parameter in declaration.Parameters)
            {
                if (scope.Associated.ContainsKey(parameter.Name) || LanguageTypes.Contains(parameter.Name))
                    throw Error($"Duplicate or reserved concept parameter '{parameter.Name}'.", parameter.Span, "E0214");
                var type = OpenType(parameter.Name);
                scope.Associated = scope.Associated.Add(parameter.Name, type);
                parameters.Add(type);
                if (observations) program.TypeUses.Add(new(Range(parameter.Span), type));
            }
            var bounds = declaration.Parameters.Select((parameter, index) => (parameter, index))
                .Where(item => item.parameter.Bound is not null)
                .Select(item => (parameters[item.index], Type(item.parameter.Bound!))).ToImmutableArray();
            return BindConcept(declaration, parameters.MoveToImmutable(), bounds);
        }
        finally { scope.Associated = before; conceptQueryTypes = previousQueries; }
    }

    private HirNodeId BindConcept(ConceptDeclaration declaration, ImmutableArray<HirTypeId> parameters,
        ImmutableArray<(HirTypeId, HirTypeId)> bounds)
    {
        List<int> bases = [];
        foreach (var parent in declaration.Bases)
        {
            var parentType = Type(parent);
            int inherited = hir.Types[parentType.Value].Concept;
            if (inherited < 0)
                throw Error($"Unknown concept '{parent.Name}'.", parent.Span, "E0214");
            bases.Add(inherited);
            foreach (var (name, type) in program.Concepts[inherited].AssociatedTypes)
                if (!scope.Associated.ContainsKey(name)) scope.Associated = scope.Associated.Add(name, type);
        }
        foreach (var declared in declaration.Types)
        {
            if (scope.Associated.ContainsKey(declared.Name))
                throw Error($"Associated type '{declared.Name}' is declared more than once.", declared.Span, "E0214");
            scope.Associated = scope.Associated.Add(declared.Name, OpenType(declared.Name));
        }
        List<BoundRequirement> requirements = [];
        List<HirConceptMember> members = [];
        if (declaration.Members.Any(member => TypeSyntaxFacts.Annotations(member).Any(UsesTypeQuery)))
        {
            conceptQueryTypes = new(StringComparer.Ordinal);
            foreach (int parent in bases)
                foreach (var inherited in program.Concepts[parent].Requirements)
                    conceptQueryTypes.TryAdd(inherited.Name, inherited.Nullable ? NullableType(inherited.Type) : inherited.Type);
            foreach (var member in declaration.Members)
                conceptQueryTypes[member.Name] = OpenType("requirement " + member.Name);
        }
        foreach (var member in declaration.Members)
        {
            var origin = Range(member.NameSpan ?? member.Span);
            if (member.Type is { } annotation)
            {
                var type = Type(annotation);
                requirements.Add(new(member.Name, type, member.Nullable, origin,
                    DeclarationSource: Range(member.Span)));
                members.Add(new(member.Name, type, origin, Nullable: member.Nullable));
                RememberRequirement(member.Name, type, member.Nullable);
                continue;
            }
            var earlier = conceptQueryTypes;
            if (earlier is not null) conceptQueryTypes = new(earlier, StringComparer.Ordinal);
            var signatureArguments = ImmutableArray.CreateBuilder<HirTypeId>(member.Parameters!.Count);
            foreach (var parameter in member.Parameters)
            {
                var type = parameter.Type is null ? OpenType(parameter.Name) : Type(parameter.Type);
                if (parameter.Nullable) type = NullableType(type);
                signatureArguments.Add(type);
                if (conceptQueryTypes is not null) conceptQueryTypes[parameter.Name] = type;
            }
            var arguments = signatureArguments.MoveToImmutable();
            var result = Type(member.ReturnType!);
            conceptQueryTypes = earlier;
            int signature = hir.Signatures.Count;
            hir.Signatures.Add(new(arguments, result));
            var functionType = hir.Type(new HirType(TypeRef.Function, Signature: signature));
            requirements.Add(new(member.Name, functionType, member.Nullable, origin, arguments, result,
                member.Parameters!.Select(parameter => parameter.Name).ToImmutableArray(), Range(member.Span)));
            members.Add(new(member.Name, functionType, origin, signature, member.Nullable));
            RememberRequirement(member.Name, functionType, member.Nullable);
        }
        var own = scope.Associated;
        int id = AddConcept(new(hir.Concepts.Count, declaration.Name, Range(declaration.NameSpan ?? declaration.Span),
            bases.ToImmutableArray(), requirements.ToImmutableArray(), own)
        {
            Parameters = parameters,
            Bounds = bounds,
            InvariantFields = members.Where(member => member.Signature < 0 && DependsOn(member.Type, parameters))
                .Select(member => member.Name).ToImmutableHashSet(StringComparer.Ordinal)
        }, members.ToImmutableArray());
        scope.Concepts = scope.Concepts.SetItem(declaration.Name, id);
        return Null(declaration.Span);
    }
}
