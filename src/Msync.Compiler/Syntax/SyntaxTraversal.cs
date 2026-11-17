namespace Msync.Compiler.Syntax;

internal static class SyntaxTraversal
{
    internal static bool HasStandardReference(Node node) => node is UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } } || Children(node).Any(HasStandardReference);
    internal static IEnumerable<string> Names(Node node)
    {
        if (node is NameExpr name) yield return name.Name;
        foreach (var child in Children(node)) foreach (var nested in Names(child)) yield return nested;
    }
    internal static IEnumerable<string> ReadNames(Node node)
    {
        if (node is GlobalStatement) yield break;
        if (node is AssignmentExpr { Target: NameExpr } assignment)
        {
            foreach (var name in ReadNames(assignment.Value)) yield return name;
            yield break;
        }
        if (node is NameExpr nameExpression) yield return nameExpression.Name;
        foreach (var child in Children(node)) foreach (var nested in ReadNames(child)) yield return nested;
    }
    internal static IEnumerable<Node> Children(Node node) => node switch
    {
        BlockExpr block => block.Statements,
        LetStatement let => let.Failure is null ? [let.Pattern, let.Value] : [let.Pattern, let.Value, let.Failure],
        GlobalStatement global => global.Names,
        ExpressionStatement expression => [expression.Expression],
        FunctionDeclaration declaration => [declaration.Function],
        ScopeDeclaration declaration => [declaration.Scope],
        FunctionExpr function => [function.Body],
        ScopeExpr scope => scope.Members.Cast<Node>().Concat(scope.Value is null ? [] : [scope.Value]),
        ScopeMember member => [member.Value],
        UnaryExpr unary => [unary.Operand],
        BinaryExpr binary => [binary.Left, binary.Right],
        CastExpr cast => [cast.Value],
        AssignmentExpr assignment => [assignment.Target, assignment.Value],
        BarrierExpr { Value: not null } barrier => [barrier.Value],
        CallExpr call => new Node[] { call.Callee }.Concat(call.Arguments),
        IfExpr conditional => new Node[] { conditional.Condition, conditional.Then }.Concat(conditional.Else is null ? [] : [conditional.Else]),
        MatchExpr match => new Node[] { match.Value }.Concat(match.Arms),
        MatchArm arm => arm.Guard is null ? [arm.Pattern, arm.Value] : [arm.Pattern, arm.Guard, arm.Value],
        PatternTestExpr test => [test.Value, test.Pattern],
        BinaryPattern pattern => [pattern.Left, pattern.Right],
        NotPattern pattern => [pattern.Inner],
        CapturePattern pattern => [pattern.Inner, pattern.Binding],
        TuplePattern pattern => pattern.Items,
        ScopePattern pattern => pattern.Fields,
        PatternField field => [field.Pattern],
        RelationPattern pattern => [pattern.Limit],
        DenotationPattern pattern => [pattern.TypeValue],
        LiteralPattern pattern => [pattern.Value],
        ForExpr loop => [loop.Source, loop.Body],
        LoopExpr loop => [loop.Body],
        RangeExpr range => range.Items,
        RangeComprehensionExpr range => new Node[] { range.Source, range.Projection }.Concat(range.Filter is null ? [] : [range.Filter]),
        ReturnExpr returned when returned.Value is not null => [returned.Value],
        BreakExpr broken when broken.Value is not null => [broken.Value],
        _ => []
    };
}
