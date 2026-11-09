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
            result = hir.Type(new HirType(TypeRef.Scope, Concept: concept));
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
            if (context.Values.ContainsKey("std"))
                throw Error($"Unknown type '{syntax.Name}': std is a lexical value here.", syntax.Span, "E0202");
            var concepts = BindStandardModule("concepts");
            string name = syntax.Name["$std::concepts::".Length..];
            if (!concepts.Concepts.TryGetValue(name, out int declared))
                throw Error($"Unknown concept '{syntax.Name}'.", syntax.Span, "E0202");
            result = hir.Type(new HirType(TypeRef.Scope, Concept: declared));
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
        List<int> bases = [];
        foreach (var parent in declaration.Bases)
        {
            var parentType = Type(parent);
            int inherited = hir.Types[parentType.Value].Concept;
            if (inherited < 0)
                throw Error($"Unknown concept '{parent.Name}'.", parent.Span, "E0214");
            bases.Add(inherited);
            foreach (var (name, type) in program.Concepts[inherited].AssociatedTypes)
                scope.Associated = scope.Associated.SetItem(name, type);
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
        int id = hir.Concepts.Count;
        var own = scope.Associated;
        hir.Concepts.Add(new(declaration.Name, Range(declaration.NameSpan ?? declaration.Span),
            bases.ToImmutableArray(), own.Values.ToImmutableArray(), members.ToImmutableArray()));
        program.Concepts.Add(new(id, declaration.Name, Range(declaration.NameSpan ?? declaration.Span),
            bases.ToImmutableArray(), requirements.ToImmutableArray(), own));
        scope.Associated = before;
        scope.Concepts = scope.Concepts.SetItem(declaration.Name, id);
        return Null(declaration.Span);
    }
}
