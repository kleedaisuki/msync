using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private Dictionary<string, HirTypeId>? conceptQueryTypes;

    private static bool UsesTypeQuery(TypeSyntax type) => type.Query is not null
        || type.Group is { } group && UsesTypeQuery(group)
        || type.Complement is { } complement && UsesTypeQuery(complement)
        || type.Arguments.Any(UsesTypeQuery) || type.Intersections.Any(UsesTypeQuery) || type.Alternatives.Any(UsesTypeQuery);

    private void RememberRequirement(string name, HirTypeId type, bool nullable)
    {
        if (conceptQueryTypes is not { } names) return;
        var placeholder = names[name];
        var resolved = nullable ? NullableType(type) : type;
        program.TypeQueryMembers.Add(hir.Types[placeholder.Value].TypeParameter, resolved);
        names[name] = resolved;
    }

    private HirTypeId TypeQuery(Expr expression)
    {
        if (expression is NameExpr name && conceptQueryTypes?.TryGetValue(name.Name, out var direct) == true)
            return direct;
        var outer = scope;
        var input = ImmutableArray.CreateBuilder<(HirLocationId, HirTypeId)>();
        scope = new(outer, Range(expression.Span), outer.Template);
        try
        {
            if (conceptQueryTypes is { } names)
                foreach (string referenced in SyntaxTraversal.ReadNames(expression).Distinct(StringComparer.Ordinal))
                {
                    if (!names.TryGetValue(referenced, out var type)) continue;
                    var location = Declare(referenced, expression.Span, DeclarationKind.Local, type, nullable: true);
                    program.TypeQueryInputs.Add(location);
                    input.Add((location, type));
                }
            var operand = Expression(expression);
            var result = OpenType("decltype");
            program.TypeQueries.Add(hir.Types[result.Value].TypeParameter,
                new(operand, input.ToImmutable(), Range(expression.Span)));
            return result;
        }
        finally { scope = outer; }
    }
}
