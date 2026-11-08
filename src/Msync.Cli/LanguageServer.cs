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
    private long serverRequest;
    private static readonly string[] Keywords = ["let", "fn", "scope", "native", "if", "else", "for", "in", "match", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", "Bool", "Range", "String", "Function", "Scope", "Path", "Process", "Connection", "Null", "Number"];

    private JsonNode? Handle(string method, JsonElement p)
    {
        if (method == "initialize")
        {
            semanticRefreshSupported = p.TryGetProperty("capabilities", out var capabilities)
                && capabilities.TryGetProperty("workspace", out var workspace)
                && workspace.TryGetProperty("semanticTokens", out var tokens)
                && tokens.TryGetProperty("refreshSupport", out var refresh) && refresh.ValueKind == JsonValueKind.True;
            return JsonNode.Parse("""
            {"capabilities":{"positionEncoding":"utf-16","textDocumentSync":{"openClose":true,"change":2},"documentFormattingProvider":true,"codeActionProvider":true,"completionProvider":{"triggerCharacters":[":","$"]},"hoverProvider":true,"definitionProvider":true,"documentSymbolProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["keyword","function","variable","type","string","comment","protocol","endpoint","operator","namespace"],"tokenModifiers":[]},"full":true}},"serverInfo":{"name":"msync","version":"0.8.0"}}
            """);
        }
        if (method == "shutdown") return null;
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
        if (method == "textDocument/completion") return Completions(document, p);
        if (method == "textDocument/semanticTokens/full") return SemanticTokens(document);
        if (method == "textDocument/documentSymbol") return Symbols(document);
        if (method == "textDocument/definition") return Definition(documentUri, document.Source, p);
        if (method == "textDocument/hover") return Hover(document, p);
        if (method == "textDocument/codeAction") return CodeActions(documentUri, document, p);
        return null;
    }

    private static JsonArray Completions(EditorDocument document, JsonElement p)
    {
        SourceText source = document.Source;
        string text = source.Text;
        int offset = Offset(source, p.GetProperty("position"));
        string prefix = text[..offset];
        int select = prefix.LastIndexOf("::", StringComparison.Ordinal);
        if (select >= 0 && IdentifierSuffix(prefix.AsSpan(select + 2)))
        {
            int qualifierEnd = select;
            while (qualifierEnd > 0 && char.IsWhiteSpace(text[qualifierEnd - 1])) qualifierEnd--;
            var qualifier = document.Analysis?.Occurrences.Where(item => item.Span.End == qualifierEnd && item.Members is not null)
                .MinBy(item => item.Span.Length);
            IReadOnlyList<SemanticMember>? members = qualifier?.Members;
            if (members is null && document.Analysis is { RecoveryBindings: { } bindings, RecoveryStart: { } start } && start <= select)
            {
                string name = text.AsSpan(start, select - start).Trim().ToString();
                if (IdentifierSuffix(name)) members = bindings.FirstOrDefault(binding => binding.Name == name)?.Members;
            }
            if (members is not null)
            {
                JsonArray selected = [];
                foreach (var member in members)
                    selected.Add((JsonNode)Completion(member.Name, member.Type, member.Signature, member.Description));
                return selected;
            }
        }
        IEnumerable<string> words = Keywords.Concat(Declaration().Matches(text).Select(m => m.Groups[2].Value));
        Match standard = StandardMember().Match(prefix);
        if (standard.Success && standard.Groups[1].Length == 0)
        {
            words = ["fs", "path", "string", "bool", "ranges", "process"];
        }
        else if (StandardModule(prefix) is { } module)
        {
            words = Declaration().Matches(StandardSource($"msync-stdlib:///0.8.0/{module}.msync")).Select(m => m.Groups[2].Value);
        }
        JsonArray items = [];
        Dictionary<string, SemanticOccurrence> names = new(StringComparer.Ordinal);
        var inferred = (document.Analysis?.Occurrences ?? []).GroupBy(item => item.Span.Start)
            .ToDictionary(group => group.Key, group => group.MinBy(item => item.Span.Length)!);
        foreach (Match declaration in Declaration().Matches(text))
        {
            if (declaration.Groups[2].Index <= offset && inferred.TryGetValue(declaration.Groups[2].Index, out var occurrence))
                names[declaration.Groups[2].Value] = occurrence;
        }
        foreach (string word in words.Distinct(StringComparer.Ordinal))
        {
            names.TryGetValue(word, out var occurrence);
            items.Add((JsonNode)Completion(word, occurrence?.Type, occurrence?.Signature, occurrence?.Description,
                keyword: Keywords.Contains(word, StringComparer.Ordinal)));
        }
        return items;
    }

    private static bool IdentifierSuffix(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
            if (!char.IsLetterOrDigit(character) && character != '_') return false;
        return true;
    }

    private static JsonObject Completion(string name, string? type, string? signature, string? description, bool keyword = false) => new()
    {
        ["label"] = name,
        ["kind"] = keyword ? 14 : type switch { "Function" or "Function?" => 3, "Scope" or "Scope?" => 7, _ => 6 },
        ["detail"] = signature,
        ["documentation"] = description is null ? null : new JsonObject { ["kind"] = "markdown", ["value"] = description }
    };

    private static JsonNode? Definition(string uri, SourceText sourceText, JsonElement p)
    {
        string text = sourceText.Text;
        int offset = Offset(sourceText, p.GetProperty("position"));
        string word = Word(text, offset);
        int start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        if (StandardModule(text[..start]) is { } module)
        {
            string target = $"msync-stdlib:///0.8.0/{module}.msync";
            string source = StandardSource(target);
            foreach (Match match in Declaration().Matches(source))
                if (match.Groups[2].Value == word) return new JsonObject { ["uri"] = target, ["range"] = Range(source, match.Groups[2].Index, match.Groups[2].Length) };
            return null;
        }
        foreach (Match match in Declaration().Matches(text))
            if (match.Groups[2].Value == word) return new JsonObject { ["uri"] = uri, ["range"] = sourceText.Range(match.Groups[2].Index, match.Groups[2].Length) };
        return null;
    }

    private static string? StandardModule(string prefix)
    {
        Match qualifier = StandardQualifier().Match(prefix);
        if (!qualifier.Success) return null;
        if (qualifier.Groups["module"].Success) return qualifier.Groups["module"].Value;
        string alias = qualifier.Groups["alias"].Value;
        string? module = null;
        foreach (Match declaration in StandardAlias().Matches(prefix))
            if (declaration.Groups[1].Value == alias) module = declaration.Groups[2].Value;
        return module;
    }

    private static JsonArray Symbols(EditorDocument document)
    {
        SourceText source = document.Source;
        JsonArray symbols = [];
        var inferred = (document.Analysis?.Occurrences ?? []).GroupBy(item => item.Span.Start)
            .ToDictionary(group => group.Key, group => group.MinBy(item => item.Span.Length)!);
        foreach (Match match in Declaration().Matches(source.Text))
        {
            JsonObject range = source.Range(match.Groups[2].Index, match.Groups[2].Length);
            inferred.TryGetValue(match.Groups[2].Index, out var occurrence);
            int kind = occurrence?.Type switch { "Function" or "Function?" => 12, "Scope" or "Scope?" => 5, _ => match.Groups[1].Value switch { "fn" => 12, "scope" => 5, _ => 13 } };
            symbols.Add((JsonNode)new JsonObject { ["name"] = match.Groups[2].Value, ["detail"] = occurrence?.Signature, ["kind"] = kind, ["range"] = range, ["selectionRange"] = range.DeepClone() });
        }
        return symbols;
    }

    private static JsonObject SemanticTokens(EditorDocument document)
    {
        SourceText source = document.Source;
        string text = source.Text;
        var inferred = (document.Analysis?.Occurrences ?? []).Where(item => item.Type is "Function" or "Scope" or "Function?" or "Scope?")
            .GroupBy(item => item.Span.Start).ToDictionary(group => group.Key, group => group.MinBy(item => item.Span.Length)!);
        var declarations = Declaration().Matches(text).ToDictionary(match => match.Groups[2].Index,
            match => match.Groups[1].Value == "fn" ? 1 : match.Groups[1].Value == "scope" ? 3 : 2);
        List<int> data = [];
        int previousLine = 0, previousColumn = 0;
        foreach (ValueMatch match in LexicalTokens().EnumerateMatches(text))
        {
            ReadOnlySpan<char> token = text.AsSpan(match.Index, match.Length);
            bool comment = token[0] == '#' || token.StartsWith("//", StringComparison.Ordinal);
            bool quoted = token[0] == '"';
            int separator = !comment && !quoted ? token.IndexOf("://", StringComparison.Ordinal) : -1;
            if (separator >= 0)
            {
                AddToken(match.Index, separator, 6);
                AddToken(match.Index + separator, 3, 8);
                AddToken(match.Index + separator + 3, match.Length - separator - 3, 7);
                continue;
            }
            int type = comment ? 5 : quoted ? 4 : "?:-=!/$|".AsSpan().Contains(token[0]) ? 8 :
                IsKeyword(token) ? char.IsUpper(token[0]) ? 3 : 0 :
                match.Index > 0 && text[match.Index - 1] == '$' ? 9 : 2;
            if (inferred.TryGetValue(match.Index, out var semantic) && semantic.Span.Length == match.Length)
                type = semantic.Type.StartsWith("Function", StringComparison.Ordinal) ? 1 : 3;
            else if (declarations.TryGetValue(match.Index, out int declared)) type = declared;
            AddToken(match.Index, match.Length, type);
        }
        JsonNode? encoded = JsonSerializer.SerializeToNode(data.ToArray(), LanguageServerJson.Default.Int32Array);
        return new JsonObject { ["data"] = encoded };

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

    [GeneratedRegex(@"\b(fn|let|scope)\s+([\p{L}_][\p{L}\p{N}_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
    [GeneratedRegex(@"\$std::(?:(\w+)::)?[\w]*$", RegexOptions.CultureInvariant)]
    private static partial Regex StandardMember();
    [GeneratedRegex(@"(?:\$std::(?<module>\w+)|(?<alias>\w+))::\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex StandardQualifier();
    [GeneratedRegex(@"\blet\s+(\w+)\s*=\s*\$std::(\w+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex StandardAlias();
    [GeneratedRegex("""(?<comment>(?<!\S)(?:\#|//)[^\r\n]*)|(?<string>"(?:[^"]|"")*")|(?<uri>[A-Za-z][A-Za-z0-9+.-]*://[^\s;{}(),"]*)|(?<word>[\p{L}_][\p{L}\p{N}_]*)|(?<operator>::|->|=>|==|!=|[?=/$|])""", RegexOptions.CultureInvariant)]
    private static partial Regex LexicalTokens();
}
