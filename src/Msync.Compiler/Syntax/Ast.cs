namespace Msync.Compiler.Syntax;

public readonly record struct SourceSpan(int Start, int Length)
{
    public int End => Start + Length;
    public static SourceSpan Cover(SourceSpan first, SourceSpan last) => new(first.Start, last.End - first.Start);
}

public sealed record Document(IReadOnlyList<Statement> Statements, SourceSpan Span)
{
    public SyntaxException? EditorError { get; init; }
}
public abstract record Node(SourceSpan Span);
public abstract record Statement(SourceSpan Span) : Node(Span);
public sealed record LetStatement(Pattern Pattern, bool Nullable, TypeSyntax? Type, Expr Value, SourceSpan Span) : Statement(Span);
public sealed record GlobalStatement(IReadOnlyList<NameExpr> Names, SourceSpan Span) : Statement(Span);
public sealed record FunctionDeclaration(string Name, FunctionExpr Function, SourceSpan Span) : Statement(Span);
public sealed record NativeDeclaration(string Name, IReadOnlyList<Parameter> Parameters, TypeSyntax ReturnType, SourceSpan Span) : Statement(Span)
{
    public SourceSpan? NameSpan { get; init; }
}
public sealed record ScopeDeclaration(string Name, ScopeExpr Scope, SourceSpan Span) : Statement(Span)
{
    public SourceSpan? NameSpan { get; init; }
}
public sealed record ConceptDeclaration(string Name, IReadOnlyList<TypeSyntax> Bases, IReadOnlyList<ConceptAssociatedType> Types, IReadOnlyList<ConceptMember> Members, SourceSpan Span) : Statement(Span)
{
    public SourceSpan? NameSpan { get; init; }
}
public sealed record ConceptAssociatedType(string Name, SourceSpan Span) : Node(Span)
{
    public SourceSpan? NameSpan { get; init; }
}
public sealed record ConceptMember(string Name, TypeSyntax? Type, IReadOnlyList<Parameter>? Parameters, TypeSyntax? ReturnType, SourceSpan Span) : Node(Span)
{
    public bool Nullable { get; init; }
    public SourceSpan? NameSpan { get; init; }
}
public sealed record ExpressionStatement(Expr Expression, bool Discarded, SourceSpan Span) : Statement(Span);
public abstract record Expr(SourceSpan Span) : Node(Span);
public sealed record MissingExpr(SourceSpan Span) : Expr(Span);
public sealed record NameExpr(string Name, SourceSpan Span) : Expr(Span);
public sealed record PathExpr(string Path, SourceSpan Span) : Expr(Span);
public sealed record StringExpr(string Value, SourceSpan Span) : Expr(Span);
public sealed record BoolExpr(bool Value, SourceSpan Span) : Expr(Span);
public sealed record NullExpr(SourceSpan Span) : Expr(Span);
public sealed record NumberExpr(double Value, SourceSpan Span) : Expr(Span);
public sealed record CastExpr(Expr Value, TypeSyntax Type, SourceSpan Span) : Expr(Span);
public sealed record UnaryExpr(string Operator, Expr Operand, SourceSpan Span) : Expr(Span);
public sealed record BinaryExpr(string Operator, Expr Left, Expr Right, SourceSpan Span) : Expr(Span)
{
    public SourceSpan? OperatorSpan { get; init; }
}
public sealed record AssignmentExpr(Expr Target, Expr Value, SourceSpan Span) : Expr(Span)
{
    public SourceSpan? OperatorSpan { get; init; }
}
public sealed record BarrierExpr(bool Release, bool Acquire, Expr? Value, SourceSpan Span) : Expr(Span);
public sealed record CallExpr(Expr Callee, IReadOnlyList<Expr> Arguments, SourceSpan Span) : Expr(Span);
public sealed record BlockExpr(IReadOnlyList<Statement> Statements, SourceSpan Span) : Expr(Span)
{
    public bool EditorIncomplete { get; init; }
}
public sealed record IfExpr(Expr Condition, BlockExpr Then, Expr? Else, SourceSpan Span) : Expr(Span);
public sealed record MatchExpr(Expr Value, IReadOnlyList<MatchArm> Arms, SourceSpan Span) : Expr(Span);
public sealed record ForExpr(Pattern Pattern, Expr Source, BlockExpr Body, SourceSpan Span) : Expr(Span);
public sealed record FunctionExpr(string? Name, IReadOnlyList<Parameter> Parameters, TypeSyntax? ReturnType, BlockExpr Body, SourceSpan Span) : Expr(Span)
{
    public SourceSpan? NameSpan { get; init; }
}
public sealed record ScopeExpr(IReadOnlyList<ScopeMember> Members, Expr? Value, SourceSpan Span) : Expr(Span)
{
    public string? Name { get; init; }
    public SourceSpan? NameSpan { get; init; }
    public bool EditorIncomplete { get; init; }
}
public sealed record RangeExpr(IReadOnlyList<Expr> Items, SourceSpan Span) : Expr(Span);
public sealed record TypeExpr(TypeSyntax Type, SourceSpan Span) : Expr(Span);
public sealed record LoopExpr(BlockExpr Body, SourceSpan Span) : Expr(Span);
public sealed record ReturnExpr(Expr? Value, SourceSpan Span) : Expr(Span);
public sealed record BreakExpr(Expr? Value, SourceSpan Span) : Expr(Span);
public sealed record Parameter(string Name, TypeSyntax? Type, bool Nullable, SourceSpan Span) : Node(Span);
public sealed record ScopeMember(string Name, Expr Value, SourceSpan Span) : Node(Span)
{
    public bool Nullable { get; init; }
    public SourceSpan? NameSpan { get; init; }
}
public sealed record MatchArm(Pattern Pattern, Expr Value, SourceSpan Span) : Node(Span);
public abstract record Pattern(SourceSpan Span) : Node(Span);
public sealed record NamePattern(string Name, SourceSpan Span) : Pattern(Span);
public sealed record WildcardPattern(SourceSpan Span) : Pattern(Span);
public sealed record NullPattern(SourceSpan Span) : Pattern(Span);
public sealed record LiteralPattern(Expr Value, SourceSpan Span) : Pattern(Span);
public sealed record TypePattern(TypeSyntax Type, SourceSpan Span) : Pattern(Span);
public sealed record RegexPattern(string Pattern, SourceSpan Span) : Pattern(Span);
public sealed record TuplePattern(IReadOnlyList<Pattern> Items, SourceSpan Span) : Pattern(Span);
public sealed record TypeSyntax(string Name, IReadOnlyList<TypeSyntax> Arguments, IReadOnlyList<TypeSyntax> Alternatives, bool Nullable, SourceSpan Span) : Node(Span);

public sealed class SyntaxException : Exception
{
    public SourceDiagnostic Diagnostic { get; internal set; }
    public string SourceName { get; }
    public SourceSpan Span { get; }
    public int Line { get; }
    public int Column { get; }
    public bool IsIncomplete { get; }
    public SyntaxException(string message, string source, string sourceName, SourceSpan span)
        : this(message, source, sourceName, span, false) { }

    public SyntaxException(string message, string source, string sourceName, SourceSpan span, bool isIncomplete)
        : base($"{sourceName}:{SourceDiagnostic.Locate(source, span.Start).Line}:{SourceDiagnostic.Locate(source, span.Start).Column}: {message}")
    {
        Diagnostic = new("E0100", message, source, sourceName, span,
            isIncomplete ? "input ends here" : "unexpected token", Help: null);
        SourceName = sourceName;
        Span = span;
        IsIncomplete = isIncomplete;
        (Line, Column) = Diagnostic.Position;
    }
}
