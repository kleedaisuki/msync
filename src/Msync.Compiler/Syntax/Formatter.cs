using System.Text;

namespace Msync.Compiler.Syntax;

public static class Formatter
{
    private const int PrintWidth = 80;
    public static string Format(string source, string sourceName = "<source>")
    {
        _ = Parser.Parse(source, sourceName);
        string normalized = new Writer(source, new Lexer(source, sourceName).Lex()).Write();
        return ExceedsWidth(normalized)
            ? new Writer(normalized, new Lexer(normalized, sourceName).Lex(), wrap: true).Write()
            : normalized;
    }
    private static bool ExceedsWidth(string text)
    {
        int column = 0;
        foreach (char character in text)
        {
            if (character == '\n') column = 0;
            else if (++column > PrintWidth) return true;
        }
        return false;
    }
    private sealed class Writer(string source, IReadOnlyList<Token> tokens, bool wrap = false)
    {
        private readonly StringBuilder output = new();
        private readonly Stack<Layout> nesting = new();
        private readonly record struct Layout(TokenKind Kind, bool Expanded, int ParentIndent, int ClosingIndent);
        private readonly Dictionary<int, int>? closing = wrap ? MatchDelimiters(tokens) : null;
        private int continuation;
        private int indent;
        private Token? previous;
        private int tokenIndex;
        private bool globalList;
        public string Write()
        {
            for (tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var token = tokens[tokenIndex];
                Comments(source[(previous?.Span.End ?? 0)..token.Span.Start]);
                if (token.Kind == TokenKind.End) break;
                Emit(token);
                previous = token;
            }
            Newline();
            return output.ToString();
        }
        private void Emit(Token token)
        {
            switch (token.Kind)
            {
                case TokenKind.LeftBrace:
                    Space(); Text("{"); Open(token, expanded: true); break;
                case TokenKind.RightBrace:
                    Close(token); break;
                case TokenKind.LeftParen:
                case TokenKind.LeftBracket:
                    if (token.Kind == TokenKind.LeftBracket && previous?.Kind == TokenKind.Atom) Space();
                    Text(token.Text);
                    bool expanded = wrap && Column + closing![token.Span.Start] - token.Span.End + 1 > PrintWidth;
                    Open(token, expanded);
                    break;
                case TokenKind.RightParen:
                case TokenKind.RightBracket:
                    Close(token); break;
                case TokenKind.Semicolon:
                    globalList = false;
                    Trim(); Text(";"); Newline(); break;
                case TokenKind.Comma:
                    Trim(); Text(",");
                    if (globalList)
                    {
                        if (wrap && Column + 1 + tokens[tokenIndex + 1].Text.Length > PrintWidth)
                        { Newline(); continuation = 4; }
                        else Space();
                        break;
                    }
                    if (nesting.TryPeek(out var kind) && (kind.Kind == TokenKind.LeftBrace || kind.Expanded)) Newline(); else Space();
                    break;
                case TokenKind.Colon:
                    Trim(); Text(":"); Space(); break;
                case TokenKind.Select:
                case TokenKind.Slash:
                case TokenKind.Question:
                    Trim(); Text(token.Text); break;
                case TokenKind.Dollar:
                    WordGap(token); Text("$"); break;
                case TokenKind.Arrow:
                case TokenKind.FatArrow:
                case TokenKind.Equal:
                case TokenKind.EqualEqual:
                case TokenKind.NotEqual:
                case TokenKind.Pipe:
                case TokenKind.Less:
                case TokenKind.LessEqual:
                case TokenKind.Greater:
                case TokenKind.GreaterEqual:
                    if (wrap && token.Kind != TokenKind.Equal && NeedsOperatorBreak(token))
                    { Newline(); continuation = 4; }
                    else Space();
                    Text(token.Text);
                    if (wrap && token.Kind == TokenKind.Equal && NeedsAssignmentBreak(token))
                    { Newline(); continuation = 4; }
                    else Space();
                    break;
                default:
                    if (token.Kind == TokenKind.Atom && token.Text == "global" && tokens[tokenIndex + 1].Kind == TokenKind.Atom)
                        globalList = true;
                    if (wrap && IsWordOperator(token) && NeedsOperatorBreak(token))
                    { Newline(); continuation = 4; }
                    WordGap(token);
                    Text(source[token.Span.Start..token.Span.End]); break;
            }
        }
        private void Open(Token token, bool expanded)
        {
            int closingIndent = indent + continuation / 4;
            nesting.Push(new(token.Kind, expanded, indent, closingIndent));
            if (!expanded) return;
            indent = closingIndent + 1;
            Newline();
        }
        private void Close(Token token)
        {
            var layout = nesting.Pop();
            if (layout.Expanded)
            {
                indent = layout.ClosingIndent;
                Newline();
            }
            Trim(); Text(token.Text);
            indent = layout.ParentIndent;
        }
        private int Column
        {
            get
            {
                int start = output.Length;
                while (start > 0 && output[start - 1] != '\n') start--;
                return output.Length - start;
            }
        }
        private bool NeedsAssignmentBreak(Token token)
        {
            int end = source.IndexOf('\n', token.Span.End);
            if (end < 0) end = source.Length;
            int call = source.IndexOf('(', token.Span.End, end - token.Span.End);
            if (call >= 0) end = call + 1;
            return Column + end - token.Span.End > PrintWidth;
        }
        private bool NeedsOperatorBreak(Token token)
        {
            int end = token.Span.End;
            for (int index = tokenIndex + 1; index < tokens.Count; index++)
            {
                var next = tokens[index];
                if (IsBreak(next) || next.Kind is TokenKind.End or TokenKind.Comma or TokenKind.Semicolon
                    or TokenKind.RightBrace or TokenKind.LeftBrace) break;
                end = next.Span.End;
                if (next.Kind == TokenKind.LeftParen) break;
            }
            return Column + 1 + end - token.Span.Start > PrintWidth && Column > indent * 4 + 4;
        }
        private static bool IsBreak(Token token) => token.Kind is TokenKind.Arrow or TokenKind.FatArrow
            or TokenKind.Equal or TokenKind.EqualEqual or TokenKind.NotEqual or TokenKind.Pipe
            or TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual
            || IsWordOperator(token);
        private static bool IsWordOperator(Token token) => token.Kind == TokenKind.Atom
            && token.Text is "and" or "or" or "is" or "+" or "-" or "*";
        private static Dictionary<int, int> MatchDelimiters(IReadOnlyList<Token> items)
        {
            Dictionary<int, int> result = [];
            Stack<Token> opened = new();
            foreach (var token in items)
            {
                if (token.Kind is TokenKind.LeftParen or TokenKind.LeftBracket) opened.Push(token);
                else if (token.Kind is TokenKind.RightParen or TokenKind.RightBracket)
                    result.Add(opened.Pop().Span.Start, token.Span.End);
            }
            return result;
        }
        private void WordGap(Token token)
        {
            if (output.Length == 0 || output[^1] == '\n') return;
            if (previous is not { } prior) return;
            if (token.Kind == TokenKind.String && prior.Kind == TokenKind.Atom && Parser.WindowsPathPrefix(prior.Text)) return;
            if (prior.Kind is TokenKind.LeftParen or TokenKind.LeftBracket or TokenKind.Select or TokenKind.Slash or TokenKind.Dollar) return;
            if (prior.Kind == TokenKind.Atom && token.Kind == TokenKind.Atom)
            {
                var gap = source[prior.Span.End..token.Span.Start];
                if (!gap.Contains('#', StringComparison.Ordinal) && !gap.Contains("//", StringComparison.Ordinal) && gap.All(char.IsWhiteSpace))
                {
                    if (gap.Length > 0 && !gap.Contains('\n', StringComparison.Ordinal) && !gap.Contains('\r', StringComparison.Ordinal)) Text(gap);
                    else Space();
                    return;
                }
            }
            Space();
        }
        private void Comments(string gap)
        {
            foreach (var line in gap.Split('\n'))
            {
                string comment = line.Trim();
                if (!comment.StartsWith('#') && !comment.StartsWith("//", StringComparison.Ordinal)) continue;
                if (output.Length > 0 && output[^1] != '\n') Space();
                Text(comment); Newline();
            }
        }
        private void Text(string text)
        {
            if (text.Length == 0) return;
            if (output.Length == 0 || output[^1] == '\n') output.Append(' ', indent * 4 + continuation);
            output.Append(text);
        }
        private void Space()
        {
            if (output.Length > 0 && !char.IsWhiteSpace(output[^1])) output.Append(' ');
        }
        private void Trim()
        {
            while (output.Length > 0 && output[^1] is ' ' or '\t') output.Length--;
        }
        private void Newline()
        {
            continuation = 0;
            Trim();
            if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
        }
    }
}
