using Msync.Compiler;
using Msync.Core;
using PrettyPrompt;
using PrettyPrompt.Highlighting;

namespace Msync.Cli;

internal static class PromptSyntax
{
    internal static readonly string[] Keywords = ["let", "global", "fn", "object", "concept", "typename", "as", "native", "if", "else", "for", "in", "match", "null", "true", "false", "is", "not", "and", "or", "exists", "decltype", "return", "break", "continue", "loop", "release", "acquire", "acqrel", .. LanguageTypes.Names];
    internal static readonly string[] Builtins = ["$std", "$local", "$wsl", "$pwd", "$file", "$path", "$env"];
    internal static readonly string[] Commands = ["/help", "/?", "/syntax", "/quit", "/exit", "/clear", "/pwd", "/cd"];

    public static List<FormatSpan> Highlight(string text, IReadOnlyDictionary<string, SemanticMember>? bindings = null)
    {
        List<FormatSpan> spans = [];
        string previous = "", chain = "";
        foreach (EditorLexeme lexeme in EditorLexemes.Scan(text))
        {
            int start = lexeme.Span.Start, length = lexeme.Span.Length;
            string word = text.Substring(start, length);
            AnsiColor? style;
            if (lexeme.Kind == EditorLexemeKind.Comment) style = AnsiColor.BrightBlack;
            else if (lexeme.Kind == EditorLexemeKind.String)
            {
                style = AnsiColor.Green;
                if (previous == "::" && lexeme.Complete)
                {
                    chain += "::" + word[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                    if (bindings is not null && bindings.TryGetValue(chain, out var member)) style = ValueColor(member.SemanticRole);
                }
                previous = "value";
            }
            else if (lexeme.Kind is EditorLexemeKind.Regex or EditorLexemeKind.Number)
            {
                style = AnsiColor.Magenta;
                previous = lexeme.Kind == EditorLexemeKind.Number ? "number" : "value";
                chain = "";
            }
            else if (word.StartsWith("ssh://", StringComparison.Ordinal) || word.StartsWith("wsl://", StringComparison.Ordinal))
            {
                spans.Add(new(start, 6, AnsiColor.Cyan));
                previous = "value";
                continue;
            }
            else if (lexeme.Kind is EditorLexemeKind.Operator or EditorLexemeKind.Delimiter || word is "+" or "-" or "*" or "%" or "**")
            {
                style = word == "?" ? AnsiColor.Magenta
                    : word == "/" ? previous is "number" or ")" ? AnsiColor.Cyan : null
                    : word is "::" or "->" or "=>" or "=" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "$" or "|" or "@" or "+" or "-" or "*" or "**" or "%" ? AnsiColor.Cyan : null;
                chain = word == "::" ? chain.Length == 0 ? previous : chain : "";
                previous = word;
            }
            else
            {
                if (previous == "/" && start > 0 && text[start - 1] == '/' && Commands.Contains("/" + word, StringComparer.Ordinal))
                {
                    spans.Add(new(start - 1, length + 1, AnsiColor.Cyan));
                    previous = word;
                    continue;
                }
                string selected = previous == "$" ? "$" + word : previous == "::" && chain.Length != 0 ? chain + "::" + word : word;
                style = Commands.Contains(word, StringComparer.Ordinal) ? AnsiColor.Cyan
                    : word is "if" or "else" or "match" or "for" or "in" or "loop" or "return" or "break" or "continue" or "release" or "acquire" or "acqrel" ? AnsiColor.Magenta
                    : word == "type" || Keywords.Contains(word, StringComparer.Ordinal) ? char.IsUpper(word[0]) ? AnsiColor.Magenta : AnsiColor.Cyan
                    : previous == "fn" ? AnsiColor.Yellow
                    : previous is "object" or "concept" or "typename" or "type" ? AnsiColor.Magenta
                    : bindings is not null && bindings.TryGetValue(selected, out var value) ? ValueColor(value.SemanticRole)
                    : DeclaredColor(text, lexeme.Span.End);
                previous = word; chain = selected;
            }
            if (style is { } accent) spans.Add(new(start, length, accent));
        }
        return spans;
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
            : source.EndsWith("@", StringComparison.Ordinal) ? "operand: Path or String; Connection @ path keeps path provenance"
            : EndsWithWord(source, "as") ? "explicit conversion: expression as Type; only Path/String conversion is implicit"
            : source.EndsWith("concept", StringComparison.Ordinal) ? "concept Name { Base, member: typename Item, operation: fn(value: Item) -> Item };"
            : EndsWithWord(source, "typename") ? "declare an associated type inline: member?: typename Item; reuse Item in related requirements"
            : source.EndsWith("=", StringComparison.Ordinal) && !source.EndsWith("==", StringComparison.Ordinal) ? "provide a value; use fn (...) { ... } for a function"
            : source.EndsWith("is", StringComparison.Ordinal) ? "null | not null | Type; '?' also means is null"
            : source.EndsWith("fn", StringComparison.Ordinal) ? "fn name(parameters) { result }; omit ';' on the result"
            : source.EndsWith("let", StringComparison.Ordinal) ? "let name = value; add '?' to admit Null"
            : source.EndsWith("global", StringComparison.Ordinal) ? "global root, .codex; import prior definition-time ancestor locations inside fn"
            : source.EndsWith("object", StringComparison.Ordinal) ? "object Name { member: value }; select with Name::member"
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
        foreach (EditorLexeme lexeme in EditorLexemes.Scan(text))
        {
            if (lexeme.Span.Start >= caret) break;
            if (lexeme.Kind != EditorLexemeKind.Delimiter) continue;
            char character = text[lexeme.Span.Start];
            if (character is '(' or '[' or '{') opens.Push((character, lexeme.Span.Start, 0));
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
            : "value::member selects a member; Connection @ path qualifies a path";
    }

    internal static bool InsideLiteral(string text, int offset) => EditorLexemes.Scan(text).Any(lexeme =>
        lexeme.Kind is EditorLexemeKind.String or EditorLexemeKind.Regex or EditorLexemeKind.Comment
        && lexeme.Span.Start < offset && (offset < lexeme.Span.End
            || offset == lexeme.Span.End && (!lexeme.Complete || lexeme.Kind == EditorLexemeKind.Comment)));
}
