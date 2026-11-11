using Msync.Compiler;
using Msync.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer(Func<string, string> format)
{
    private readonly Dictionary<string, EditorDocument> documents = new(StringComparer.Ordinal);
    private readonly JsonRpcTransport transport = new();
    private bool semanticRefreshSupported;
    private bool semanticRefreshPending;
    private bool semanticRefreshDirty;
    private bool hierarchicalDocumentSymbols;
    private bool completionResolveDocumentation;
    private long serverRequest;
    private static readonly string[] Keywords = ["let", "global", "fn", "scope", "concept", "typename", "as", "native", "if", "else", "for", "in", "match", "return", "break", "loop", "release", "acquire", "acqrel", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", .. LanguageTypes.Names];

    private JsonNode? Handle(string method, JsonElement p)
    {
        if (method == "initialize")
        {
            InitializeAuthentication(p);
            hierarchicalDocumentSymbols = p.TryGetProperty("capabilities", out var symbolCapabilities)
                && symbolCapabilities.TryGetProperty("textDocument", out var textDocumentCapabilities)
                && textDocumentCapabilities.TryGetProperty("documentSymbol", out var symbolOptions)
                && symbolOptions.TryGetProperty("hierarchicalDocumentSymbolSupport", out var hierarchy)
                && hierarchy.ValueKind == JsonValueKind.True;
            completionResolveDocumentation = ResolvesDocumentation(p);
            semanticRefreshSupported = p.TryGetProperty("capabilities", out var capabilities)
                && capabilities.TryGetProperty("workspace", out var workspace)
                && workspace.TryGetProperty("semanticTokens", out var tokens)
                && tokens.TryGetProperty("refreshSupport", out var refresh) && refresh.ValueKind == JsonValueKind.True;
            JsonNode response = JsonNode.Parse("""
            {"capabilities":{"positionEncoding":"utf-16","textDocumentSync":{"openClose":true,"change":2},"documentFormattingProvider":true,"codeActionProvider":true,"completionProvider":{"resolveProvider":true,"triggerCharacters":[":","$","/","\\","@"]},"hoverProvider":true,"definitionProvider":true,"documentSymbolProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["keyword","function","variable","type","string","comment","protocol","endpoint","operator","namespace","controlKeyword","regexp","number"],"tokenModifiers":[]},"full":true}},"serverInfo":{"name":"msync","version":""}}
            """)!;
            response["serverInfo"]!["version"] = ProductVersion.Current;
            return response;
        }
        if (method == "shutdown") return null;
        if (method == "completionItem/resolve") return ResolveCompletion(p);
        if (method == "msync/stdlibSource")
        {
            string uri = p.GetProperty("uri").GetString()!;
            return JsonValue.Create(StandardSource(uri));
        }
        string documentUri = p.TryGetProperty("textDocument", out JsonElement td) ? td.GetProperty("uri").GetString()! : "";
        EditorDocument document = documents.GetValueOrDefault(documentUri) ?? new("", 0, 0);
        string text = document.Source.Text;
        if (method == "textDocument/formatting")
        {
            try { return new JsonArray(new JsonObject { ["range"] = document.Source.Range(0, text.Length), ["newText"] = format(text) }); }
            catch (Msync.Compiler.Syntax.SyntaxException) { return new JsonArray(); }
        }
        if (method == "textDocument/completion") return Completions(document, p, lazyDocumentation: completionResolveDocumentation);
        if (method == "textDocument/semanticTokens/full") return SemanticTokens(document);
        if (method == "textDocument/documentSymbol") return Symbols(documentUri, document);
        if (method == "textDocument/definition") return Definition(document, p);
        if (method == "textDocument/hover") return Hover(document, p);
        if (method == "textDocument/codeAction") return CodeActions(documentUri, document, p);
        return null;
    }

    private static JsonArray Completions(EditorDocument document, JsonElement parameters, EditorContext? context = null,
        bool lazyDocumentation = false)
    {
        int offset = Offset(document.Source, parameters.GetProperty("position"));
        context ??= document.Analysis?.At(offset);
        if (document.Analysis?.IsTypePosition(offset) == true) return TypeCompletions(document.Source, offset, context);
        if (context?.IsGlobalImport == true)
            return NameCompletions(document.Source, offset,
                MemberCompletions(context.Bindings, document.Generation, offset, lazyDocumentation));
        JsonArray items = MemberCompletions(CompletionMembers(context).Where(member => !member.IsConstraint), document.Generation, offset, lazyDocumentation);
        if (context?.Receiver is not null && context.ReceiverKind == EditorReceiverKind.Member)
            return NameCompletions(document.Source, offset, items);
        foreach (string keyword in Keywords)
            items.Add((JsonNode)Completion(keyword, null, null, keyword == "global"
                ? "Explicitly import prior definition-time ancestor locations inside a function; no caller or future lookup." : null, keyword: true));
        return NameCompletions(document.Source, offset, items);
    }

    private static IEnumerable<SemanticMember> CompletionMembers(EditorContext? context) =>
        context?.Receiver is { } receiver && context.ReceiverKind == EditorReceiverKind.Member
            ? receiver.Members ?? []
            : (context?.Bindings ?? []).Concat(EditorCompiler.BuiltinBindings).DistinctBy(binding => binding.Name, StringComparer.Ordinal);

    private static JsonArray MemberCompletions(IEnumerable<SemanticMember> members, long generation, int offset, bool lazyDocumentation)
    {
        JsonArray items = [];
        foreach (var member in members)
        {
            JsonObject item = Completion(member.Name, member.Type, member.Signature,
                lazyDocumentation ? null : member.Description, semanticRole: member.SemanticRole);
            if (lazyDocumentation)
                item["data"] = new JsonArray(JsonValue.Create("msync"), JsonValue.Create(generation), JsonValue.Create(offset));
            items.Add((JsonNode)item);
        }
        return items;
    }

    private static JsonObject Completion(string name, string? type, string? signature, string? description, bool keyword = false, string? semanticRole = null)
    {
        JsonObject item = new()
        {
            ["label"] = name,
            ["kind"] = keyword ? 14 : semanticRole switch { "function" => 3, "type" => 7, _ => type is "Function" or "Function?" ? 3 : 6 }
        };
        if (signature is not null) item["detail"] = signature;
        if (description is not null) item["documentation"] = Documentation(description);
        return item;
    }

    private static JsonObject Documentation(string text) => new() { ["kind"] = "markdown", ["value"] = text };

    private static JsonArray SourceItems(SourceText source, SourceCompletionContext context, IReadOnlyList<SourceCompletionItem> candidates, JsonArray? items = null)
    {
        items ??= [];
        foreach (var item in candidates)
        {
            JsonObject candidate = new()
            {
                ["label"] = item.Label,
                ["kind"] = context.Kind == SourceCompletionKind.Ssh ? 18 : item.IsDirectory ? 19 : 17,
                ["detail"] = item.Detail,
                ["filterText"] = item.FilterText ?? context.Qualifier + (context.Quoted ? "\"" : "") + item.Label,
                ["textEdit"] = new JsonObject { ["range"] = source.Range(context.Start, context.Length), ["newText"] = item.Replacement }
            };
            if (item.Detail.StartsWith("String", StringComparison.Ordinal))
                candidate["documentation"] = Documentation("A String preserves literal text, including whitespace. On the left of `->` it writes that text. Keep a Path prefix such as `./`, or bind the completed value with a `Path` annotation, when you intend to read a file.");
            items.Add((JsonNode)candidate);
        }
        return items;
    }

    private static JsonNode? Definition(EditorDocument document, JsonElement parameters)
    {
        int offset = Offset(document.Source, parameters.GetProperty("position"));
        var definitions = document.Analysis?.DefinitionsAt(offset) ?? [];
        if (definitions.Count == 0) return null;
        JsonObject Location(EditorLocation location) => new()
        {
            ["uri"] = DocumentUri(location.SourceName),
            ["range"] = new SourceText(location.Source).Range(location.Span.Start, location.Span.Length)
        };
        return definitions.Count == 1 ? Location(definitions[0])
            : new JsonArray(definitions.Select(location => (JsonNode)Location(location)).ToArray());
    }

    internal static string StandardSource(string uri)
    {
        string file = new Uri(uri).AbsolutePath.Split('/')[^1];
        var assembly = typeof(LanguageServer).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith("." + file, StringComparison.Ordinal));
        if (resource is null) return "";
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

}
