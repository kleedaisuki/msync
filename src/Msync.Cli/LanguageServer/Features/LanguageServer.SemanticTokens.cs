using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Msync.Compiler;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private static bool SupportsSemanticTokenDelta(JsonElement capabilities) =>
        capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty("textDocument", out var text) && text.ValueKind == JsonValueKind.Object
        && text.TryGetProperty("semanticTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
        && tokens.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Object
        && requests.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.Object
        && full.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.True;

    private static string TokenResultId(EditorDocument document) => document.Generation.ToString(CultureInfo.InvariantCulture);

    private JsonObject SemanticTokens(EditorDocument document, string? previousResultId = null)
    {
        if (!semanticDeltaSupported || document.Generation == 0)
            return new JsonObject { ["data"] = JsonArray.Create(document.SemanticTokenData
                ?? FullTokenData(document, EncodeSemanticTokens(document))) };
        string resultId = TokenResultId(document);
        if (document.SemanticTokenValues is not null && previousResultId == resultId)
            return new() { ["resultId"] = resultId, ["edits"] = new JsonArray() };
        int[] values = document.SemanticTokenValues ??= EncodeSemanticTokens(document);
        if (document.PreviousSemanticTokens is { } previous && previous.ResultId == previousResultId
            && TokenDelta(previous.Values, values) is { } edits)
            return new() { ["resultId"] = resultId, ["edits"] = edits };
        return new() { ["resultId"] = resultId, ["data"] = JsonArray.Create(FullTokenData(document, values)) };
    }

    private static JsonElement FullTokenData(EditorDocument document, int[] values) =>
        document.SemanticTokenData ??= JsonSerializer.SerializeToElement(values, LanguageServerJson.Default.Int32Array);

    private static JsonArray? TokenDelta(ReadOnlySpan<int> previous, ReadOnlySpan<int> current)
    {
        int start = previous.CommonPrefixLength(current);
        var oldTail = previous[start..];
        var newTail = current[start..];
        int unchanged = 0;
        int limit = Math.Min(oldTail.Length, newTail.Length);
        while (unchanged < limit && oldTail[^(unchanged + 1)] == newTail[^(unchanged + 1)]) unchanged++;
        int removed = oldTail.Length - unchanged;
        int inserted = newTail.Length - unchanged;
        if (removed == 0 && inserted == 0) return [];
        // A delta edit has extra JSON fields. Send a full result when it
        // would avoid very little data, especially for short documents.
        if (current.Length - inserted < 16) return null;
        var edit = new JsonObject { ["start"] = start, ["deleteCount"] = removed };
        if (inserted != 0)
            edit["data"] = JsonArray.Create(JsonSerializer.SerializeToElement(newTail[..inserted].ToArray(),
                LanguageServerJson.Default.Int32Array));
        return new(edit);
    }

    private static int[] EncodeSemanticTokens(EditorDocument document)
    {
        SourceText source = document.Source;
        string text = source.Text;
        Dictionary<int, EditorTokenRole> inferred = [];
        foreach (var role in document.Analysis?.TokenRoles() ?? [])
            if (!inferred.TryGetValue(role.Span.Start, out var previous) || role.Span.Length < previous.Span.Length)
                inferred[role.Span.Start] = role;
        List<int> data = [];
        int previousLine = 0, previousColumn = 0;
        var patterns = document.Analysis?.RegexSpans ?? [];
        int patternIndex = 0, patternEnd = -1;
        var numberSpans = document.Analysis?.NumberSpans ?? Msync.Compiler.Syntax.Parser.NumberSpans(text);
        int numberIndex = 0;
        foreach (ValueMatch match in LexicalTokens().EnumerateMatches(text))
        {
            while (numberIndex < numberSpans.Count && numberSpans[numberIndex].End <= match.Index)
            {
                var number = numberSpans[numberIndex++];
                AddLexical(number.Start, number.Length, true);
            }
            if (numberIndex == numberSpans.Count || numberSpans[numberIndex].Start >= match.Index + match.Length)
                AddLexical(match.Index, match.Length, false);
        }
        while (numberIndex < numberSpans.Count)
        {
            var number = numberSpans[numberIndex++];
            AddLexical(number.Start, number.Length, true);
        }
        while (patternIndex < patterns.Count)
        {
            var pattern = patterns[patternIndex++];
            AddToken(pattern.Start, pattern.Length, 11);
        }
        return data.ToArray();

        void AddLexical(int index, int length, bool number)
        {
            while (patternIndex < patterns.Count && patterns[patternIndex].Start <= index)
            {
                var pattern = patterns[patternIndex++];
                AddToken(pattern.Start, pattern.Length, 11);
                patternEnd = pattern.End;
            }
            if (index < patternEnd) return;
            ReadOnlySpan<char> token = text.AsSpan(index, length);
            bool comment = token[0] == '#' || token.StartsWith("//", StringComparison.Ordinal);
            bool quoted = token[0] == '"';
            bool keyword = !comment && !quoted && IsKeyword(token);
            int separator = !comment && !quoted ? token.IndexOf("://", StringComparison.Ordinal) : -1;
            if (separator >= 0)
            {
                AddToken(index, separator, 6);
                AddToken(index + separator, 3, 8);
                AddToken(index + separator + 3, length - separator - 3, 7);
                return;
            }
            int type = comment ? 5 : quoted ? 4 : "?:-=!/$|@+*%<>".AsSpan().Contains(token[0]) ? 8 :
                keyword ? char.IsUpper(token[0]) ? 3 : 0 :
                index > 0 && text[index - 1] == '$' ? 9 : 2;
            if (!comment && !quoted)
            {
                if (token.SequenceEqual("?") || ControlFlow(token)) type = 10;
                else if (number) type = 12;
            }
            if (!keyword)
            {
                if ((inferred.TryGetValue(index, out var semantic)
                    || index > 0 && text[index - 1] == '$'
                        && inferred.TryGetValue(index - 1, out semantic))
                    && semantic.Span.End == index + length)
                    type = semantic.Role switch { "function" => 1, "type" => 3, _ => 2 };
            }
            AddToken(index, length, type);
        }

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
        || token.SequenceEqual("release") || token.SequenceEqual("acquire") || token.SequenceEqual("acqrel") || token.SequenceEqual("as");

    private static bool IsKeyword(ReadOnlySpan<char> token)
    {
        if (token.SequenceEqual("type")) return true;
        foreach (string keyword in Keywords)
            if (token.SequenceEqual(keyword)) return true;
        return false;
    }

    [GeneratedRegex("""(?<comment>(?<!\S)(?:\#|//)[^\r\n]*)|(?<string>"(?:[^"]|"")*")|(?<uri>[A-Za-z][A-Za-z0-9+.-]*://[^\s;{}(),"]*)|(?<word>[\p{L}_][\p{L}\p{N}_]*)|(?<operator>::|->|=>|==|!=|\*\*|[?=/$|@+*%<>-])""", RegexOptions.CultureInvariant)]
    private static partial Regex LexicalTokens();
}
