using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private HirNodeId Conditional(IfExpr conditional)
    {
        var condition = Expression(conditional.Condition);
        var yes = Block(conditional.Then);
        var no = conditional.Else is null ? Null(conditional.Span) : Expression(conditional.Else);
        return Node(HirKind.If, OpenType("if"), conditional.Span, [condition, yes, no]);
    }

    private HirNodeId For(ForExpr loop)
    {
        var values = Expression(loop.Source);
        var outer = scope;
        scope = new(outer, Range(loop.Body.Span), outer.Template);
        HirLoopId id = new(program.NextLoop++);
        loops.Push(id);
        try
        {
            var itemType = OpenType("iteration");
            var item = Declare("<iteration>", loop.Pattern.Span, DeclarationKind.Loop, itemType);
            var current = Node(HirKind.ReadLocation, itemType, loop.Pattern.Span, data: item.Value,
                effects: HirEffects.ReadLocation);
            var pattern = BindPattern(loop.Pattern, current, false, itemType, null, DeclarationKind.Loop);
            var body = Block(loop.Body);
            var block = Node(HirKind.Block, program.Node(body).Type, loop.Body.Span, [pattern, body]);
            int descriptor = hir.Fors.Count;
            hir.Fors.Add(new(id, item));
            return Node(HirKind.For, hir.Type(TypeRef.Null), loop.Span, [values, block], descriptor,
                effects: HirEffects.Enumerate);
        }
        finally { loops.Pop(); scope = outer; }
    }

    private HirNodeId Loop(LoopExpr loop)
    {
        HirLoopId id = new(program.NextLoop++);
        loops.Push(id);
        try { return Node(HirKind.Loop, OpenType("break"), loop.Span, [Block(loop.Body)], id.Value); }
        finally { loops.Pop(); }
    }

    private HirNodeId Break(BreakExpr broken)
    {
        if (!loops.TryPeek(out var loop)) throw Error("break requires an enclosing loop.", broken.Span, "E0207");
        if (broken.Value is null)
            return Node(HirKind.Break, hir.Type(new TypeRef(TypeKind.Never)), broken.Span, data: loop.Value);
        return Node(HirKind.Break, hir.Type(new TypeRef(TypeKind.Never)), broken.Span,
            [Expression(broken.Value)], loop.Value);
    }

    private HirNodeId Barrier(BarrierExpr barrier)
    {
        int descriptor = hir.Barriers.Count;
        hir.Barriers.Add(new(barrier.Release, barrier.Acquire));
        if (barrier.Value is null)
            return Node(HirKind.Barrier, hir.Type(TypeRef.Null), barrier.Span, data: descriptor,
                effects: HirEffects.Synchronize);
        var value = Expression(barrier.Value);
        return Node(HirKind.Barrier, program.Node(value).Type, barrier.Span, [value], descriptor,
            effects: HirEffects.Synchronize);
    }

    private HirNodeId Match(MatchExpr match)
    {
        var value = Expression(match.Value);
        List<HirMatchArm> arms = [];
        foreach (var arm in match.Arms)
        {
            var outer = scope;
            scope = new(outer, Range(arm.Span), outer.Template);
            try
            {
                var pattern = Pattern(arm.Pattern, program.Node(value).Type);
                arms.Add(new(pattern, Expression(arm.Value)));
            }
            finally { scope = outer; }
        }
        int descriptor = hir.Matches.Count;
        hir.Matches.Add(new(arms.ToImmutableArray()));
        return Node(HirKind.Match, OpenType("match"), match.Span, [value], descriptor);
    }

    private HirPattern Pattern(Pattern pattern, HirTypeId input) => pattern switch
    {
        WildcardPattern or NamePattern { Name: "_" } => new(HirPatternKind.Any),
        NamePattern name => new(HirPatternKind.Any, Binding: Declare(name.Name, name.Span,
            DeclarationKind.Pattern, input, hir.Types[input.Value].RuntimeType.Nullable)),
        NullPattern => new(HirPatternKind.Null),
        TypePattern type => new(HirPatternKind.Type, Type(type.Type).Value),
        RegexPattern regex => new(HirPatternKind.Regex, Text: regex.Pattern),
        LiteralPattern literal => new(HirPatternKind.Literal, LiteralPattern(literal.Value)),
        _ => throw Error("A tuple is a binding pattern, not a match alternative.", pattern.Span)
    };

    private int LiteralPattern(Expr expression)
    {
        if (expression is UnaryExpr { Operator: "+" or "-", Operand: NumberExpr number } unary)
        {
            int index = hir.Literals.Count;
            hir.Literals.Add(new(HirLiteralKind.Number, Number: unary.Operator == "-" ? -number.Value : number.Value));
            return index;
        }
        var node = program.Node(Expression(expression));
        if (node.Kind != HirKind.Literal) throw Error("A match literal must be a constant.", expression.Span);
        return node.Data;
    }
}
