using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msync.Cli;

internal sealed record EditorDiagnostic(string Message, int Line, int Column);

internal sealed partial class LanguageServer(
    Func<string, string?, EditorDiagnostic?> check,
    Func<string, string> format)
{
    private readonly Dictionary<string, string> documents = new(StringComparer.Ordinal);
    private readonly Stream input = Console.OpenStandardInput();
    private readonly Stream output = Console.OpenStandardOutput();
    private static readonly string[] Keywords = ["let", "fn", "scope", "native", "if", "else", "for", "in", "match", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", "Bool", "Range", "String", "Function", "Scope", "Path", "Process", "Connection", "Null"];

    public int Run()
    {
        while (Read() is { } request)
        {
            using (request)
            {
                JsonElement root = request.RootElement;
                string method = root.TryGetProperty("method", out JsonElement m) ? m.GetString() ?? "" : "";
                JsonNode? id = root.TryGetProperty("id", out JsonElement requestId) ? JsonNode.Parse(requestId.GetRawText()) : null;
                JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;
                if (method == "exit") return 0;
                try
                {
                    if (id is not null) Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Handle(method, parameters) });
                    else Notify(method, parameters);
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentException)
                {
                    if (id is not null) Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32602, ["message"] = error.Message } });
                    else Console.Error.WriteLine($"LSP {method}: {error.Message}");
                }
            }
        }
        return 0;
    }

    private JsonNode? Handle(string method, JsonElement p)
    {
        if (method == "initialize") return JsonNode.Parse("""
            {"capabilities":{"positionEncoding":"utf-16","textDocumentSync":{"openClose":true,"change":2},"documentFormattingProvider":true,"completionProvider":{"triggerCharacters":[":","$"]},"hoverProvider":true,"definitionProvider":true,"documentSymbolProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["keyword","function","variable","type","string","comment","protocol","endpoint","operator","namespace"],"tokenModifiers":[]},"full":true}},"serverInfo":{"name":"msync","version":"0.8.0"}}
            """);
        if (method == "shutdown") return null;
        if (method == "msync/stdlibSource")
        {
            string uri = p.GetProperty("uri").GetString()!;
            return JsonValue.Create(StandardSource(uri));
        }
        string documentUri = p.TryGetProperty("textDocument", out JsonElement td) ? td.GetProperty("uri").GetString()! : "";
        string text = documents.GetValueOrDefault(documentUri, "");
        if (method == "textDocument/formatting") return new JsonArray(new JsonObject { ["range"] = Range(text, 0, text.Length), ["newText"] = format(text) });
        if (method == "textDocument/completion") return Completions(text, p);
        if (method == "textDocument/semanticTokens/full") return SemanticTokens(text);
        if (method == "textDocument/documentSymbol") return Symbols(text);
        if (method == "textDocument/definition") return Definition(documentUri, text, p);
        if (method == "textDocument/hover") return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"`{Word(text, Offset(text, p.GetProperty("position")))}` — msync 0.8 typed expression" } };
        return null;
    }

    private void Notify(string method, JsonElement p)
    {
        if (method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")) return;
        JsonElement td = p.GetProperty("textDocument");
        string uri = td.GetProperty("uri").GetString()!;
        if (method == "textDocument/didClose") documents.Remove(uri);
        else if (method == "textDocument/didOpen") documents[uri] = td.GetProperty("text").GetString()!;
        else
        {
            string text = documents.GetValueOrDefault(uri, "");
            foreach (JsonElement change in p.GetProperty("contentChanges").EnumerateArray())
            {
                string replacement = change.GetProperty("text").GetString()!;
                if (!change.TryGetProperty("range", out JsonElement range)) { text = replacement; continue; }
                int start = Offset(text, range.GetProperty("start"));
                int end = Offset(text, range.GetProperty("end"));
                text = string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));
            }
            documents[uri] = text;
        }
        JsonArray diagnostics = [];
        if (documents.TryGetValue(uri, out string? source) && check(source, uri) is { } diagnostic)
        {
            int line = Math.Max(0, diagnostic.Line - 1), column = Math.Max(0, diagnostic.Column - 1);
            diagnostics.Add((JsonNode)new JsonObject { ["range"] = new JsonObject { ["start"] = Position(line, column), ["end"] = Position(line, column + 1) }, ["severity"] = 1, ["source"] = "msync", ["message"] = diagnostic.Message });
        }
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics", ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics } });
    }

    private static JsonArray Completions(string text, JsonElement p)
    {
        int offset = Offset(text, p.GetProperty("position"));
        string prefix = text[..offset];
        IEnumerable<string> words = Keywords.Concat(Declaration().Matches(text).Select(m => m.Groups[2].Value));
        Match standard = StandardMember().Match(prefix);
        if (standard.Success)
        {
            string module = standard.Groups[1].Value;
            words = module.Length == 0 ? ["fs", "path", "string", "bool", "ranges"] : Declaration().Matches(StandardSource($"msync-stdlib:///0.8.0/{module}.msync")).Select(m => m.Groups[2].Value);
        }
        JsonArray items = [];
        foreach (string word in words.Distinct(StringComparer.Ordinal)) items.Add((JsonNode)new JsonObject { ["label"] = word, ["kind"] = Keywords.Contains(word, StringComparer.Ordinal) ? 14 : 3 });
        return items;
    }

    private static JsonNode? Definition(string uri, string text, JsonElement p)
    {
        int offset = Offset(text, p.GetProperty("position"));
        string word = Word(text, offset);
        foreach (Match match in Declaration().Matches(text))
            if (match.Groups[2].Value == word) return new JsonObject { ["uri"] = uri, ["range"] = Range(text, match.Groups[2].Index, match.Groups[2].Length) };
        Match qualified = StandardMember().Match(text[..Math.Min(text.Length, offset + word.Length)]);
        if (!qualified.Success || qualified.Groups[1].Length == 0) return null;
        string target = $"msync-stdlib:///0.8.0/{qualified.Groups[1].Value}.msync";
        string source = StandardSource(target);
        foreach (Match match in Declaration().Matches(source))
            if (match.Groups[2].Value == word) return new JsonObject { ["uri"] = target, ["range"] = Range(source, match.Groups[2].Index, match.Groups[2].Length) };
        return null;
    }

    private static JsonArray Symbols(string text)
    {
        JsonArray symbols = [];
        foreach (Match match in Declaration().Matches(text))
        {
            JsonObject range = Range(text, match.Groups[2].Index, match.Groups[2].Length);
            symbols.Add((JsonNode)new JsonObject { ["name"] = match.Groups[2].Value, ["kind"] = match.Groups[1].Value == "fn" ? 12 : 13, ["range"] = range, ["selectionRange"] = range.DeepClone() });
        }
        return symbols;
    }

    private static JsonObject SemanticTokens(string text)
    {
        JsonArray data = [];
        int previousLine = 0, previousColumn = 0;
        foreach (Match match in LexicalTokens().Matches(text))
        {
            if (match.Groups["uri"].Success)
            {
                int separator = match.Value.IndexOf("://", StringComparison.Ordinal);
                AddToken(match.Index, separator, 6);
                AddToken(match.Index + separator, 3, 8);
                AddToken(match.Index + separator + 3, match.Length - separator - 3, 7);
                continue;
            }
            int type = match.Groups["comment"].Success ? 5 : match.Groups["string"].Success ? 4 : match.Groups["operator"].Success ? 8 :
                Keywords.Contains(match.Value, StringComparer.Ordinal) ? char.IsUpper(match.Value[0]) ? 3 : 0 :
                match.Index > 0 && text[match.Index - 1] == '$' ? 9 : 2;
            AddToken(match.Index, match.Length, type);
        }
        return new JsonObject { ["data"] = data };

        void AddToken(int offset, int length, int type)
        {
            int end = offset + length;
            while (offset < end)
            {
                int newline = text.IndexOf('\n', offset, end - offset);
                int tokenEnd = newline < 0 ? end : newline;
                if (tokenEnd > offset && text[tokenEnd - 1] == '\r') tokenEnd--;
                if (tokenEnd > offset)
                {
                    (int line, int column) = Coordinates(text, offset);
                    data.Add((JsonNode?)JsonValue.Create(line - previousLine)); data.Add((JsonNode?)JsonValue.Create(line == previousLine ? column - previousColumn : column));
                    data.Add((JsonNode?)JsonValue.Create(tokenEnd - offset)); data.Add((JsonNode?)JsonValue.Create(type)); data.Add((JsonNode?)JsonValue.Create(0));
                    previousLine = line; previousColumn = column;
                }
                offset = newline < 0 ? end : newline + 1;
            }
        }
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
        byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        output.Write(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"));
        output.Write(bytes);
        output.Flush();
    }

    private static int Offset(string text, JsonElement position)
    {
        int target = position.GetProperty("line").GetInt32(), line = 0, offset = 0;
        while (offset < text.Length && line < target) if (text[offset++] == '\n') line++;
        int end = text.IndexOf('\n', offset);
        if (end < 0) end = text.Length;
        if (end > offset && text[end - 1] == '\r') end--;
        return Math.Min(end, offset + position.GetProperty("character").GetInt32());
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
    [GeneratedRegex("""(?<comment>(?<!\S)(?:\#|//)[^\r\n]*)|(?<string>"(?:[^"]|"")*")|(?<uri>[A-Za-z][A-Za-z0-9+.-]*://[^\s;{}(),"]*)|(?<word>[\p{L}_][\p{L}\p{N}_]*)|(?<operator>::|->|=>|==|!=|[?=/$|])""", RegexOptions.CultureInvariant)]
    private static partial Regex LexicalTokens();
}
