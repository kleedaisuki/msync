using System.Text;

namespace Msync.Compiler.Syntax;

internal enum TokenKind { End, Atom, String, Regex, LeftBrace, RightBrace, LeftParen, RightParen, LeftBracket, RightBracket, Comma, Semicolon, Colon, Select, Equal, Arrow, FatArrow, Question, Dollar, Slash, Pipe, EqualEqual, NotEqual, Less, LessEqual, Greater, GreaterEqual }
internal readonly record struct Token(TokenKind Kind, string Text, SourceSpan Span);
internal sealed class Lexer(string source, string sourceName)
{
    private int position;
    private readonly List<Token> tokens = [];
    public IReadOnlyList<Token> Lex()
    {
        while (position < source.Length)
        {
            if (char.IsWhiteSpace(source[position])) { position++; continue; }
            if (source[position] == '#' && (position == 0 || char.IsWhiteSpace(source[position - 1])))
            { while (position < source.Length && source[position] != '\n') position++; continue; }
            int start = position;
            char c = source[position++];
            if (c == '"') { ReadString(start); continue; }
            if (c == '<' && IsRegexStart() && ReadRegex(start)) continue;
            if (c == '/' && position < source.Length && source[position] == '/' &&
                (start == 0 || char.IsWhiteSpace(source[start - 1])))
            { while (position < source.Length && source[position] != '\n') position++; continue; }
            TokenKind? kind = c switch
            {
                '{' => TokenKind.LeftBrace,
                '}' => TokenKind.RightBrace,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                '[' => TokenKind.LeftBracket,
                ']' => TokenKind.RightBracket,
                ',' => TokenKind.Comma,
                ';' => TokenKind.Semicolon,
                '?' => TokenKind.Question,
                '$' => TokenKind.Dollar,
                '/' => TokenKind.Slash,
                '|' => TokenKind.Pipe,
                ':' => Take(':') ? TokenKind.Select : TokenKind.Colon,
                '=' => Take('>') ? TokenKind.FatArrow : Take('=') ? TokenKind.EqualEqual : TokenKind.Equal,
                '-' when Take('>') => TokenKind.Arrow,
                '!' when Take('=') => TokenKind.NotEqual,
                '<' => Take('=') ? TokenKind.LessEqual : TokenKind.Less,
                '>' => Take('=') ? TokenKind.GreaterEqual : TokenKind.Greater,
                _ => null
            };
            if (kind is { } tokenKind) { Add(tokenKind, source[start..position], start); continue; }
            ReadAtom(start);
        }
        tokens.Add(new(TokenKind.End, "", new(source.Length, 0)));
        return tokens;
    }
    private bool Take(char c)
    {
        if (position == source.Length || source[position] != c) return false;
        position++; return true;
    }
    private void Add(TokenKind kind, string text, int start) => tokens.Add(new(kind, text, new(start, position - start)));
    private void ReadString(int start)
    {
        StringBuilder value = new();
        while (position < source.Length)
        {
            char c = source[position++];
            if (c != '"') { value.Append(c); continue; }
            if (Take('"')) { value.Append('"'); continue; }
            Add(TokenKind.String, value.ToString(), start); return;
        }
        throw new SyntaxException("Unterminated string.", source, sourceName, new(start, position - start));
    }
    private bool IsRegexStart() => tokens.Count == 0 || tokens[^1].Kind is TokenKind.Slash or TokenKind.LeftBrace or TokenKind.Comma or TokenKind.LeftBracket or TokenKind.FatArrow;
    private bool ReadRegex(int start)
    {
        int end = position;
        bool escaped = false, characterClass = false;
        for (; end < source.Length; end++)
        {
            char c = source[end];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '[') characterClass = true;
            if (c == ']') characterClass = false;
            if (c != '>' || characterClass) continue;
            if (end + 1 < source.Length && !char.IsWhiteSpace(source[end + 1]) && !"/,;}|)".Contains(source[end + 1], StringComparison.Ordinal)) continue;
            string pattern = source[position..end];
            position = end + 1; Add(TokenKind.Regex, pattern, start); return true;
        }
        return false;
    }
    private void ReadAtom(int start)
    {
        bool uri = source.AsSpan(start).StartsWith("ssh://", StringComparison.Ordinal) || source.AsSpan(start).StartsWith("wsl://", StringComparison.Ordinal);
        bool windows = start + 2 < source.Length && char.IsLetter(source[start]) && source[start + 1] == ':' && source[start + 2] == '\\';
        int brackets = 0;
        while (position < source.Length)
        {
            char c = source[position];
            if (uri && c == '[') brackets++;
            if (uri && c == ']') brackets--;
            string delimiters = uri ? "{}(),;?\"$|<>!" : "{}()[],;?\"$|<>!";
            if (char.IsWhiteSpace(c) || delimiters.Contains(c, StringComparison.Ordinal)) break;
            if (uri && brackets == 0 && c == ':' && position + 1 < source.Length && source[position + 1] == ':') break;
            if (!uri && !windows && (c == '/' || c == ':')) break;
            if (c == '=' && (position + 1 < source.Length && source[position + 1] is '=' or '>' || tokens.Count > 0 && tokens[^1].Text == "let")) break;
            if (c == '-' && position + 1 < source.Length && source[position + 1] == '>') break;
            position++;
        }
        Add(TokenKind.Atom, source[start..position], start);
    }
}
