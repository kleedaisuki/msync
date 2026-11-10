using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private HirNodeId Expression(Expr expression) => expression switch
    {
        MissingExpr => Node(HirKind.Error, hir.Type(TypeRef.Any), expression.Span),
        NullExpr => Null(expression.Span),
        BoolExpr boolean => hir.Literal(new(HirLiteralKind.Bool, Boolean: boolean.Value),
            hir.Type(TypeRef.Bool), Range(boolean.Span)),
        NumberExpr number => hir.Literal(new(HirLiteralKind.Number, Number: number.Value),
            hir.Type(TypeRef.Number), Range(number.Span)),
        StringExpr text => hir.Literal(new(HirLiteralKind.String, Text: text.Value),
            hir.Type(TypeRef.String), Range(text.Span)),
        PathExpr path => Path(path.Path, path.Span),
        NameExpr name => Name(name.Name, name.Span),
        FunctionExpr function => Function(function),
        ScopeExpr shape => Scope(shape),
        RangeExpr range => RangeValue(range),
        BlockExpr block => Block(block),
        UnaryExpr unary => Unary(unary),
        BinaryExpr binary => Binary(binary),
        AssignmentExpr assignment => Assignment(assignment),
        CallExpr call => Call(call),
        CastExpr cast => Cast(cast),
        TypeExpr type => Node(HirKind.TypeValue, hir.Type(TypeRef.Type), type.Span, data: Type(type.Type).Value),
        IfExpr conditional => Conditional(conditional),
        ForExpr loop => For(loop),
        LoopExpr loop => Loop(loop),
        MatchExpr match => Match(match),
        ReturnExpr returned => Node(HirKind.Return, hir.Type(new TypeRef(TypeKind.Never)), returned.Span,
            [returned.Value is null ? Null(returned.Span) : Expression(returned.Value)]),
        BreakExpr broken => Break(broken),
        BarrierExpr barrier => Barrier(barrier),
        _ => throw Error("Unsupported expression.", expression.Span)
    };

    private HirNodeId Path(string text, SourceSpan span)
    {
        if (text.StartsWith("ssh://", StringComparison.Ordinal))
            return hir.Literal(new(HirLiteralKind.Connection, Text: text[6..], ConnectionKind: ConnectionKind.Ssh),
                hir.Type(TypeRef.Connection), Range(span));
        if (text.StartsWith("wsl://", StringComparison.Ordinal))
            return hir.Literal(new(HirLiteralKind.Connection, Text: text[6..], ConnectionKind: ConnectionKind.Wsl),
                hir.Type(TypeRef.Connection), Range(span));
        return hir.Literal(new(HirLiteralKind.Path, Text: text), hir.Type(TypeRef.Path), Range(span));
    }

    private HirNodeId Name(string name, SourceSpan span, bool reference = false)
    {
        if (scope.Values.TryGetValue(name, out var existing))
        {
            var location = Capture(existing, name);
            Observe(span, location);
            return Node(HirKind.ReadLocation, hir.Locations[location.Value].StorageType,
                span, data: location.Value, effects: HirEffects.ReadLocation);
        }
        if (reference && name is "view" or "views" or "ranges")
            throw Error($"The predefined range alias '${name}' has been removed. Use '$std::ranges'.", span, "E0210");
        if (reference && name == "std") return StandardReference("", span);
        if (reference && name is "local" or "wsl" or "pwd" or "file" or "env" or "path")
            return Node(HirKind.Builtin, hir.Type(TypeRef.Connection), span, text: name);
        return Path(name, span);
    }

    private HirNodeId Unary(UnaryExpr unary)
    {
        if (unary.Operator == "$") return unary.Operand is NameExpr name
            ? Name(name.Name, unary.Span, true) : Expression(unary.Operand);
        var operand = Expression(unary.Operand);
        return unary.Operator switch
        {
            "?" => Node(HirKind.IsNull, hir.Type(TypeRef.Bool), unary.Span, [operand]),
            "not" => Node(HirKind.Unary, hir.Type(TypeRef.Bool), unary.Span, [operand], (int)HirUnaryOperator.Not),
            "+" => operand,
            "-" => Node(HirKind.Unary, hir.Type(TypeRef.Number), unary.Span, [operand], (int)HirUnaryOperator.Negate),
            "exists" => CallNative("fs.exists", hir.Type(TypeRef.Bool), unary.Span, [operand]),
            _ => throw Error($"Unknown unary operator '{unary.Operator}'.", unary.Span)
        };
    }

    private HirNodeId Binary(BinaryExpr binary)
    {
        if (binary.Operator == "::")
        {
            if (binary.Left is UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } root }
                && !scope.Values.ContainsKey("std"))
            {
                string? module = binary.Right switch
                {
                    NameExpr name => name.Name,
                    StringExpr text => text.Value,
                    _ => null
                };
                if (module is not null)
                {
                    var reference = StandardReference(module, binary.Span);
                    if (observations)
                    {
                        int start = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
                        program.StandardUses.Add(new(reference, Range(new(start, binary.Right.Span.End - start)),
                            Range(binary.Right.Span), module, Range(root.Span)));
                    }
                    return reference;
                }
            }
            var receiver = Expression(binary.Left);
            string? member = binary.Right switch
            {
                NameExpr name => name.Name,
                PathExpr path => path.Path,
                StringExpr text => text.Value,
                MissingExpr => null,
                _ => throw Error("A lexical member must be selected by name.", binary.Right.Span, "E0205")
            };
            var selected = Node(HirKind.MemberRead, OpenType(member ?? "member"), binary.Span,
                [receiver], text: member, effects: HirEffects.ReadMemory);
            if (observations)
            {
                int start = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
                program.MemberUses.Add(new(selected, Range(new(start, binary.Right.Span.End - start)),
                    Range(binary.Right.Span), member, Range(binary.Left.Span)));
            }
            return selected;
        }
        if (binary.Operator == "|") return Pipe(binary);
        var left = Expression(binary.Left);
        var right = Expression(binary.Right);
        if (binary.Operator == "@") return Node(HirKind.Qualify, hir.Type(TypeRef.Path), binary.Span, [left, right]);
        if (binary.Operator == "->") return Node(HirKind.Transfer, hir.Type(TypeRef.Bool), binary.Span,
            [left, right], effects: HirEffects.ReadExternal | HirEffects.WriteExternal | HirEffects.MayFail);
        if (binary.Operator is "is" or "is not")
        {
            var test = program.RuntimeType(right) == TypeRef.Null
                ? Node(HirKind.IsNull, hir.Type(TypeRef.Bool), binary.Span, [left])
                : Node(HirKind.IsType, hir.Type(TypeRef.Bool), binary.Span, [left, right]);
            return binary.Operator == "is not"
                ? Node(HirKind.Unary, hir.Type(TypeRef.Bool), binary.Span, [test], (int)HirUnaryOperator.Not) : test;
        }
        if (binary.Operator == "/" && binary.Right is PathExpr { Path: var selection }
            && selection.StartsWith('<') && selection.EndsWith('>'))
            return Node(HirKind.SelectPath, hir.Type(new TypeRef(TypeKind.Range, TypeRef.Path)), binary.Span,
                [left], text: selection[1..^1], effects: HirEffects.ReadExternal | HirEffects.Enumerate | HirEffects.MayFail);
        var operation = binary.Operator switch
        {
            "+" => HirBinaryOperator.Add,
            "-" => HirBinaryOperator.Subtract,
            "*" => HirBinaryOperator.Multiply,
            "/" => HirBinaryOperator.Divide,
            "%" => HirBinaryOperator.Remainder,
            "**" => HirBinaryOperator.Power,
            "==" => HirBinaryOperator.Equal,
            "!=" => HirBinaryOperator.NotEqual,
            "<" => HirBinaryOperator.Less,
            "<=" => HirBinaryOperator.LessEqual,
            ">" => HirBinaryOperator.Greater,
            ">=" => HirBinaryOperator.GreaterEqual,
            "and" => HirBinaryOperator.And,
            "or" => HirBinaryOperator.Or,
            _ => throw Error($"Unknown operator '{binary.Operator}'.", binary.Span)
        };
        bool boolean = operation >= HirBinaryOperator.Equal;
        var type = boolean ? hir.Type(TypeRef.Bool) : OpenType("binary");
        var result = Node(HirKind.Binary, type, binary.Span, [left, right], (int)operation);
        if (observations && binary.Operator == "/")
        {
            int start = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
            program.PathUses.Add(new(result, Range(new(start, binary.Right.Span.End - start)), Range(binary.Left.Span)));
        }
        return result;
    }

    private HirNodeId Assignment(AssignmentExpr assignment)
    {
        if (assignment.Target is NameExpr name)
        {
            if (!scope.Values.TryGetValue(name.Name, out var existing))
                throw Error($"Cannot assign to undeclared binding '{name.Name}'.", name.Span, "E0204");
            var location = Capture(existing, name.Name);
            program.WrittenLocations.Add(program.Original(location));
            var value = Expression(assignment.Value);
            Observe(name.Span, location);
            return Node(HirKind.Assign, hir.Type(TypeRef.Null), assignment.Span, [value], location.Value,
                effects: HirEffects.WriteLocation);
        }
        if (assignment.Target is BinaryExpr { Operator: "::" } member)
        {
            var receiver = Expression(member.Left);
            string? selected = member.Right switch
            {
                NameExpr item => item.Name,
                PathExpr item => item.Path,
                StringExpr item => item.Value,
                _ => null
            };
            if (selected is null) throw Error("A lexical member assignment requires a name.", member.Right.Span, "E0205");
            var value = Expression(assignment.Value);
            return Node(HirKind.MemberAssign, hir.Type(TypeRef.Null), assignment.Span,
                [receiver, value], text: selected, effects: HirEffects.WriteMemory);
        }
        throw Error("Assign to a binding or Scope field.", assignment.Target.Span, "E0204");
    }

    private HirNodeId Cast(CastExpr cast)
    {
        var value = Expression(cast.Value);
        var type = Type(cast.Type);
        program.TypeAnnotations[value] = type;
        return Node(HirKind.Convert, type, cast.Span, [value], type.Value, effects: HirEffects.MayFail);
    }

    private HirNodeId Call(CallExpr call)
    {
        if (call.Callee is NameExpr { Name: "decltype" })
        {
            if (call.Arguments.Count != 1) throw Error("decltype expects one unevaluated expression.", call.Span);
            return Node(HirKind.TypeOf, hir.Type(TypeRef.Type), call.Span, [Expression(call.Arguments[0])]);
        }
        var callee = Expression(call.Callee);
        List<HirNodeId> children = [callee];
        children.AddRange(call.Arguments.Select(Expression));
        int descriptor = hir.Calls.Count;
        hir.Calls.Add(new(HirCallKind.Dynamic, new(-1)));
        return Node(HirKind.Call, OpenType("call"), call.Span,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(children), descriptor, effects: HirEffects.Invoke);
    }

    private HirNodeId CallNative(string name, HirTypeId result, SourceSpan span, ReadOnlySpan<HirNodeId> arguments)
    {
        var function = Node(HirKind.NativeFunction, hir.Type(TypeRef.Function), span, text: name);
        var children = new HirNodeId[arguments.Length + 1];
        children[0] = function;
        arguments.CopyTo(children.AsSpan(1));
        int descriptor = hir.Calls.Count;
        hir.Calls.Add(new(HirCallKind.Native, new(-1), name));
        return Node(HirKind.Call, result, span, children, descriptor);
    }

    private HirNodeId Pipe(BinaryExpr pipe)
    {
        var input = Expression(pipe.Left);
        var outer = scope.Values;
        var temporary = Declare("<pipe>", pipe.Left.Span, DeclarationKind.Local, program.Node(input).Type);
        var declared = Node(HirKind.Declare, hir.Type(TypeRef.Null), pipe.Left.Span, [input], temporary.Value);
        var callee = pipe.Right is CallExpr invocation ? Expression(invocation.Callee) : Expression(pipe.Right);
        List<HirNodeId> children = [callee, Node(HirKind.ReadLocation, program.Node(input).Type, pipe.Left.Span,
            data: temporary.Value, effects: HirEffects.ReadLocation)];
        if (pipe.Right is CallExpr call) children.AddRange(call.Arguments.Select(Expression));
        int descriptor = hir.Calls.Count;
        hir.Calls.Add(new(HirCallKind.Dynamic, new(-1)));
        var called = Node(HirKind.Call, OpenType("pipe"), pipe.Span,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(children), descriptor, effects: HirEffects.Invoke);
        scope.Values = outer;
        return Node(HirKind.Block, program.Node(called).Type, pipe.Span, [declared, called]);
    }

    private HirNodeId RangeValue(RangeExpr range)
    {
        var children = new HirNodeId[range.Items.Count];
        var deferred = ImmutableArray.CreateBuilder<bool>(range.Items.Count);
        for (int index = 0; index < range.Items.Count; index++)
        {
            bool delayed = !PureValue(range.Items[index]);
            deferred.Add(delayed);
            var outerLoops = delayed && loops.Count != 0 ? loops.ToArray() : null;
            if (delayed) loops.Clear();
            try { children[index] = Expression(range.Items[index]); }
            finally
            {
                if (outerLoops is not null)
                    for (int outer = outerLoops.Length - 1; outer >= 0; outer--) loops.Push(outerLoops[outer]);
            }
        }
        var item = children.Length == 0 ? hir.Type(new TypeRef(TypeKind.Never)) : OpenType("element");
        var type = hir.Type(new HirType(new(TypeKind.Range, hir.Types[item.Value].RuntimeType), Element: item.Value));
        int descriptor = hir.Ranges.Count;
        var delayedItems = deferred.MoveToImmutable();
        hir.Ranges.Add(new(delayedItems));
        return Node(delayedItems.Any(value => value) ? HirKind.LazyRange : HirKind.Range,
            type, range.Span, children, descriptor);
    }

    private static bool PureValue(Expr expression) => expression switch
    {
        BoolExpr or NullExpr or StringExpr or NumberExpr or PathExpr or NameExpr or TypeExpr => true,
        BinaryExpr { Operator: "/" or "::" } binary => PureValue(binary.Left) && PureValue(binary.Right),
        UnaryExpr { Operator: "$" } unary => PureValue(unary.Operand),
        _ => false
    };
}
