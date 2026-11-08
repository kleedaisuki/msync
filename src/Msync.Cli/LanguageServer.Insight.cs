using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private static bool ResolvesDocumentation(JsonElement parameters)
    {
        foreach (string name in new[] { "capabilities", "textDocument", "completion", "completionItem", "resolveSupport", "properties" })
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(name, out parameters)) return false;
        return parameters.ValueKind == JsonValueKind.Array
            && parameters.EnumerateArray().Any(property => property.ValueKind == JsonValueKind.String && property.GetString() == "documentation");
    }

    private JsonObject ResolveCompletion(JsonElement parameters)
    {
        JsonObject item = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        if (!parameters.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 3
            || data[0].ValueKind != JsonValueKind.String || data[0].GetString() != "msync") return item;
        long generation = data[1].GetInt64();
        var document = documents.Values.FirstOrDefault(candidate => candidate.Generation == generation);
        if (document?.Analysis is null) return item;
        string label = parameters.GetProperty("label").GetString()!;
        var member = document.Analysis.Resolve(data[2].GetInt32(), label);
        if (member is null && label.StartsWith('$'))
            member = Msync.Compiler.EditorCompiler.BuiltinBindings.FirstOrDefault(candidate => candidate.Name == label);
        if (member is not null) item["documentation"] = Documentation(member.Description);
        return item;
    }

    private static JsonNode? Hover(EditorDocument document, JsonElement parameters)
    {
        int offset = Offset(document.Source, parameters.GetProperty("position"));
        var occurrence = document.Analysis?.Occurrences
            .Where(item => item.Span.Start <= offset && offset < item.Span.End)
            .MinBy(item => item.Span.Length);
        if (occurrence is not null)
            return new JsonObject
            {
                ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"```msync\n{occurrence.Signature}\n```\n\n{occurrence.Description}" },
                ["range"] = document.Source.Range(occurrence.Span.Start, occurrence.Span.Length)
            };
        string word = Word(document.Source.Text, offset);
        string? help = word switch
        {
            "scope" => "```msync\nscope Name { member: value, optional?: null }\n```\n\nConstruct a lexical Scope; access or assign fields with `Name::member`. Field values are mutable; their inferred types stay fixed. Add `?` to admit Null. A Scope is not a filesystem Connection.",
            "fn" => "```msync\nfn name(parameter: Type) -> Type { result }\n```\n\nOmit annotations for generic inference. A final expression returns its value; `;` discards it. Closures share captured binding locations, so later assignments are visible.",
            "let" => "```msync\nlet name = value;\nname = replacement;\n```\n\nBind a mutable value with a fixed inferred or declared type. A new `let` shadows rather than updates the previous binding. Use `let name? = value;` to admit Null.",
            "decltype" => "```msync\ndecltype(expression)\n```\n\nInfer the expression’s type without executing its effects.",
            "null" => "`Null` is a value type. Use `value is null` to test and refine a nullable binding; postfix `value?` also means **is null**.",
            _ => null
        };
        return help is null ? null : new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = help } };
    }

    private static JsonArray CodeActions(string uri, EditorDocument document, JsonElement parameters)
    {
        JsonArray actions = [];
        if (document.Analysis?.Diagnostic is not { Fix: { } fix } diagnostic) return actions;
        if (diagnostic.SourceName != uri) return actions;
        JsonElement requested = parameters.GetProperty("range");
        int start = Offset(document.Source, requested.GetProperty("start"));
        int end = Offset(document.Source, requested.GetProperty("end"));
        if (end < diagnostic.Span.Start || start > diagnostic.Span.End) return actions;
        actions.Add((JsonNode)new JsonObject
        {
            ["title"] = fix.Title,
            ["kind"] = "quickfix",
            ["isPreferred"] = true,
            ["edit"] = new JsonObject
            {
                ["documentChanges"] = new JsonArray(new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = document.Version },
                    ["edits"] = new JsonArray(new JsonObject { ["range"] = document.Source.Range(fix.Span.Start, fix.Span.Length), ["newText"] = fix.Replacement })
                })
            }
        });
        return actions;
    }
}
