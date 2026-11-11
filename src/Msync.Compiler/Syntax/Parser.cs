using Msync.Core;

namespace Msync.Compiler.Syntax;

public sealed record EditorParseResult(Document Document, SyntaxException? Error, int? IncompleteStart = null)
{
    public IReadOnlyList<SourceSpan> RegexSpans { get; init; } = [];
    internal IReadOnlyList<Token> Tokens { get; init; } = [];
}

public sealed class Parser
{
    private readonly string source;
    private readonly string sourceName;
    private readonly IReadOnlyList<Token> tokens;
    private readonly bool editorMode;
    private readonly CancellationToken cancellationToken;
    private SyntaxException? firstError;
    private int errorCount;
    private int? incompleteRoot;
    private int position;
    private Parser(string source, string sourceName) : this(source, sourceName, new Lexer(source, sourceName).Lex()) { }
    private Parser(string source, string sourceName, IReadOnlyList<Token> tokens, bool editorMode = false,
        SyntaxException? firstError = null, CancellationToken cancellationToken = default)
    {
        this.source = source;
        this.sourceName = sourceName;
        this.tokens = tokens;
        this.editorMode = editorMode;
        this.firstError = firstError;
        this.cancellationToken = cancellationToken;
    }
    public static Document Parse(string source, string sourceName = "<source>")
    {
        Parser parser = new(source, sourceName);
        var statements = parser.Statements(TokenKind.End);
        return new(statements, new(0, source.Length));
    }

    public static EditorParseResult ParseForEditor(string source, string sourceName = "<source>") =>
        ParseForEditor(source, sourceName, default);

    internal static EditorParseResult ParseForEditor(string source, string sourceName, CancellationToken cancellationToken)
    {
        var tokens = new Lexer(source, sourceName, cancellationToken).LexForEditor(out SyntaxException? error);
        var regexSpans = tokens.Where(token => token.Kind == TokenKind.Regex).Select(token => token.Span).ToList();
        if (error is { IsIncomplete: true } && source[error.Span.Start] == '<') regexSpans.Add(error.Span);
        Parser parser = new(source, sourceName, tokens, editorMode: true, error, cancellationToken);
        List<Statement> statements = [];
        try { _ = parser.Statements(TokenKind.End, statements); }
        catch (SyntaxException syntax)
        {
            if (error is null || syntax.Span.Start < error.Span.Start) error = syntax;
        }
        SyntaxException? first = parser.firstError ?? error;
        return new(new(statements, new(0, source.Length)) { EditorError = first }, first, parser.incompleteRoot)
        { RegexSpans = regexSpans, Tokens = tokens };
    }

    public static IReadOnlyList<SourceSpan> NumberSpans(string source) => new Lexer(source, "<editor>")
        .LexForEditor(out _).Where(token => token.Kind == TokenKind.Number).Select(token => token.Span).ToArray();

    public static (IReadOnlyList<SourceSpan> Numbers, IReadOnlyList<SourceSpan> ArithmeticOperators) HighlightSpans(string source)
    {
        List<SourceSpan> numbers = [], operators = [];
        foreach (Token token in new Lexer(source, "<editor>").LexForEditor(out _))
        {
            if (token.Kind == TokenKind.Number) numbers.Add(token.Span);
            if (token.Kind == TokenKind.Atom && token.Text is "+" or "-" or "*" or "%" or "**") operators.Add(token.Span);
        }
        return (numbers, operators);
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
        if (!At(kind))
        {
            Fail(message);
            return new(kind, "", new(Current.Span.Start, 0));
        }
        return Next();
    }
    private void Fail(string message)
    {
        SyntaxException error = Error(message);
        if (!editorMode) throw error;
    }
    private SyntaxException Error(string message)
    {
        if (editorMode) errorCount++;
        string found = At(TokenKind.End) ? "end of input" : $"'{Current.Text}'";
        SyntaxException error = new($"{message} Found {found}.", source, sourceName, Current.Span, At(TokenKind.End));
        if (message.StartsWith("Expected ';'", StringComparison.Ordinal) || message.StartsWith("Expected ','", StringComparison.Ordinal))
        {
            string separator = message.Contains("';'", StringComparison.Ordinal) ? ";" : ",";
            error.Diagnostic = error.Diagnostic with
            {
                Code = "E0102",
                Help = separator == ";" ? "End this declaration with ';'. In a block, only the final expression may omit it to return a value."
                    : "Separate adjacent members or arguments with ','.",
                Fix = new($"Insert '{separator}'", new(Previous.Span.End, 0), separator)
            };
        }
        else if (UnclosedDelimiter() is { } opening && (At(TokenKind.End) || message.StartsWith("Expected ')'", StringComparison.Ordinal)
            || message.StartsWith("Expected ']'", StringComparison.Ordinal) || message.StartsWith("Expected '}'", StringComparison.Ordinal)))
        {
            string close = opening.Kind switch { TokenKind.LeftBrace => "}", TokenKind.LeftBracket => "]", _ => ")" };
            error.Diagnostic = error.Diagnostic with
            {
                Code = "E0103",
                Help = $"Close the '{opening.Text}' delimiter with '{close}' before continuing."
            };
            error.Diagnostic.Related.Add(new(sourceName, source, opening.Span, $"this '{opening.Text}' is still open"));
        }
        else if (At(TokenKind.End)) error.Diagnostic = error.Diagnostic with { Help = "Complete the expression before submitting it." };
        if (editorMode && (firstError is null || error.Span.Start < firstError.Span.Start)) firstError = error;
        return error;
    }

    private Token? UnclosedDelimiter()
    {
        Stack<Token> open = [];
        for (int index = 0; index < position; index++)
        {
            Token token = tokens[index];
            if (token.Kind is TokenKind.LeftBrace or TokenKind.LeftBracket or TokenKind.LeftParen) open.Push(token);
            else if (open.TryPeek(out Token previous) && (previous.Kind, token.Kind) is
                (TokenKind.LeftBrace, TokenKind.RightBrace) or (TokenKind.LeftBracket, TokenKind.RightBracket) or (TokenKind.LeftParen, TokenKind.RightParen)) open.Pop();
        }
        return open.TryPeek(out Token opening) ? opening : null;
    }
    private IReadOnlyList<Statement> Statements(TokenKind end, List<Statement>? completed = null)
    {
        List<Statement> result = completed ?? [];
        while (!At(end) && !At(TokenKind.End))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Take(TokenKind.Semicolon)) continue;
            int before = position;
            int errors = errorCount;
            int start = Current.Span.Start;
            result.Add(Statement(end));
            if (editorMode && end == TokenKind.End && errors != errorCount) incompleteRoot ??= start;
            if (editorMode && position == before) Next();
        }
        if (end != TokenKind.End && At(TokenKind.End)) Fail("Expected '}' to close the block.");
        return result;
    }
    private Statement Statement(TokenKind end)
    {
        var start = Current.Span;
        if (TakeWord("concept")) return Concept(start);
        if (TakeWord("global")) return Global(start);
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
            Next(); Token name = Next();
            var scope = Scope(start);
            Take(TokenKind.Semicolon);
            return new ScopeDeclaration(name.Text, scope, SourceSpan.Cover(start, Previous.Span)) { NameSpan = name.Span };
        }
        var expression = Expression();
        bool discarded = Take(TokenKind.Semicolon);
        if (!discarded && !At(end) && !At(TokenKind.End)) Fail("Expected ';' between expressions.");
        return new ExpressionStatement(expression, discarded, SourceSpan.Cover(start, Previous.Span));
    }
    private List<ConceptAssociatedType>? conceptTypes;
    private ConceptDeclaration Concept(SourceSpan start)
    {
        var previous = conceptTypes;
        conceptTypes = [];
        try { return ConceptBody(start); }
        finally { conceptTypes = previous; }
    }
    private ConceptDeclaration ConceptBody(SourceSpan start)
    {
        Token name = Require(TokenKind.Atom, "Expected a concept name.");
        Require(TokenKind.LeftBrace, "Expected '{' after the concept name.");
        List<TypeSyntax> bases = [];
        List<ConceptAssociatedType> types = conceptTypes!;
        List<ConceptMember> members = [];
        while (!At(TokenKind.RightBrace) && !At(TokenKind.End))
        {
            int before = position;
            if (TakeWord("type"))
            {
                Token associated = Require(TokenKind.Atom, "Expected an associated type name.");
                types.Add(new(associated.Text, associated.Span) { NameSpan = associated.Span });
            }
            else if ((At(TokenKind.Atom) || At(TokenKind.String)) && (Peek().Kind == TokenKind.Colon ||
                Peek().Kind == TokenKind.Question && Peek(2).Kind == TokenKind.Colon))
            {
                Token member = Next();
                bool nullable = Take(TokenKind.Question);
                Require(TokenKind.Colon, "Expected ':' after the required member name.");
                TypeSyntax? type = null, result = null;
                IReadOnlyList<Parameter>? parameters = null;
                if (TakeWord("fn"))
                {
                    parameters = At(TokenKind.LeftParen) ? Parameters() : [];
                    Require(TokenKind.Arrow, "Expected '->' and the required result type.");
                    result = ParseType();
                }
                else type = ParseType();
                members.Add(new(member.Text, type, parameters, result, SourceSpan.Cover(member.Span, Previous.Span))
                { Nullable = nullable, NameSpan = member.Span });
            }
            else bases.Add(ParseType());
            if (!Take(TokenKind.Comma) && !Take(TokenKind.Semicolon) && !At(TokenKind.RightBrace))
                Fail("Expected ',' or ';' after a concept requirement.");
            if (editorMode && position == before) Next();
        }
        Token close = Require(TokenKind.RightBrace, "Expected '}' after concept requirements.");
        Require(TokenKind.Semicolon, "Expected ';' after the concept declaration.");
        return new(name.Text, bases, types, members, SourceSpan.Cover(start, close.Span)) { NameSpan = name.Span };
    }

    private GlobalStatement Global(SourceSpan start)
    {
        List<NameExpr> names = [];
        while (true)
        {
            if (!At(TokenKind.Atom))
            {
                Fail("Expected a binding name after 'global' or ','.");
                break;
            }
            var name = Next();
            names.Add(new(name.Text, name.Span));
            if (Take(TokenKind.Comma)) continue;
            if (!At(TokenKind.Atom)) break;
            Fail("Expected ',' between global binding names.");
        }
        Require(TokenKind.Semicolon, "Expected ';' after the global import.");
        return new(names, SourceSpan.Cover(start, Previous.Span));
    }
    private NativeDeclaration Native()
    {
        var start = Next().Span;
        if (!TakeWord("fn")) Fail("Expected 'fn' after 'native'.");
        Token name = Require(TokenKind.Atom, "Expected a native function name.");
        var parameters = Parameters();
        Require(TokenKind.Arrow, "Expected '->' and the native result type.");
        var type = ParseType();
        Require(TokenKind.Semicolon, "Expected ';' after the native declaration.");
        return new(name.Text, parameters, type, SourceSpan.Cover(start, Previous.Span)) { NameSpan = name.Span };
    }
    private Expr Expression(int minimum = 0, bool header = false, bool stopSelection = false)
    {
        Expr left = Prefix(header, stopSelection);
        while (true)
        {
            if (At(TokenKind.LeftParen) && 15 >= minimum) { left = Call(left); continue; }
            if (At(TokenKind.Question) && 15 >= minimum)
            {
                Next(); left = new UnaryExpr("?", left, SourceSpan.Cover(left.Span, Previous.Span)); continue;
            }
            string? op = Operator();
            if (stopSelection && op == "/" && Peek().Kind == TokenKind.Regex) break;
            int precedence = op == "/" && ExplicitPath(left) ? 16 : Precedence(op);
            if (op is null || precedence < minimum) break;
            Token operation = Next();
            if (op == "as")
            {
                TypeSyntax type = ParseType();
                left = new CastExpr(left, type, SourceSpan.Cover(left.Span, type.Span)) { OperatorSpan = operation.Span };
                continue;
            }
            if (op == "is" && TakeWord("not")) op = "is not";
            Expr right;
            if (op == "/" && IsPathEnd(header)) right = new PathExpr("", Previous.Span);
            else if (op == "::" && At(TokenKind.Atom))
            {
                var member = Next();
                right = new NameExpr(member.Text, member.Span);
                if (WindowsPathBeforeString(member)) right = WindowsPathWithString(member);
            }
            else right = Expression(precedence + (op is "=" or "**" ? 0 : 1), header, stopSelection: op == "@");
            left = op == "="
                ? new AssignmentExpr(left, right, SourceSpan.Cover(left.Span, right.Span)) { OperatorSpan = operation.Span }
                : new BinaryExpr(op, left, right, SourceSpan.Cover(left.Span, right.Span)) { OperatorSpan = operation.Span };
        }
        return left;
    }
    private string? Operator() => Current.Kind switch
    {
        TokenKind.Equal => "=",
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
        TokenKind.At => "@",
        TokenKind.Atom when Current.Text is "and" or "or" or "is" or "+" or "-" or "*" or "**" or "%" or "as" => Current.Text,
        _ => null
    };
    private static bool ExplicitPath(Expr value) => value switch
    {
        PathExpr => true,
        NameExpr { Name: "~" or "." or ".." } => true,
        NameExpr name => WindowsPathPrefix(name.Name),
        BinaryExpr { Operator: "/" } path => ExplicitPath(path.Left),
        _ => false
    };
    private static int Precedence(string? op) => op switch
    {
        "=" => 0,
        "->" => 1,
        "or" => 2,
        "and" => 3,
        "|" => 4,
        "is" or "is not" or "==" or "!=" => 5,
        "<" or "<=" or ">" or ">=" => 6,
        "+" or "-" => 7,
        "*" or "/" or "%" => 8,
        "@" => 9,
        "as" => 10,
        "**" => 13,
        "::" => 17,
        _ => -1
    };
    private bool IsExpressionEnd(bool header = false) => Current.Kind is TokenKind.End or TokenKind.RightBrace or TokenKind.RightParen or TokenKind.RightBracket or TokenKind.Comma or TokenKind.Semicolon or TokenKind.Arrow or TokenKind.At || Word("in") || header && At(TokenKind.LeftBrace);
    private bool IsPathEnd(bool header)
    {
        // Adjacency keeps ./and a filename; a separated operator resumes expression parsing.
        return IsExpressionEnd(header) || Current.Span.Start > Previous.Span.End && Operator() is
            "and" or "or" or "is" or "as" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "|" or "=";
    }
    private Expr Prefix(bool header, bool stopSelection = false)
    {
        Token token = Current;
        if (Word("release") || Word("acquire") || Word("acqrel")) return Barrier(header);
        if (TakeWord("if")) return If(token.Span);
        if (TakeWord("match")) return Match(token.Span);
        if (TakeWord("for")) return For(token.Span);
        if (Word("fn")) return Function();
        if (TakeWord("scope"))
        {
            Token? name = At(TokenKind.Atom) && Peek().Kind == TokenKind.LeftBrace ? Next() : null;
            return Scope(token.Span) with { Name = name?.Text, NameSpan = name?.Span };
        }
        if (TakeWord("loop")) { var body = Block(); return new LoopExpr(body, SourceSpan.Cover(token.Span, body.Span)); }
        if (TakeWord("return")) { Expr? value = IsExpressionEnd() ? null : Expression(header: header); return new ReturnExpr(value, SourceSpan.Cover(token.Span, value?.Span ?? token.Span)); }
        if (TakeWord("break")) { Expr? value = IsExpressionEnd() ? null : Expression(header: header); return new BreakExpr(value, SourceSpan.Cover(token.Span, value?.Span ?? token.Span)); }
        if (TakeWord("exists") || TakeWord("not"))
        {
            var operand = Expression(token.Text == "not" ? 5 : 8, header);
            return new UnaryExpr(token.Text, operand, SourceSpan.Cover(token.Span, operand.Span));
        }
        if (TakeWord("-") || TakeWord("+"))
        {
            var operand = Expression(13, header);
            return new UnaryExpr(token.Text, operand, SourceSpan.Cover(token.Span, operand.Span));
        }
        if (Take(TokenKind.Dollar))
        {
            var operand = Expression(18, header);
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
            while (!At(TokenKind.RightBracket) && !At(TokenKind.End))
            {
                items.Add(Expression());
                if (!Take(TokenKind.Comma)) break;
            }
            Require(TokenKind.RightBracket, "Expected ']'.");
            return new RangeExpr(items, SourceSpan.Cover(token.Span, Previous.Span));
        }
        if (stopSelection && At(TokenKind.Slash) && Peek().Kind == TokenKind.Regex)
            return new PathExpr("/", token.Span);
        if (Take(TokenKind.Slash))
        {
            if (IsPathEnd(header)) return new PathExpr("/", token.Span);
            var right = Expression(17, header);
            return new BinaryExpr("/", new PathExpr("/", token.Span), right, SourceSpan.Cover(token.Span, right.Span));
        }
        if (Take(TokenKind.String)) return new StringExpr(token.Text, token.Span);
        if (Take(TokenKind.Number)) return new NumberExpr(NumberSyntax.TryParse(token.Text, out double number) ? number : 0, token.Span);
        if (Take(TokenKind.Regex)) return new PathExpr("<" + token.Text + ">", token.Span);
        if (!At(TokenKind.Atom))
        {
            Fail("Expected an expression.");
            return new MissingExpr(new(Current.Span.Start, 0));
        }
        if (LanguageTypes.Contains(token.Text)) { var type = ParseType(); return new TypeExpr(type, type.Span); }
        Next();
        if (WindowsPathBeforeString(token)) return WindowsPathWithString(token);
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
            _ => new NameExpr(token.Text, token.Span)
        };
    }
    private BarrierExpr Barrier(bool header)
    {
        SourceSpan start = Current.Span;
        bool release = false, acquire = false;
        while (Word("release") || Word("acquire") || Word("acqrel"))
        {
            bool before = Current.Text is "release" or "acqrel";
            bool after = Current.Text is "acquire" or "acqrel";
            if (before && release || after && acquire)
                Fail("Duplicate scheduling prefix. Use release and acquire once, or acqrel to combine them.");
            release |= before;
            acquire |= after;
            Next();
        }
        Expr? value = IsExpressionEnd() ? null : Expression(header: header);
        return new(release, acquire, value, SourceSpan.Cover(start, value?.Span ?? Previous.Span));
    }

    private bool WindowsPathBeforeString(Token token) => WindowsPathPrefix(token.Text) && At(TokenKind.String);

    internal static bool WindowsPathPrefix(string text) => text.Length > 0 && text[^1] is '/' or '\\'
        && (text[0] == '\\' || text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && text[2] is '/' or '\\');

    private BinaryExpr WindowsPathWithString(Token token)
    {
        Token child = Next();
        return new("/", new PathExpr(token.Text, token.Span), new StringExpr(child.Text, child.Span),
            SourceSpan.Cover(token.Span, child.Span))
        { OperatorSpan = new(token.Span.End - 1, 1) };
    }

    private CallExpr Call(Expr callee)
    {
        Next(); List<Expr> args = [];
        while (!At(TokenKind.RightParen) && !At(TokenKind.End))
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
        Token close = Require(TokenKind.RightBrace, "Expected '}'.");
        return new(statements, SourceSpan.Cover(start, close.Span)) { EditorIncomplete = close.Text.Length == 0 };
    }
    private FunctionExpr Function()
    {
        var start = Next().Span;
        Token? name = At(TokenKind.Atom) ? Next() : null;
        var parameters = At(TokenKind.LeftParen) ? Parameters() : [];
        TypeSyntax? result = Take(TokenKind.Arrow) ? ParseType() : null;
        var body = Block();
        return new(name?.Text, parameters, result, body, SourceSpan.Cover(start, body.Span)) { NameSpan = name?.Span };
    }
    private IReadOnlyList<Parameter> Parameters()
    {
        Require(TokenKind.LeftParen, "Expected '('."); List<Parameter> result = [];
        while (!At(TokenKind.RightParen) && !At(TokenKind.End))
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
        while (!At(TokenKind.RightBrace) && !At(TokenKind.End))
        {
            if ((At(TokenKind.Atom) || At(TokenKind.String)) &&
                (Peek().Kind == TokenKind.Colon || Peek().Kind == TokenKind.Question && Peek(2).Kind == TokenKind.Colon))
            {
                var name = Next();
                bool nullable = Take(TokenKind.Question);
                Require(TokenKind.Colon, "Expected ':' after the scope member name.");
                var member = Expression();
                members.Add(new(name.Text, member, SourceSpan.Cover(name.Span, member.Span))
                { NameSpan = name.Span, Nullable = nullable });
                if (!Take(TokenKind.Comma) && !At(TokenKind.RightBrace)) Fail("Expected ',' after a scope member.");
                continue;
            }
            value = Expression(); Take(TokenKind.Comma); Take(TokenKind.Semicolon);
            if (!At(TokenKind.RightBrace))
            {
                Fail("A scope's final value must be its last expression.");
                if (editorMode && !At(TokenKind.End)) Next();
            }
        }
        Token close = Require(TokenKind.RightBrace, "Expected '}' after scope members.");
        return new(members, value, SourceSpan.Cover(start, close.Span)) { EditorIncomplete = close.Text.Length == 0 };
    }
    private IfExpr If(SourceSpan start)
    {
        var condition = Expression(header: true); var then = Block(); Expr? otherwise = null;
        if (TakeWord("else")) otherwise = TakeWord("if") ? If(Previous.Span) : Block();
        return new(condition, then, otherwise, SourceSpan.Cover(start, otherwise?.Span ?? then.Span));
    }
    private MatchExpr Match(SourceSpan start)
    {
        var value = Expression(header: true); Require(TokenKind.LeftBrace, "Expected '{' after match value.");
        List<MatchArm> arms = [];
        while (!At(TokenKind.RightBrace) && !At(TokenKind.End))
        {
            var pattern = ParsePattern(true);
            Require(TokenKind.FatArrow, "Expected '=>' after match pattern.");
            var armValue = Expression();
            arms.Add(new(pattern, armValue, SourceSpan.Cover(pattern.Span, armValue.Span)));
            if (!Take(TokenKind.Comma) && !At(TokenKind.RightBrace)) Fail("Expected ',' between match arms.");
        }
        Require(TokenKind.RightBrace, "Expected '}' after match arms.");
        return new(value, arms, SourceSpan.Cover(start, Previous.Span));
    }
    private ForExpr For(SourceSpan start)
    {
        var pattern = ParsePattern(false);
        if (!TakeWord("in")) Fail("Expected 'in' after the loop binding.");
        var values = Expression(header: true); var body = Block();
        return new(pattern, values, body, SourceSpan.Cover(start, body.Span));
    }
    private Pattern ParsePattern(bool matching)
    {
        var token = Current;
        if (Take(TokenKind.LeftParen) || Take(TokenKind.LeftBracket))
        {
            var close = token.Kind == TokenKind.LeftParen ? TokenKind.RightParen : TokenKind.RightBracket;
            List<Pattern> items = [];
            while (!At(close) && !At(TokenKind.End)) { items.Add(ParsePattern(matching)); if (!Take(TokenKind.Comma)) break; }
            Require(close, "Expected the pattern's closing delimiter.");
            return new TuplePattern(items, SourceSpan.Cover(token.Span, Previous.Span));
        }
        if (TakeWord("_")) return new WildcardPattern(token.Span);
        if (matching && TakeWord("null")) return new NullPattern(token.Span);
        if (matching && Take(TokenKind.Regex)) return new RegexPattern(token.Text, token.Span);
        if (matching && LanguageTypes.Contains(token.Text)) { var type = ParseType(); return new TypePattern(type, type.Span); }
        if (matching && (At(TokenKind.String) || At(TokenKind.Number) || Word("true") || Word("false") ||
            (Word("+") || Word("-")) && Peek().Kind == TokenKind.Number))
        { var literal = Prefix(false); return new LiteralPattern(literal, literal.Span); }
        return new NamePattern(Require(TokenKind.Atom, "Expected a binding or match pattern.").Text, token.Span);
    }
    private TypeSyntax ParseType(bool union = true)
    {
        var start = Current.Span;
        bool declares = TakeWord("typename");
        if (declares && conceptTypes is null)
            Fail("An inline 'typename' declaration belongs in a concept requirement.");
        bool reference = Take(TokenKind.Dollar);
        var name = Require(TokenKind.Atom, "Expected a type name.");
        if (declares)
        {
            if (reference || At(TokenKind.Select) || LanguageTypes.Contains(name.Text))
                Fail("Declare an associated type with a new unqualified name after 'typename'.");
            conceptTypes!.Add(new(name.Text, SourceSpan.Cover(start, name.Span)) { NameSpan = name.Span });
        }
        string qualified = (reference ? "$" : "") + name.Text;
        while (Take(TokenKind.Select))
            qualified += "::" + Require(TokenKind.Atom, "Expected a type or concept name after '::'.").Text;
        List<TypeSyntax> arguments = [], alternatives = [];
        if (Take(TokenKind.Less))
        {
            do { arguments.Add(ParseType()); } while (Take(TokenKind.Comma));
            Require(TokenKind.Greater, "Expected '>' after generic type arguments.");
        }
        bool nullable = Take(TokenKind.Question);
        if (union) while (Take(TokenKind.Pipe)) alternatives.Add(ParseType(false));
        return new(qualified, arguments, alternatives, nullable,
            new(start.Start, Math.Max(name.Span.End, Previous.Span.End) - start.Start)) { DeclaresAssociatedType = declares };
    }
    private bool ContinuesBarePath() => source[Previous.Span.End..Current.Span.Start].All(c => c is ' ' or '\t');
    private static bool CanContinuePath(string text) => text is not ("+" or "-" or "*" or "**" or "%" or "as" or "concept" or "true" or "false" or "null" or "and" or "or" or "is" or "not" or "in" or "else" or "if" or "fn" or "scope" or "let" or "match" or "for" or "exists" or "return" or "break" or "loop" or "native" or "release" or "acquire" or "acqrel") && !LanguageTypes.Contains(text);
}
