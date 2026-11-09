using Msync.Compiler;
using Msync.Core;
using PrettyPrompt;
using PrettyPrompt.Highlighting;

namespace Msync.Cli;

internal static class PromptSyntax
{
    internal static readonly string[] Keywords = ["let", "global", "fn", "scope", "concept", "type", "as", "native", "if", "else", "for", "in", "match", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", "return", "break", "loop", "release", "acquire", "acqrel", .. LanguageTypes.Names];
    internal static readonly string[] Builtins = ["$std", "$local", "$wsl", "$pwd", "$file", "$path", "$env"];
    internal static readonly string[] Commands = ["/help", "/?", "/syntax", "/quit", "/exit", "/clear", "/pwd", "/cd"];

    public static List<FormatSpan> Highlight(string text, IReadOnlyDictionary<string, SemanticMember>? bindings = null)
    {
        List<FormatSpan> spans = [];
        var lexical = Msync.Compiler.Syntax.Parser.HighlightSpans(text);
        var numbers = lexical.Numbers.ToDictionary(span => span.Start, span => span.Length);
        var arithmetic = lexical.ArithmeticOperators.ToDictionary(span => span.Start, span => span.Length);
        int position = 0, start = 0;
        string previous = "", chain = "";
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position])) { position++; continue; }
            start = position;
            char character = text[position];
            if ((character == '#' || text.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
                && (position == 0 || char.IsWhiteSpace(text[position - 1])))
            {
                while (position < text.Length && text[position] is not ('\r' or '\n')) position++;
                Add(AnsiColor.BrightBlack); continue;
            }
            if (character == '"')
            {
                position++;
                while (position < text.Length)
                {
                    if (text[position++] != '"') continue;
                    if (position < text.Length && text[position] == '"') { position++; continue; }
                    break;
                }
                AnsiColor? quotedStyle = AnsiColor.Green;
                if (previous == "::" && position > start + 1 && text[position - 1] == '"')
                {
                    chain += "::" + text[(start + 1)..(position - 1)].Replace("\"\"", "\"", StringComparison.Ordinal);
                    if (bindings is not null && bindings.TryGetValue(chain, out var member)) quotedStyle = ValueColor(member.SemanticRole);
                }
                if (quotedStyle is { } quotedAccent) Add(quotedAccent);
                previous = "value"; continue;
            }
            if (character == '<' && (previous is "" or "/" or "{" or "[" or "," or "=>" || previous.EndsWith('/')))
            {
                position = PatternEnd(text, position + 1);
                Add(AnsiColor.Magenta); previous = "value"; continue;
            }
            int protocol = text.AsSpan(position).StartsWith("ssh://", StringComparison.Ordinal) ? 3
                : text.AsSpan(position).StartsWith("wsl://", StringComparison.Ordinal) ? 3 : 0;
            if (protocol > 0)
            {
                spans.Add(new(start, protocol + 3, AnsiColor.Cyan));
                position += protocol + 3;
                while (position < text.Length && (!Boundary(text[position]) || text[position] == '@')
                    && !text.AsSpan(position).StartsWith("::", StringComparison.Ordinal)) position++;
                previous = "value"; continue;
            }
            int numericLength = numbers.GetValueOrDefault(start);
            if (numericLength > 0)
            {
                position += numericLength;
                Add(AnsiColor.Magenta); previous = "number"; chain = ""; continue;
            }
            if (arithmetic.TryGetValue(start, out int arithmeticLength))
            {
                position += arithmeticLength;
                Add(AnsiColor.Cyan); previous = "operator"; chain = ""; continue;
            }
            if ("{}()[],;?:=$|<>!@".Contains(character, StringComparison.Ordinal) || text.AsSpan(position).StartsWith("->", StringComparison.Ordinal))
            {
                position++;
                if (position < text.Length && (character == ':' && text[position] == ':' || character is '=' or '!' or '<' or '>' && text[position] == '='
                    || character is '-' or '=' && text[position] == '>' || character == '*' && text[position] == '*')) position++;
                string operation = text[start..position];
                if (character == '?') Add(AnsiColor.Magenta);
                else if (operation is "::" or "->" or "=>" or "=" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "$" or "|" or "@" or "+" or "-" or "*" or "**" or "%") Add(AnsiColor.Cyan);
                chain = operation == "::" ? chain.Length == 0 ? previous : chain : "";
                previous = operation;
                continue;
            }
            if (character == '/' && (previous is "number" or ")" || position + 1 == text.Length || text[position + 1] == '<'))
            { position++; if (previous is "number" or ")") Add(AnsiColor.Cyan); previous = "/"; continue; }
            while (position < text.Length && !Boundary(text[position]) && !arithmetic.ContainsKey(position) && text[position] is not ('"' or '<' or ':' or '$')
                && !text.AsSpan(position).StartsWith("->", StringComparison.Ordinal)) position++;
            if (position == start) position++;
            string word = text[start..position];
            string selected = previous == "$" ? "$" + word : previous == "::" && chain.Length != 0 ? chain + "::" + word : word;
            AnsiColor? style = Commands.Contains(word, StringComparer.Ordinal) ? AnsiColor.Cyan
                : word is "if" or "else" or "match" or "for" or "in" or "loop" or "return" or "break" or "release" or "acquire" or "acqrel" or "as" ? AnsiColor.Magenta
                : Keywords.Contains(word, StringComparer.Ordinal) ? char.IsUpper(word[0]) ? AnsiColor.Magenta : AnsiColor.Cyan
                : word is "+" or "-" or "*" ? AnsiColor.Cyan
                : NumberSyntax.TryParse(word, out _) ? AnsiColor.Magenta
                : previous == "fn" ? AnsiColor.Yellow
                : previous is "scope" or "concept" ? AnsiColor.Magenta
                : bindings is not null && bindings.TryGetValue(selected, out var value) ? ValueColor(value.SemanticRole)
                : DeclaredColor(text, position);
            if (style is { } accent) Add(accent);
            previous = word; chain = selected;
        }
        return spans;

        void Add(AnsiColor style) => spans.Add(new(start, position - start, style));
    }

    private static AnsiColor? DeclaredColor(string text, int offset)
    {
        while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
        if (offset < text.Length && text[offset] == '?') offset++;
        while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
        if (offset == text.Length || text[offset] is not (':' or '=')) return null;
        offset++;
        while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
        ReadOnlySpan<char> tail = text.AsSpan(offset);
        return Word(tail, "fn") || Word(tail, "Function") ? AnsiColor.Yellow : null;

        static bool Word(ReadOnlySpan<char> source, string word) => source.StartsWith(word, StringComparison.Ordinal)
            && (source.Length == word.Length || !char.IsLetterOrDigit(source[word.Length]) && source[word.Length] != '_');
    }

    private static AnsiColor? ValueColor(string role) => role == "function" ? AnsiColor.Yellow
        : role == "type" ? AnsiColor.Magenta : null;

    private static bool Boundary(char character) => char.IsWhiteSpace(character) || "{}()[],;?=$|<>!@".Contains(character, StringComparison.Ordinal);

    private static int PatternEnd(string text, int position)
    {
        bool escaped = false, characterClass = false, namedAngle = false;
        for (; position < text.Length; position++)
        {
            char character = text[position];
            if (escaped) { escaped = false; continue; }
            if (character == '\\')
            {
                if (!characterClass && position + 2 < text.Length && text[position + 1] == 'k' && text[position + 2] == '<')
                { namedAngle = true; position += 2; continue; }
                escaped = true; continue;
            }
            if (character == '[') characterClass = true;
            if (character == ']') characterClass = false;
            if (character == '(' && !characterClass && position + 3 < text.Length && text[position + 1] == '?' && text[position + 2] == '<'
                && text[position + 3] is not ('=' or '!')) { namedAngle = true; position += 2; continue; }
            if (character != '>' || characterClass) continue;
            if (namedAngle) { namedAngle = false; continue; }
            int next = position + 1;
            if (next == text.Length || char.IsWhiteSpace(text[next]) || "/,;}|)".Contains(text[next], StringComparison.Ordinal)
                || text.AsSpan(next).StartsWith("=>", StringComparison.Ordinal) || text.AsSpan(next).StartsWith("->", StringComparison.Ordinal))
                return next;
        }
        return position;
    }

    public static PromptHint? Hint(string text, int caret, IReadOnlyDictionary<string, SemanticMember> bindings)
    {
        if (caret == 0 || caret != text.Length) return null;
        int start = caret;
        while (start > 0 && (!Msync.Compiler.Syntax.NameSyntax.Boundary(text[start - 1]) || text[start - 1] is '$' or ':' or '/')) start--;
        string prefix = text[start..caret];
        if (prefix.Length > 0 && !InsideLiteral(text, start))
        {
            string? single = null;
            foreach (string candidate in Keywords.Concat(Builtins).Concat(Commands).Concat(bindings.Where(pair => !pair.Value.IsConstraint).Select(pair => pair.Key)))
            {
                if (!candidate.StartsWith(prefix, StringComparison.Ordinal) || candidate == prefix) continue;
                if (single is not null && single != candidate) { single = null; break; }
                single = candidate;
            }
            if (single is not null) return new(single[prefix.Length..]);
        }
        string source = text.TrimEnd();
        string? message = source.EndsWith("->", StringComparison.Ordinal) ? Msync.Compiler.Syntax.TypeSyntaxFacts.IsReturnTypeArrow(source)
                ? "return type: choose a type, union or nullable type; finish with { result }"
                : "destination: Path or String; '->' returns Bool"
            : source.EndsWith("@", StringComparison.Ordinal) ? "endpoint: Connection; Path | String @ Connection keeps path provenance"
            : EndsWithWord(source, "as") ? "explicit conversion: expression as Type; only Path/String conversion is implicit"
            : source.EndsWith("concept", StringComparison.Ordinal) ? "concept Name { Base, type Self; member: Type, operation: fn(value: Self) -> Self };"
            : source.EndsWith("=", StringComparison.Ordinal) && !source.EndsWith("==", StringComparison.Ordinal) ? "provide a value; use fn (...) { ... } for a function"
            : source.EndsWith("is", StringComparison.Ordinal) ? "null | not null | Type; '?' also means is null"
            : source.EndsWith("fn", StringComparison.Ordinal) ? "fn name(parameters) { result }; omit ';' on the result"
            : source.EndsWith("let", StringComparison.Ordinal) ? "let name = value; add '?' to admit Null"
            : source.EndsWith("global", StringComparison.Ordinal) ? "global root, .codex; import prior definition-time ancestor locations inside fn"
            : source.EndsWith("scope", StringComparison.Ordinal) ? "scope Name { member: value }; select with Name::member"
            : source.EndsWith("release", StringComparison.Ordinal) ? "before fence; release Path(args) keeps its Process live"
            : source.EndsWith("acquire", StringComparison.Ordinal) ? "join this invocation's pending work; acquire process joins only that Process"
            : source.EndsWith("acqrel", StringComparison.Ordinal) ? "acqrel expression applies before and after ordering fences"
            : source.EndsWith("exists", StringComparison.Ordinal) ? "exists Path | String -> Bool"
            : source.EndsWith("for", StringComparison.Ordinal) ? "for item in Range { body }; iteration binds each item"
            : source.EndsWith("if", StringComparison.Ordinal) ? "if Bool { result } else { fallback }; Null is not Bool"
            : source.EndsWith("match", StringComparison.Ordinal) ? "match value { pattern => { result }, _ => { fallback } }"
            : source.EndsWith("::", StringComparison.Ordinal) ? MemberHint(source[..^2], bindings)
            : null;
        message ??= CallHint(text, caret, bindings);
        return message is null || InsideLiteral(text, caret) ? null : new("  [" + message + "]", Insertable: false);
    }

    private static bool EndsWithWord(string source, string word) => source.EndsWith(word, StringComparison.Ordinal)
        && (source.Length == word.Length || Msync.Compiler.Syntax.NameSyntax.Boundary(source[source.Length - word.Length - 1]));

    private static string? CallHint(string text, int caret, IReadOnlyDictionary<string, SemanticMember> bindings)
    {
        Stack<(char Delimiter, int Offset, int Argument)> opens = [];
        bool quoted = false;
        for (int position = 0; position < caret; position++)
        {
            char character = text[position];
            if (character == '"')
            {
                if (quoted && position + 1 < caret && text[position + 1] == '"') position++;
                else quoted = !quoted;
                continue;
            }
            if (quoted) continue;
            if (character == '<' && RegexStart(text, position)) { position = PatternEnd(text, position + 1) - 1; continue; }
            if ((character == '#' || text.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
                && (position == 0 || char.IsWhiteSpace(text[position - 1])))
            {
                while (position < caret && text[position] is not ('\r' or '\n')) position++;
                continue;
            }
            if (character is '(' or '[' or '{') opens.Push((character, position, 0));
            else if (character is ')' or ']' or '}') { if (opens.Count > 0) opens.Pop(); }
            else if (character == ',' && opens.Count > 0)
            {
                var top = opens.Pop();
                opens.Push((top.Delimiter, top.Offset, top.Argument + 1));
            }
        }
        foreach (var opening in opens)
        {
            if (opening.Delimiter != '(') continue;
            int end = opening.Offset, begin = end;
            while (begin > 0 && (char.IsLetterOrDigit(text[begin - 1]) || text[begin - 1] is '_' or ':' or '$')) begin--;
            string name = text[begin..end];
            if (bindings.TryGetValue(name, out var called)) return called.Signature + $" — argument {opening.Argument + 1}";
            if (name == "decltype") return "infer expression type without executing its effects";
        }
        return null;
    }

    private static string MemberHint(string qualifier, IReadOnlyDictionary<string, SemanticMember> bindings)
    {
        int begin = qualifier.Length;
        while (begin > 0 && (char.IsLetterOrDigit(qualifier[begin - 1]) || qualifier[begin - 1] is '_' or ':' or '$')) begin--;
        return bindings.TryGetValue(qualifier[begin..], out var value) && value.Members is { Count: > 0 } members
            ? string.Join(", ", members.Select(member => $"{member.Name}: {member.Type}"))
            : "Scope::member selects a member; path @ Connection qualifies a path";
    }

    private static bool RegexStart(string text, int offset)
    {
        int previous = offset - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
        return previous < 0 || "/{[,".Contains(text[previous], StringComparison.Ordinal)
            || previous > 0 && text[previous] == '>' && text[previous - 1] == '=';
    }

    internal static bool InsideLiteral(string text, int offset)
    {
        bool quoted = false;
        for (int position = 0; position < offset; position++)
        {
            if (!quoted && (text[position] == '#' || text.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
                && (position == 0 || char.IsWhiteSpace(text[position - 1])))
            {
                while (position < offset && text[position] is not ('\r' or '\n')) position++;
                if (position == offset) return true;
            }
            if (!quoted && text[position] == '<' && RegexStart(text, position))
            {
                int end = PatternEnd(text, position + 1);
                if (end > offset || end == offset && text[end - 1] != '>') return true;
                position = end - 1;
                continue;
            }
            if (text[position] != '"') continue;
            if (quoted && position + 1 < offset && text[position + 1] == '"') position++;
            else quoted = !quoted;
        }
        return quoted;
    }
}
