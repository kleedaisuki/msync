namespace Msync.Compiler.Syntax;

public sealed class Parser
{
    private readonly string source;
    private readonly string sourceName;
    private readonly IReadOnlyList<Token> tokens;
    private int position;
    private Parser(string source, string sourceName)
    {
        this.source = source;
        this.sourceName = sourceName;
        tokens = new Lexer(source, sourceName).Lex();
    }
    public static Document Parse(string source, string sourceName = "<source>")
    {
        Parser parser = new(source, sourceName);
        var statements = parser.Statements(TokenKind.End);
        return new(statements, new(0, source.Length));
    }
    private Token Current => tokens[position];
    private Token Previous => tokens[Math.Max(0, position - 1)];
    private Token Peek(int distance = 1) => tokens[Math.Min(position + distance, tokens.Count - 1)];
    private bool At(TokenKind kind) => Current.Kind == kind;
    private bool Word(string value) => At(TokenKind.Atom) && Current.Text == value;
    private Token Next() => tokens[position++];
    private bool Take(TokenKind kind) { if (!At(kind)) return false; position++; return true; }
    private bool TakeWord(string value) { if (!Word(value)) return false; position++; return true; }
    private Token Require(TokenKind kind, string message)
    {
        if (!At(kind)) throw Error(message);
        return Next();
    }
    private SyntaxException Error(string message) => new(message, source, sourceName, Current.Span);
    private IReadOnlyList<Statement> Statements(TokenKind end)
    {
        List<Statement> result = [];
        while (!At(end) && !At(TokenKind.End))
        {
            if (Take(TokenKind.Semicolon)) continue;
            result.Add(Statement(end));
        }
        if (end != TokenKind.End && At(TokenKind.End)) throw Error("Expected '}' to close the block.");
        return result;
    }
    private Statement Statement(TokenKind end)
    {
        var start = Current.Span;
        if (TakeWord("let"))
        {
            var pattern = ParsePattern(false);
            bool nullable = Take(TokenKind.Question);
            TypeSyntax? type = Take(TokenKind.Colon) ? ParseType() : null;
            Require(TokenKind.Equal, "Expected '=' after the binding pattern.");
            var value = Expression();
            Require(TokenKind.Semicolon, "Expected ';' after the let declaration.");
            return new LetStatement(pattern, nullable, type, value, SourceSpan.Cover(start, Previous.Span));
        }
        if (Word("native")) return Native();
        if (Word("fn") && Peek().Kind == TokenKind.Atom)
        {
            var function = Function();
            Take(TokenKind.Semicolon);
            return new FunctionDeclaration(function.Name!, function, SourceSpan.Cover(start, Previous.Span));
        }
        if (Word("scope") && Peek().Kind == TokenKind.Atom)
        {
            Next(); string name = Next().Text;
            var scope = Scope(start);
            Take(TokenKind.Semicolon);
            return new ScopeDeclaration(name, scope, SourceSpan.Cover(start, Previous.Span));
        }
        var expression = Expression();
        bool discarded = Take(TokenKind.Semicolon);
        if (!discarded && !At(end) && !At(TokenKind.End)) throw Error("Expected ';' between expressions.");
        return new ExpressionStatement(expression, discarded, SourceSpan.Cover(start, Previous.Span));
    }
    private NativeDeclaration Native()
    {
        var start = Next().Span;
        if (!TakeWord("fn")) throw Error("Expected 'fn' after 'native'.");
        string name = Require(TokenKind.Atom, "Expected a native function name.").Text;
        var parameters = Parameters();
        Require(TokenKind.Arrow, "Expected '->' and the native result type.");
        var type = ParseType();
        Require(TokenKind.Semicolon, "Expected ';' after the native declaration.");
        return new(name, parameters, type, SourceSpan.Cover(start, Previous.Span));
    }
    private Expr Expression(int minimum = 0)
    {
        Expr left = Prefix();
        while (true)
        {
            if (At(TokenKind.LeftParen) && 9 >= minimum) { left = Call(left); continue; }
            if (At(TokenKind.Question) && 9 >= minimum)
            {
                Next(); left = new UnaryExpr("?", left, SourceSpan.Cover(left.Span, Previous.Span)); continue;
            }
            string? op = Operator();
            int precedence = Precedence(op);
            if (op is null || precedence < minimum) break;
            Next();
            if (op == "is" && TakeWord("not")) op = "is not";
            Expr right;
            if (op == "/" && IsExpressionEnd()) right = new PathExpr("", Previous.Span);
            else if (op == "::" && At(TokenKind.Atom))
            {
                var member = Next();
                right = IsIdentifier(member.Text) ? new NameExpr(member.Text, member.Span) : new PathExpr(member.Text, member.Span);
            }
            else right = Expression(precedence + 1);
            left = new BinaryExpr(op, left, right, SourceSpan.Cover(left.Span, right.Span));
        }
        return left;
    }
    private string? Operator() => Current.Kind switch
    {
        TokenKind.Arrow => "->",
        TokenKind.Pipe => "|",
        TokenKind.EqualEqual => "==",
        TokenKind.NotEqual => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEqual => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEqual => ">=",
        TokenKind.Select => "::",
        TokenKind.Slash => "/",
        TokenKind.Atom when Current.Text is "and" or "or" or "is" or "+" or "-" or "*" => Current.Text,
        _ => null
    };
    private static int Precedence(string? op) => op switch
    {
        "->" => 1,
        "or" => 2,
        "and" => 3,
        "|" => 4,
        "is" or "is not" or "==" or "!=" => 5,
        "<" or "<=" or ">" or ">=" => 6,
        "+" or "-" => 7,
        "*" => 8,
        "/" => 11,
        "::" => 12,
        _ => -1
    };
    private bool IsExpressionEnd() => Current.Kind is TokenKind.End or TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket or TokenKind.Comma or TokenKind.Semicolon or TokenKind.Arrow || Word("in");
    private Expr Prefix()
    {
        Token token = Current;
        if (TakeWord("if")) return If(token.Span);
        if (TakeWord("match")) return Match(token.Span);
        if (TakeWord("for")) return For(token.Span);
        if (Word("fn")) return Function();
        if (TakeWord("scope"))
        {
            if (At(TokenKind.Atom) && Peek().Kind == TokenKind.LeftBrace) Next();
            return Scope(token.Span);
        }
        if (TakeWord("loop")) { var body = Block(); return new LoopExpr(body, SourceSpan.Cover(token.Span, body.Span)); }
        if (TakeWord("return")) { Expr? value = IsExpressionEnd() ? null : Expression(); return new ReturnExpr(value, SourceSpan.Cover(token.Span, value?.Span ?? token.Span)); }
        if (TakeWord("break")) { Expr? value = IsExpressionEnd() ? null : Expression(); return new BreakExpr(value, SourceSpan.Cover(token.Span, value?.Span ?? token.Span)); }
        if (TakeWord("exists") || TakeWord("not"))
        {
            var operand = Expression(token.Text == "not" ? 5 : 9);
            return new UnaryExpr(token.Text, operand, SourceSpan.Cover(token.Span, operand.Span));
        }
        if (TakeWord("-"))
        {
            var operand = Expression(9);
            return new UnaryExpr("-", operand, SourceSpan.Cover(token.Span, operand.Span));
        }
        if (Take(TokenKind.Dollar))
        {
            var operand = Expression(13);
            return new UnaryExpr("$", operand, SourceSpan.Cover(token.Span, operand.Span));
        }
        if (At(TokenKind.LeftBrace)) return Block();
        if (Take(TokenKind.LeftParen))
        {
            var inner = Expression(); Require(TokenKind.RightParen, "Expected ')'."); return inner;
        }
        if (Take(TokenKind.LeftBracket))
        {
            List<Expr> items = [];
            while (!At(TokenKind.RightBracket))
            {
                items.Add(Expression());
                if (!Take(TokenKind.Comma)) break;
            }
            Require(TokenKind.RightBracket, "Expected ']'.");
            return new RangeExpr(items, SourceSpan.Cover(token.Span, Previous.Span));
        }
        if (Take(TokenKind.Slash))
        {
            if (IsExpressionEnd()) return new PathExpr("/", token.Span);
            var right = Expression(12);
            return new BinaryExpr("/", new PathExpr("/", token.Span), right, SourceSpan.Cover(token.Span, right.Span));
        }
        if (Take(TokenKind.String)) return new StringExpr(token.Text, token.Span);
        if (Take(TokenKind.Regex)) return new PathExpr("<" + token.Text + ">", token.Span);
        if (!At(TokenKind.Atom)) throw Error("Expected an expression.");
        if (TypeNames.Contains(token.Text)) { var type = ParseType(); return new TypeExpr(type, type.Span); }
        Next();
        if (CanContinuePath(token.Text) && At(TokenKind.Atom) && CanContinuePath(Current.Text) && ContinuesBarePath())
        {
            while (At(TokenKind.Atom) && CanContinuePath(Current.Text) && ContinuesBarePath()) Next();
            var span = SourceSpan.Cover(token.Span, Previous.Span);
            return new PathExpr(source[span.Start..span.End], span);
        }
        return token.Text switch
        {
            "true" => new BoolExpr(true, token.Span),
            "false" => new BoolExpr(false, token.Span),
            "null" => new NullExpr(token.Span),
            _ when long.TryParse(token.Text, out long value) => new NumberExpr(value, token.Span),
            _ when IsIdentifier(token.Text) => new NameExpr(token.Text, token.Span),
            _ => new PathExpr(token.Text, token.Span)
        };
    }
    private CallExpr Call(Expr callee)
    {
        Next(); List<Expr> args = [];
        while (!At(TokenKind.RightParen))
        {
            args.Add(Expression());
            if (!Take(TokenKind.Comma)) break;
        }
        Require(TokenKind.RightParen, "Expected ')' after function arguments.");
        return new(callee, args, SourceSpan.Cover(callee.Span, Previous.Span));
    }
    private BlockExpr Block()
    {
        var start = Require(TokenKind.LeftBrace, "Expected '{'.").Span;
        var statements = Statements(TokenKind.RightBrace);
        Require(TokenKind.RightBrace, "Expected '}'.");
        return new(statements, SourceSpan.Cover(start, Previous.Span));
    }
    private FunctionExpr Function()
    {
        var start = Next().Span;
        string? name = At(TokenKind.Atom) ? Next().Text : null;
        var parameters = At(TokenKind.LeftParen) ? Parameters() : [];
        TypeSyntax? result = Take(TokenKind.Arrow) ? ParseType() : null;
        var body = Block();
        return new(name, parameters, result, body, SourceSpan.Cover(start, body.Span));
    }
    private IReadOnlyList<Parameter> Parameters()
    {
        Require(TokenKind.LeftParen, "Expected '('."); List<Parameter> result = [];
        while (!At(TokenKind.RightParen))
        {
            var name = Require(TokenKind.Atom, "Expected a parameter name.");
            bool nullable = Take(TokenKind.Question);
            TypeSyntax? type = Take(TokenKind.Colon) ? ParseType() : null;
            result.Add(new(name.Text, type, nullable, SourceSpan.Cover(name.Span, Previous.Span)));
            if (!Take(TokenKind.Comma)) break;
        }
        Require(TokenKind.RightParen, "Expected ')' after parameters.");
        return result;
    }
    private ScopeExpr Scope(SourceSpan start)
    {
        Require(TokenKind.LeftBrace, "Expected '{' after scope.");
        List<ScopeMember> members = [];
        Expr? value = null;
        while (!At(TokenKind.RightBrace))
        {
            if ((At(TokenKind.Atom) || At(TokenKind.String)) && Peek().Kind == TokenKind.Colon)
            {
                var name = Next(); Next(); var member = Expression();
                members.Add(new(name.Text, member, SourceSpan.Cover(name.Span, member.Span)));
                if (!Take(TokenKind.Comma) && !At(TokenKind.RightBrace)) throw Error("Expected ',' after a scope member.");
                continue;
            }
            value = Expression(); Take(TokenKind.Comma); Take(TokenKind.Semicolon);
            if (!At(TokenKind.RightBrace)) throw Error("A scope's final value must be its last expression.");
        }
        Require(TokenKind.RightBrace, "Expected '}' after scope members.");
        return new(members, value, SourceSpan.Cover(start, Previous.Span));
    }
    private IfExpr If(SourceSpan start)
    {
        var condition = Expression(); var then = Block(); Expr? otherwise = null;
        if (TakeWord("else")) otherwise = TakeWord("if") ? If(Previous.Span) : Block();
        return new(condition, then, otherwise, SourceSpan.Cover(start, otherwise?.Span ?? then.Span));
    }
    private MatchExpr Match(SourceSpan start)
    {
        var value = Expression(); Require(TokenKind.LeftBrace, "Expected '{' after match value.");
        List<MatchArm> arms = [];
        while (!At(TokenKind.RightBrace))
        {
            var pattern = ParsePattern(true);
            Require(TokenKind.FatArrow, "Expected '=>' after match pattern.");
            var armValue = Expression();
            arms.Add(new(pattern, armValue, SourceSpan.Cover(pattern.Span, armValue.Span)));
            if (!Take(TokenKind.Comma) && !At(TokenKind.RightBrace)) throw Error("Expected ',' between match arms.");
        }
        Require(TokenKind.RightBrace, "Expected '}' after match arms.");
        return new(value, arms, SourceSpan.Cover(start, Previous.Span));
    }
    private ForExpr For(SourceSpan start)
    {
        var pattern = ParsePattern(false);
        if (!TakeWord("in")) throw Error("Expected 'in' after the loop binding.");
        var values = Expression(); var body = Block();
        return new(pattern, values, body, SourceSpan.Cover(start, body.Span));
    }
    private Pattern ParsePattern(bool matching)
    {
        var token = Current;
        if (Take(TokenKind.LeftParen) || Take(TokenKind.LeftBracket))
        {
            var close = token.Kind == TokenKind.LeftParen ? TokenKind.RightParen : TokenKind.RightBracket;
            List<Pattern> items = [];
            while (!At(close)) { items.Add(ParsePattern(matching)); if (!Take(TokenKind.Comma)) break; }
            Require(close, "Expected the pattern's closing delimiter.");
            return new TuplePattern(items, SourceSpan.Cover(token.Span, Previous.Span));
        }
        if (TakeWord("_")) return new WildcardPattern(token.Span);
        if (matching && TakeWord("null")) return new NullPattern(token.Span);
        if (matching && Take(TokenKind.Regex)) return new RegexPattern(token.Text, token.Span);
        if (matching && TypeNames.Contains(token.Text)) { var type = ParseType(); return new TypePattern(type, type.Span); }
        if (matching && (At(TokenKind.String) || Word("true") || Word("false") || long.TryParse(token.Text, out _)))
        { var literal = Prefix(); return new LiteralPattern(literal, literal.Span); }
        return new NamePattern(Require(TokenKind.Atom, "Expected a binding or match pattern.").Text, token.Span);
    }
    private static readonly HashSet<string> TypeNames = ["Bool", "Range", "String", "Function", "Scope", "Path", "Process", "Connection", "Null", "Type", "Number", "Int"];
    private TypeSyntax ParseType(bool union = true)
    {
        var name = Require(TokenKind.Atom, "Expected a type name.");
        List<TypeSyntax> arguments = [], alternatives = [];
        if (Take(TokenKind.Less))
        {
            do { arguments.Add(ParseType()); } while (Take(TokenKind.Comma));
            Require(TokenKind.Greater, "Expected '>' after generic type arguments.");
        }
        bool nullable = Take(TokenKind.Question);
        if (union) while (Take(TokenKind.Pipe)) alternatives.Add(ParseType(false));
        return new(name.Text, arguments, alternatives, nullable, SourceSpan.Cover(name.Span, Previous.Span));
    }
    private bool ContinuesBarePath() => source[Previous.Span.End..Current.Span.Start].All(c => c is ' ' or '\t');
    private static bool CanContinuePath(string text) => text is not ("+" or "-" or "*" or "true" or "false" or "null" or "and" or "or" or "is" or "not" or "in" or "else" or "if" or "fn" or "scope" or "let" or "match" or "for" or "exists" or "return" or "break" or "loop" or "native") && !TypeNames.Contains(text);
    private static bool IsIdentifier(string text) => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');
}
