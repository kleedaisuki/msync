using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private void CheckFormatCall(string name, SemanticValue[] inputs, HirSourceRange source, SemanticContext context)
    {
        if (inputs[0].Literal is not { Kind: HirLiteralKind.String, Text: { } template }) return;
        bool named = name == "text.format_fields";
        SemanticScope? scope = named && inputs[1].Scope is { } origin
            ? context.Objects.GetValueOrDefault(origin.Region, origin) : null;

        try
        {
            TextFormat.Reader reader = new(template, named);
            while (reader.MoveNext(out var field))
            {
                if (!field.IsField) continue;
                SemanticValue value;
                if (named)
                {
                    if (scope is null) continue;
                    string member = field.Name(template).ToString();
                    if (!scope.Fields.TryGetValue(member, out var known))
                    {
                        if (scope.Dynamic) continue;
                        throw TextFormat.Error(template, field.Offset, $"Scope has no member '{member}'");
                    }
                    value = scope.Locations.TryGetValue(member, out var location)
                        ? context.Values.GetValueOrDefault(location, known) : known;
                }
                else
                {
                    if (field.Argument >= inputs.Length - 1)
                        throw TextFormat.Error(template, field.Offset, $"argument {field.Argument} is missing; supplied {inputs.Length - 1}");
                    value = inputs[field.Argument + 1];
                }
                TextFormat.CheckType(template, field, Runtime(value));
                if (value.Literal is { Kind: HirLiteralKind.Number } number)
                    TextFormat.CheckNumber(template, field, number.Number);
            }
        }
        catch (MsyncException error)
        {
            HirSourceRange location = inputs[0].Node.IsNone ? source : hir.Nodes[inputs[0].Node.Value].Source;
            throw Error(location, error.Message, "E0230");
        }
    }
}
