using System.Text;

namespace Msync.Compiler.Syntax;

public static class Formatter
{
    public static string Format(string source, string sourceName = "<source>")
    {
        _ = Parser.Parse(source, sourceName);
        return new Writer(source, new Lexer(source, sourceName).Lex()).Write();
    }
    private sealed class Writer(string source, IReadOnlyList<Token> tokens)
    {
        private readonly StringBuilder output = new();
        private readonly Stack<TokenKind> nesting = new();
        private int indent;
        private Token? previous;
        public string Write()
        {
            foreach (var token in tokens)
            {
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
                    Space(); Text("{"); nesting.Push(token.Kind); indent++; Newline(); break;
                case TokenKind.RightBrace:
                    indent--; Newline(); Text("}"); nesting.Pop(); break;
                case TokenKind.LeftParen:
                case TokenKind.LeftBracket:
                    if (token.Kind == TokenKind.LeftBracket && previous?.Kind == TokenKind.Atom) Space();
                    Text(token.Text); nesting.Push(token.Kind); break;
                case TokenKind.RightParen:
                case TokenKind.RightBracket:
                    Trim(); Text(token.Text); nesting.Pop(); break;
                case TokenKind.Semicolon:
                    Trim(); Text(";"); Newline(); break;
                case TokenKind.Comma:
                    Trim(); Text(",");
                    if (nesting.TryPeek(out var kind) && kind == TokenKind.LeftBrace) Newline(); else Space();
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
                    Space(); Text(token.Text); Space(); break;
                default:
                    WordGap(token);
                    Text(source[token.Span.Start..token.Span.End]); break;
            }
        }
        private void WordGap(Token token)
        {
            if (previous is not { } prior) return;
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
            if (output.Length == 0 || output[^1] == '\n') output.Append(' ', indent * 4);
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
            Trim();
            if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
        }
    }
}
