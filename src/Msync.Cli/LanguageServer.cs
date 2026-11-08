using Msync.Compiler;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msync.Cli;

internal sealed partial class LanguageServer(
    Func<string, string?, EditorAnalysis> check,
    Func<string, string> format)
{
    private readonly Dictionary<string, EditorDocument> documents = new(StringComparer.Ordinal);
    private readonly Stream input = new BufferedStream(Console.OpenStandardInput(), 16384);
    private readonly Stream output = Console.OpenStandardOutput();
    private bool semanticRefreshSupported;
    private bool semanticRefreshPending;
    private bool semanticRefreshDirty;
    private bool hierarchicalDocumentSymbols;
    private bool completionResolveDocumentation;
    private long serverRequest;
    private static readonly string[] Keywords = ["let", "fn", "scope", "native", "if", "else", "for", "in", "match", "return", "break", "loop", "release", "acquire", "acqrel", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", .. LanguageTypes.Names];

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
            return JsonNode.Parse("""
            {"capabilities":{"positionEncoding":"utf-16","textDocumentSync":{"openClose":true,"change":2},"documentFormattingProvider":true,"codeActionProvider":true,"completionProvider":{"resolveProvider":true,"triggerCharacters":[":","$","/","\\"]},"hoverProvider":true,"definitionProvider":true,"documentSymbolProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["keyword","function","variable","type","string","comment","protocol","endpoint","operator","namespace","controlKeyword","regexp"],"tokenModifiers":[]},"full":true}},"serverInfo":{"name":"msync","version":"0.8.0"}}
            """);
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
        if (document.Analysis?.IsTypePosition(offset) == true) return TypeCompletions(document.Source, offset);
        context ??= document.Analysis?.At(offset);
        JsonArray items = MemberCompletions(CompletionMembers(context), document.Generation, offset, lazyDocumentation);
        if (context?.Receiver is not null && context.ReceiverKind == EditorReceiverKind.Member)
            return NameCompletions(document.Source, offset, items);
        foreach (string keyword in Keywords)
            items.Add((JsonNode)Completion(keyword, null, null, null, keyword: true));
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

    private JsonArray Symbols(string uri, EditorDocument document)
    {
        JsonArray symbols = [];
        var inferred = (document.Analysis?.Occurrences ?? []).ToDictionary(item => item.Span);
        foreach (var declaration in document.Analysis?.Symbols ?? []) Add(declaration, symbols, null);
        return symbols;

        void Add(EditorSymbol declaration, JsonArray target, string? container)
        {
            inferred.TryGetValue(declaration.SelectionSpan, out var occurrence);
            int kind = occurrence?.SemanticRole switch
            {
                "function" => 12,
                "type" => 5,
                _ => declaration.Kind switch { EditorSymbolKind.Function => 12, EditorSymbolKind.Scope => 5, EditorSymbolKind.Field => 8, _ => 13 }
            };
            JsonObject symbol = new() { ["name"] = declaration.Name, ["kind"] = kind };
            if (hierarchicalDocumentSymbols)
            {
                symbol["range"] = document.Source.Range(declaration.Span.Start, declaration.Span.Length);
                symbol["selectionRange"] = document.Source.Range(declaration.SelectionSpan.Start, declaration.SelectionSpan.Length);
                if (occurrence is not null) symbol["detail"] = occurrence.Signature;
                if (declaration.Children.Count != 0)
                {
                    JsonArray children = [];
                    foreach (var child in declaration.Children) Add(child, children, declaration.Name);
                    symbol["children"] = children;
                }
                target.Add((JsonNode)symbol);
                return;
            }
            symbol["location"] = new JsonObject
            {
                ["uri"] = uri,
                ["range"] = document.Source.Range(declaration.SelectionSpan.Start, declaration.SelectionSpan.Length)
            };
            if (container is not null) symbol["containerName"] = container;
            target.Add((JsonNode)symbol);
            foreach (var child in declaration.Children) Add(child, target, declaration.Name);
        }
    }

    private static JsonObject SemanticTokens(EditorDocument document)
    {
        if (document.SemanticTokenData is { } cached)
            return new JsonObject { ["data"] = JsonArray.Create(cached) };
        SourceText source = document.Source;
        string text = source.Text;
        var inferred = (document.Analysis?.Occurrences ?? []).Where(item => item.SemanticRole is "function" or "type" || item.Type is "Scope" or "Scope?")
            .GroupBy(item => item.Span.Start).ToDictionary(group => group.Key, group => group.MinBy(item => item.Span.Length)!);
        List<int> data = [];
        int previousLine = 0, previousColumn = 0;
        var patterns = document.Analysis?.RegexSpans ?? [];
        int patternIndex = 0, patternEnd = -1;
        foreach (ValueMatch match in LexicalTokens().EnumerateMatches(text))
        {
            while (patternIndex < patterns.Count && patterns[patternIndex].Start <= match.Index)
            {
                var pattern = patterns[patternIndex++];
                AddToken(pattern.Start, pattern.Length, 11);
                patternEnd = pattern.End;
            }
            if (match.Index < patternEnd) continue;
            ReadOnlySpan<char> token = text.AsSpan(match.Index, match.Length);
            bool comment = token[0] == '#' || token.StartsWith("//", StringComparison.Ordinal);
            bool quoted = token[0] == '"';
            bool keyword = !comment && !quoted && IsKeyword(token);
            int separator = !comment && !quoted ? token.IndexOf("://", StringComparison.Ordinal) : -1;
            if (separator >= 0)
            {
                AddToken(match.Index, separator, 6);
                AddToken(match.Index + separator, 3, 8);
                AddToken(match.Index + separator + 3, match.Length - separator - 3, 7);
                continue;
            }
            int type = comment ? 5 : quoted ? 4 : "?:-=!/$|".AsSpan().Contains(token[0]) ? 8 :
                keyword ? char.IsUpper(token[0]) ? 3 : 0 :
                match.Index > 0 && text[match.Index - 1] == '$' ? 9 : 2;
            if (!comment && !quoted)
            {
                if (token.SequenceEqual("?") || ControlFlow(token)) type = 10;
            }
            if (!keyword && inferred.TryGetValue(match.Index, out var semantic) && semantic.Span.Length == match.Length)
                type = semantic.SemanticRole switch { "function" => 1, "type" => 3, _ => 2 };
            AddToken(match.Index, match.Length, type);
        }
        while (patternIndex < patterns.Count)
        {
            var pattern = patterns[patternIndex++];
            AddToken(pattern.Start, pattern.Length, 11);
        }
        JsonElement encoded = JsonSerializer.SerializeToElement(data.ToArray(), LanguageServerJson.Default.Int32Array);
        document.SemanticTokenData = encoded;
        return new JsonObject { ["data"] = JsonArray.Create(encoded) };

        void AddToken(int offset, int length, int type)
        {
            int end = offset + length;
            while (offset < end)
            {
                int relative = text.AsSpan(offset, end - offset).IndexOfAny('\r', '\n');
                int newline = relative < 0 ? -1 : offset + relative;
                int tokenEnd = newline < 0 ? end : newline;
                if (tokenEnd > offset)
                {
                    (int line, int column) = source.Coordinates(offset);
                    data.Add(line - previousLine); data.Add(line == previousLine ? column - previousColumn : column);
                    data.Add(tokenEnd - offset); data.Add(type); data.Add(0);
                    previousLine = line; previousColumn = column;
                }
                offset = newline < 0 ? end : newline + (text[newline] == '\r' && newline + 1 < end && text[newline + 1] == '\n' ? 2 : 1);
            }
        }
    }

    private static bool ControlFlow(ReadOnlySpan<char> token) => token.SequenceEqual("if") || token.SequenceEqual("else")
        || token.SequenceEqual("match") || token.SequenceEqual("for") || token.SequenceEqual("in")
        || token.SequenceEqual("return") || token.SequenceEqual("break") || token.SequenceEqual("loop")
        || token.SequenceEqual("release") || token.SequenceEqual("acquire") || token.SequenceEqual("acqrel");

    private static bool IsKeyword(ReadOnlySpan<char> token)
    {
        foreach (string keyword in Keywords)
            if (token.SequenceEqual(keyword)) return true;
        return false;
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

    private JsonDocument? Read()
    {
        int length = -1;
        while (true)
        {
            StringBuilder line = new();
            int value;
            while ((value = input.ReadByte()) != -1 && value != '\n') if (value != '\r') line.Append((char)value);
            if (value == -1) return null;
            if (line.Length == 0) break;
            string header = line.ToString();
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.AsSpan(15), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (length < 0) throw new InvalidDataException("LSP message has no Content-Length header.");
        byte[] bytes = new byte[length];
        input.ReadExactly(bytes);
        return JsonDocument.Parse(bytes);
    }

    private void Write(JsonNode value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) value.WriteTo(writer);
        output.Write(Encoding.ASCII.GetBytes($"Content-Length: {buffer.WrittenCount}\r\n\r\n"));
        output.Write(buffer.WrittenSpan);
        output.Flush();
    }

    private static (int Line, int Column) Coordinates(string text, int offset)
    {
        int line = 0, start = 0;
        for (int i = 0; i < offset; i++) if (text[i] == '\n') { line++; start = i + 1; }
        return (line, offset - start);
    }

    private static JsonObject Range(string text, int offset, int length)
    {
        var start = Coordinates(text, offset);
        var end = Coordinates(text, offset + length);
        return new JsonObject { ["start"] = Position(start.Line, start.Column), ["end"] = Position(end.Line, end.Column) };
    }

    private static JsonObject Position(int line, int column) => new() { ["line"] = line, ["character"] = column };
    private static string Word(string text, int offset)
    {
        int start = offset, end = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        return text[start..end];
    }

    [GeneratedRegex("""(?<comment>(?<!\S)(?:\#|//)[^\r\n]*)|(?<string>"(?:[^"]|"")*")|(?<uri>[A-Za-z][A-Za-z0-9+.-]*://[^\s;{}(),"]*)|(?<word>[\p{L}_][\p{L}\p{N}_]*)|(?<operator>::|->|=>|==|!=|[?=/$|])""", RegexOptions.CultureInvariant)]
    private static partial Regex LexicalTokens();
}
