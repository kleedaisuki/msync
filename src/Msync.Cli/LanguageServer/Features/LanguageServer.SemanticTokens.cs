using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Msync.Compiler;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
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
        var numberSpans = Msync.Compiler.Syntax.Parser.NumberSpans(text);
        List<(int Index, int Length, bool Number)> lexical = [];
        int numberIndex = 0;
        foreach (ValueMatch match in LexicalTokens().EnumerateMatches(text))
        {
            while (numberIndex < numberSpans.Count && numberSpans[numberIndex].End <= match.Index)
            {
                var number = numberSpans[numberIndex++];
                lexical.Add((number.Start, number.Length, true));
            }
            if (numberIndex == numberSpans.Count || numberSpans[numberIndex].Start >= match.Index + match.Length)
                lexical.Add((match.Index, match.Length, false));
        }
        while (numberIndex < numberSpans.Count)
        {
            var number = numberSpans[numberIndex++];
            lexical.Add((number.Start, number.Length, true));
        }
        foreach (var match in lexical)
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
            int type = comment ? 5 : quoted ? 4 : "?:-=!/$|@+*%<>".AsSpan().Contains(token[0]) ? 8 :
                keyword ? char.IsUpper(token[0]) ? 3 : 0 :
                match.Index > 0 && text[match.Index - 1] == '$' ? 9 : 2;
            if (!comment && !quoted)
            {
                if (token.SequenceEqual("?") || ControlFlow(token)) type = 10;
                else if (match.Number) type = 12;
            }
            if (!keyword)
            {
                inferred.TryGetValue(match.Index, out var semantic);
                if (semantic is null && match.Index > 0 && text[match.Index - 1] == '$')
                    inferred.TryGetValue(match.Index - 1, out semantic);
                if (semantic is not null && semantic.Span.End == match.Index + match.Length)
                    type = semantic.SemanticRole switch { "function" => 1, "type" => 3, _ => 2 };
            }
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
