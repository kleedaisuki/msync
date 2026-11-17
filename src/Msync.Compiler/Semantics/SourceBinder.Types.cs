using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;

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
        var context = annotationScope ?? scope;
        HirTypeId result;
        if (context.Associated.TryGetValue(syntax.Name, out var associated)) result = associated;
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

        if (syntax.Nullable)
        {
            var current = hir.Types[result.Value];
            result = hir.Type(current with { RuntimeType = current.RuntimeType.WithNull, Nullable = true });
        }
        if (syntax.Alternatives.Count != 0)
        {
            var alternatives = syntax.Alternatives.Select(Type).Prepend(result).ToImmutableArray();
            var runtime = alternatives.Select(type => hir.Types[type.Value].RuntimeType).Aggregate(TypeRef.Union);
            result = hir.Type(new HirType(runtime, Alternatives: alternatives));
        }
        if (observations) program.TypeUses.Add(new(Range(syntax.Span), result));
        return result;
    }

    private HirNodeId Concept(ConceptDeclaration declaration)
    {
        var before = scope.Associated;
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
        finally { scope.Associated = before; }
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
        foreach (var member in declaration.Members)
        {
            var origin = Range(member.NameSpan ?? member.Span);
            if (member.Type is { } annotation)
            {
                var type = Type(annotation);
                requirements.Add(new(member.Name, type, member.Nullable, origin,
                    DeclarationSource: Range(member.Span)));
                members.Add(new(member.Name, type, origin, Nullable: member.Nullable));
                continue;
            }
            var arguments = member.Parameters!.Select(parameter =>
            {
                var type = parameter.Type is null ? OpenType(parameter.Name) : Type(parameter.Type);
                return parameter.Nullable ? hir.Type(hir.Types[type.Value] with
                { RuntimeType = hir.Types[type.Value].RuntimeType.WithNull, Nullable = true }) : type;
            }).ToImmutableArray();
            var result = Type(member.ReturnType!);
            int signature = hir.Signatures.Count;
            hir.Signatures.Add(new(arguments, result));
            var functionType = hir.Type(new HirType(TypeRef.Function, Signature: signature));
            requirements.Add(new(member.Name, functionType, member.Nullable, origin, arguments, result,
                member.Parameters!.Select(parameter => parameter.Name).ToImmutableArray(), Range(member.Span)));
            members.Add(new(member.Name, functionType, origin, signature, member.Nullable));
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
