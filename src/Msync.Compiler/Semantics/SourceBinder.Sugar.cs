using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private HirNodeId SourceSugar(BinaryExpr syntax) => Trace is null ? BindSourceSugar(syntax)
        : Trace.MeasureDetailed("compiler.bind.source_sugar", (self: this, syntax),
            static state => state.self.BindSourceSugar(state.syntax),
            attributes: new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, Range(syntax.Span)))
            { ["operator"] = syntax.Operator });

    private HirNodeId BindSourceSugar(BinaryExpr syntax) => syntax.Operator switch
    {
        ".." => CallNative("ranges.closed", hir.Type(new TypeRef(TypeKind.Range, TypeRef.Number)), syntax.Span,
            [Expression(syntax.Left), Expression(syntax.Right)]),
        "??" => WithSugarValue(syntax.Left, syntax.Span, value =>
            NullChoice(Expression(value), Expression(syntax.Right), Expression(value), syntax.Span)),
        "?::" => WithSugarValue(syntax.Left, syntax.Span, value =>
            NullChoice(Expression(value), Null(syntax.Span),
                Binary(syntax with { Left = value, Operator = "::" }), syntax.Span)),
        _ => CoalesceAssignment(syntax)
    };

    private HirNodeId CoalesceAssignment(BinaryExpr syntax)
    {
        if (syntax.Left is NameExpr name) return AssignWhenNull(name);
        if (syntax.Left is BinaryExpr { Operator: "::" } member)
            return WithSugarValue(member.Left, syntax.Span, value => AssignWhenNull(member with { Left = value }));
        throw Error("Assign to a binding or Scope field.", syntax.Left.Span, "E0204");

        HirNodeId AssignWhenNull(Expr target) =>
            NullChoice(Expression(target), Assignment(new(target, syntax.Right, syntax.Span)),
                Null(syntax.Span), syntax.Span);
    }

    private HirNodeId NullChoice(HirNodeId value, HirNodeId whenNull, HirNodeId whenPresent, SourceSpan span)
    {
        var test = Node(HirKind.IsNull, hir.Type(TypeRef.Bool), span, [value]);
        return Node(HirKind.If, OpenType("null branch"), span, [test, whenNull, whenPresent]);
    }

    private HirNodeId RangeComprehension(RangeComprehensionExpr syntax) => Trace is null ? BindComprehension(syntax)
        : Trace.MeasureDetailed("compiler.bind.range_comprehension", (self: this, syntax),
            static state => state.self.BindComprehension(state.syntax),
            attributes: HirTelemetry.Source(hir.Sources, Range(syntax.Span)));

    private HirNodeId BindComprehension(RangeComprehensionExpr syntax)
    {
        var source = Expression(syntax.Source);
        if (syntax.Filter is { } filter)
            source = CallNative("ranges.filter", program.Node(source).Type, filter.Span, [source, Callback(filter)]);
        return CallNative("ranges.transform", OpenType("comprehension"), syntax.Span, [source, Callback(syntax.Projection)]);

        HirNodeId Callback(Expr body) => Function(new(null, [new(syntax.Binding.Name, null, syntax.Nullable, syntax.Binding.Span)],
            null, new([new ExpressionStatement(body, false, body.Span)], body.Span), body.Span));
    }

    private HirNodeId WithSugarValue(Expr expression, SourceSpan span, Func<NameExpr, HirNodeId> body)
    {
        HirNodeId input = Expression(expression);
        var outer = scope.Values;
        string name = $"<sugar:{hir.Locations.Count}>";
        var id = Declare(name, expression.Span, DeclarationKind.Local, program.Node(input).Type, nullable: true);
        var saved = Node(HirKind.Declare, hir.Type(TypeRef.Null), expression.Span, [input], id.Value);
        try
        {
            var output = body(new(name, expression.Span));
            return Node(HirKind.Block, program.Node(output).Type, span, [saved, output]);
        }
        finally { scope.Values = outer; }
    }
}
