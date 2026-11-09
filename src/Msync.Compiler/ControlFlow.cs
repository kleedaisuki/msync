using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private const string LoopLabelPrefix = "@msync-loop:";
    private int nextLoopLabel;

    private LoopContext EnterLoop(Frame frame, bool isFor, int? iterator = null)
    {
        LoopContext loop = new(LoopLabelPrefix + ++nextLoopLabel, isFor, frame.AcquireRegions.Count, frame.Iterators.Count);
        frame.Loops.Add(loop);
        if (iterator is { } slot) frame.Iterators.Add(slot);
        return loop;
    }

    private static void CloseIterators(Frame frame, int depth, SourceSpan span)
    {
        for (int index = frame.Iterators.Count - 1; index >= depth; index--)
        {
            frame.Emit(OpCode.LoadLocal, frame.Iterators[index], span: span);
            frame.Emit(OpCode.NativeCall, 1, text: "_core.iterator_close", span: span);
            frame.Emit(OpCode.Pop);
        }
    }

    private static void LeaveLoop(Frame frame, LoopContext loop)
    {
        int end = frame.Code.Count;
        for (int index = 0; index < end; index++)
            if (frame.Code[index] is { Op: OpCode.Jump } exit && exit.Text == loop.Label)
                frame.Code[index] = exit with { A = end, Text = null };
        frame.Loops.RemoveAt(frame.Loops.Count - 1);
        frame.Iterators.RemoveRange(loop.IteratorDepth, frame.Iterators.Count - loop.IteratorDepth);
    }

    private static void MergeControlExits(Frame frame, Frame scratch)
    {
        frame.Returns.AddRange(scratch.Returns);
        for (int index = 0; index < scratch.Loops.Count; index++)
        {
            frame.Loops[index].Values.AddRange(scratch.Loops[index].Values);
            frame.Loops[index].ExitFacts.AddRange(scratch.Loops[index].ExitFacts);
        }
    }

    private Info Block(Frame frame, BlockExpr block)
    {
        var old = frame.Environment;
        var concepts = frame.Concepts;
        var owner = frame.ContextOwner;
        bool includesEnd = frame.ContextIncludesEnd;
        frame.ContextOwner = block.Span;
        frame.ContextIncludesEnd = block.EditorIncomplete;
        CaptureContext(frame, block.Span.Start);
        try
        {
            var result = Statements(frame, block.Statements);
            CaptureContext(frame, block.Span.End);
            return result;
        }
        finally { RestoreScope(frame, old); frame.Concepts = concepts; frame.ContextOwner = owner; frame.ContextIncludesEnd = includesEnd; }
    }
    private Info Conditional(Frame frame, IfExpr conditional)
    {
        var test = Expression(frame, conditional.Condition); Require(frame, test.Type, TypeRef.Bool, conditional.Condition.Span);
        if (test.KnownBool is not null)
        {
            frame.Emit(OpCode.Pop);
            var before = frame.Environment; frame.Environment = Refined(frame, conditional.Condition, test.KnownBool.Value);
            var selected = test.KnownBool.Value ? Block(frame, conditional.Then)
                : conditional.Else is null ? Emit(frame, OpCode.Null, TypeRef.Null, conditional.Span) : Expression(frame, conditional.Else);
            RestoreScope(frame, before);
            if (frame.EditorRecovery)
            {
                Frame probe = CloneFrame(frame);
                probe.EditorProbe = true;
                probe.EditorReportErrors = false;
                probe.Environment = Refined(probe, conditional.Condition, !test.KnownBool.Value);
                if (!test.KnownBool.Value) _ = Block(probe, conditional.Then);
                else if (conditional.Else is not null) _ = Expression(probe, conditional.Else);
            }
            return selected;
        }
        int otherwise = frame.Emit(OpCode.JumpIfFalse);
        var old = frame.Environment;
        var incoming = SnapshotFacts(frame);
        frame.Environment = Refined(frame, conditional.Condition, true);
        var yes = Block(frame, conditional.Then);
        var yesFacts = frame.Facts;
        int end = frame.Emit(OpCode.Jump);
        frame.Patch(otherwise, frame.Code.Count);
        frame.Environment = old;
        frame.Facts = incoming;
        frame.Environment = Refined(frame, conditional.Condition, false);
        var no = conditional.Else is null ? Emit(frame, OpCode.Null, TypeRef.Null, conditional.Span) : Expression(frame, conditional.Else);
        frame.Facts = yes.Type.Kind == TypeKind.Never ? frame.Facts : no.Type.Kind == TypeKind.Never ? yesFacts : JoinFacts(yesFacts, frame.Facts);
        RestoreScope(frame, old);
        if (yes.Type.Kind == TypeKind.Never && no.Type.Kind != TypeKind.Never) frame.Environment = Refined(frame, conditional.Condition, false);
        if (no.Type.Kind == TypeKind.Never && yes.Type.Kind != TypeKind.Never) frame.Environment = Refined(frame, conditional.Condition, true);
        frame.Patch(end, frame.Code.Count);
        return Join(yes, no);
    }
    private ImmutableDictionary<string, Binding> Refined(Frame frame, Expr expression, bool positive)
    {
        var result = frame.Environment;
        if (expression is UnaryExpr { Operator: "not" } not) return Refined(frame, not.Operand, !positive);
        if (expression is BinaryExpr logical && ((logical.Operator == "and" && positive) || (logical.Operator == "or" && !positive)))
        {
            var old = frame.Environment; frame.Environment = Refined(frame, logical.Left, positive);
            var both = Refined(frame, logical.Right, positive); frame.Environment = old; return both;
        }
        Expr? target = null; TypeRef? type = null; bool denoted = false;
        if (expression is UnaryExpr { Operator: "?" } nullable) { target = nullable.Operand; type = TypeRef.Null; }
        if (expression is BinaryExpr { Operator: "is" or "is not" } binary)
        {
            target = binary.Left;
            if (binary.Right is NullExpr) type = TypeRef.Null;
            if (binary.Right is TypeExpr syntax) { type = ResolveType(frame, syntax.Type); denoted = true; }
            if (binary.Operator == "is not") positive = !positive;
        }
        if (target is not NameExpr name || !result.TryGetValue(name.Name, out var binding))
            return target is not null && type is not null ? RefineMember(frame, target, type, positive, denoted) : result;
        if (type is null) return result;
        var info = RefineInfo(binding.Info, type, positive, denoted);
        frame.Facts = frame.Facts.SetItem(binding.Identity, info);
        result = result.SetItem(name.Name, binding with { Info = info });
        return result;
    }

    private Info For(Frame frame, ForExpr loop)
    {
        var sourceInfo = Expression(frame, loop.Source); Require(frame, sourceInfo.Type, new(TypeKind.Range), loop.Source.Span);
        frame.Emit(OpCode.GetIterator);
        int iterator = frame.Allocate(); frame.Emit(OpCode.StoreLocal, iterator);
        int begin = frame.Code.Count;
        int next = frame.Emit(OpCode.IteratorNext, iterator);
        var old = frame.Environment;
        var incoming = SnapshotFacts(frame);
        PrepareLoop(frame, probe =>
        {
            EnterLoop(probe, true, iterator);
            var before = probe.Facts;
            BindPattern(probe, loop.Pattern, sourceInfo.Element ?? new(sourceInfo.Type.Element ?? TypeRef.Any));
            if (Block(probe, loop.Body).Type.Kind == TypeKind.Never) probe.Facts = before;
        });
        var context = EnterLoop(frame, true, iterator);
        BindPattern(frame, loop.Pattern, sourceInfo.Element ?? new(sourceInfo.Type.Element ?? TypeRef.Any));
        var body = Block(frame, loop.Body); frame.Emit(OpCode.Pop);
        frame.Facts = body.Type.Kind == TypeKind.Never ? incoming : JoinFacts(incoming, frame.Facts);
        foreach (var facts in context.ExitFacts) frame.Facts = JoinFacts(frame.Facts, facts);
        RestoreScope(frame, old);
        frame.Emit(OpCode.Jump, begin);
        frame.Code[next] = frame.Code[next] with { B = frame.Code.Count };
        LeaveLoop(frame, context);
        frame.Emit(OpCode.Null);
        return new(TypeRef.Null);
    }
    private Info Loop(Frame frame, LoopExpr loop)
    {
        var incoming = SnapshotFacts(frame);
        PrepareLoop(frame, probe =>
        {
            EnterLoop(probe, false);
            var before = probe.Facts;
            if (Block(probe, loop.Body).Type.Kind == TypeKind.Never) probe.Facts = before;
        });
        int start = frame.Code.Count;
        var context = EnterLoop(frame, false);
        Block(frame, loop.Body); frame.Emit(OpCode.Pop); frame.Emit(OpCode.Jump, start);
        LeaveLoop(frame, context);
        frame.Facts = context.ExitFacts.Count == 0 ? incoming : context.ExitFacts.Aggregate(JoinFacts);
        Refresh(frame);
        return context.Values.Count == 0 ? new(new(TypeKind.Never)) : context.Values.Aggregate(Join);
    }
    private Info Return(Frame frame, ReturnExpr returned)
    {
        var result = returned.Value is null ? Emit(frame, OpCode.Null, TypeRef.Null, returned.Span) : Expression(frame, returned.Value);
        if (frame.ExpectedConceptReturn is { } conceptReturn) CheckReturnConcept(frame, result, conceptReturn, returned.Span);
        if (frame.ExpectedReturn is not null)
        {
            CheckReturnConversion(frame, result.Type, frame.ExpectedReturn, returned.Span);
            Convert(frame, result.Type, frame.ExpectedReturn, returned.Span);
            if (!frame.ExpectedReturn.Accepts(result.Type)) result = Retype(result, frame.ExpectedReturn);
        }
        CloseIterators(frame, 0, returned.Span);
        CloseAcquireRegions(frame, 0, returned.Span);
        frame.Returns.Add(result); frame.Emit(OpCode.Return, span: returned.Span); frame.Emit(OpCode.Null);
        return new(new(TypeKind.Never));
    }
    private Info Break(Frame frame, BreakExpr broken)
    {
        if (frame.Loops.Count == 0) throw Error(frame, "break requires an enclosing loop.", broken.Span,
            label: "no loop to break out of", help: "Place break inside 'for ... { ... }' or 'loop { ... }'. Use 'return value' to finish a function.", code: "E0207");
        var loop = frame.Loops[^1];
        if (loop.IsFor && broken.Value is not null)
            throw Error(frame, "A for expression returns Null and cannot return a break value.", broken.Span,
                help: "Use bare 'break;' to exit this for, or use 'loop { ... break value; }' for a value-producing loop.", code: "E0207");
        var result = loop.IsFor ? new Info(TypeRef.Null) : broken.Value is null
            ? Emit(frame, OpCode.Null, TypeRef.Null, broken.Span) : Expression(frame, broken.Value);
        loop.Values.Add(result);
        loop.ExitFacts.Add(SnapshotFacts(frame));
        CloseIterators(frame, loop.IteratorDepth, broken.Span);
        CloseAcquireRegions(frame, loop.AcquireDepth, broken.Span);
        frame.Emit(OpCode.Jump, text: loop.Label); frame.Emit(OpCode.Null);
        return new(new(TypeKind.Never));
    }

    private Info Match(Frame frame, MatchExpr match)
    {
        var value = Expression(frame, match.Value);
        int slot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, slot);
        CheckExhaustive(frame, value, match.Arms, match.Span);
        var selected = SelectKnownArm(frame, value, match.Arms);
        if (selected is not null)
        {
            var before = frame.Environment;
            RefineMatchBinding(frame, match.Value, selected.Pattern, value, slot, value.Type);
            var chosen = Expression(frame, selected.Value);
            RestoreScope(frame, before); return chosen;
        }
        Info? result = null; List<int> ends = [];
        var old = frame.Environment;
        var incoming = SnapshotFacts(frame);
        ImmutableDictionary<object, Info>? outgoing = null;
        TypeRef remaining = value.Type;
        foreach (var arm in match.Arms)
        {
            if (KnownPattern(frame, value, arm.Pattern) == false) continue;
            frame.Environment = old;
            frame.Facts = incoming;
            frame.Emit(OpCode.LoadLocal, slot);
            EmitPattern(frame, arm.Pattern, value, arm.Pattern.Span);
            int skip = frame.Emit(OpCode.JumpIfFalse);
            var refined = PatternType(frame, arm.Pattern, remaining);
            if (arm.Pattern is NullPattern or TypePattern)
            {
                var covered = arm.Pattern is TypePattern ? refined.Kind & ~TypeKind.Type : refined.Kind;
                remaining = new(remaining.Kind & ~covered, remaining.Element);
            }
            if (match.Value is NameExpr name && frame.Environment.TryGetValue(name.Name, out var bound))
                frame.Bind(name.Name, bound with
                {
                    Info = arm.Pattern is TypePattern type
                    ? RefineInfo(bound.Info, ResolveType(frame, type.Type), true, true) : bound.Info with { Type = refined }
                });
            else frame.Environment = arm.Pattern is TypePattern type
                ? RefineMember(frame, match.Value, ResolveType(frame, type.Type), true, true) : RefineMember(frame, match.Value, refined, true);
            if (arm.Pattern is NamePattern { Name: not "_" } capture) frame.Bind(capture.Name, new(slot, value with { Type = refined }));
            var branch = Expression(frame, arm.Value);
            if (branch.Type.Kind != TypeKind.Never) outgoing = outgoing is null ? frame.Facts : JoinFacts(outgoing, frame.Facts);
            result = result is null ? branch : Join(result, branch);
            ends.Add(frame.Emit(OpCode.Jump));
            frame.Patch(skip, frame.Code.Count);
        }
        frame.Facts = outgoing ?? incoming;
        RestoreScope(frame, old);
        frame.Emit(OpCode.Null);
        foreach (int end in ends) frame.Patch(end, frame.Code.Count);
        return result ?? new(TypeRef.Null);
    }
    private MatchArm? SelectKnownArm(Frame frame, Info value, IReadOnlyList<MatchArm> arms)
    {
        foreach (var arm in arms)
        {
            bool? matched = KnownPattern(frame, value, arm.Pattern);
            if (matched is null) return null;
            if (matched.Value) return arm;
        }
        return null;
    }
    private bool? KnownPattern(Frame frame, Info value, Pattern pattern) => pattern switch
    {
        WildcardPattern or NamePattern => true,
        NullPattern => value.Type == TypeRef.Null ? true : !value.Type.Nullable ? false : null,
        TypePattern type => KnownTypeMatch(value, ResolveType(frame, type.Type)),
        LiteralPattern { Value: BoolExpr boolean } when value.KnownBool is not null => value.KnownBool.Value == boolean.Value,
        RegexPattern when (value.Type.Kind & (TypeKind.Path | TypeKind.String)) == 0 => false,
        _ => null
    };
    private static bool? KnownTypeMatch(Info value, TypeRef expected)
    {
        if (value.TypeValue is not null) return expected.Accepts(value.TypeValue);
        if ((value.Type.Kind & TypeKind.Type) != 0) return null;
        if (expected.Accepts(value.Type)) return true;
        return (expected.Kind & value.Type.Kind) == 0 ? false : null;
    }
    private void RefineMatchBinding(Frame frame, Expr expression, Pattern pattern, Info value, int slot, TypeRef remaining)
    {
        var refined = PatternType(frame, pattern, remaining);
        if (expression is NameExpr name && frame.Environment.TryGetValue(name.Name, out var binding))
            frame.Bind(name.Name, binding with
            {
                Info = pattern is TypePattern type
                ? RefineInfo(binding.Info, ResolveType(frame, type.Type), true, true) : binding.Info with { Type = refined }
            });
        else frame.Environment = pattern is TypePattern type
            ? RefineMember(frame, expression, ResolveType(frame, type.Type), true, true) : RefineMember(frame, expression, refined, true);
        if (pattern is NamePattern { Name: not "_" } capture) frame.Bind(capture.Name, new(slot, value with { Type = refined }));
    }

    private void EmitPattern(Frame frame, Pattern pattern, Info value, SourceSpan span)
    {
        switch (pattern)
        {
            case WildcardPattern or NamePattern: frame.Emit(OpCode.Pop); frame.Emit(OpCode.True); break;
            case NullPattern: frame.Emit(OpCode.IsNull, span: span); break;
            case LiteralPattern literal: Expression(frame, literal.Value); frame.Emit(OpCode.Equal, span: span); break;
            case TypePattern type:
                TypeConstant(frame, ResolveType(frame, type.Type), span); frame.Emit(OpCode.IsType, span: span); break;
            case RegexPattern regex:
                ValidateRegex(frame, regex.Pattern, span);
                frame.Emit(OpCode.RegexMatch, text: regex.Pattern, span: span); break;
            default: throw Error(frame, "This pattern is not supported in a match arm.", span);
        }
    }
    private static void ValidateRegex(Frame frame, string pattern, SourceSpan span)
    {
        try { _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant); }
        catch (ArgumentException error)
        {
            throw Error(frame, $"Invalid regular expression: {error.Message}", span,
            label: "invalid pattern", help: "Check the pattern's character classes, escapes and grouping. Regex patterns use the .NET regular-expression syntax.", code: "E0208");
        }
    }
    private TypeRef PatternType(Frame frame, Pattern pattern, TypeRef original) => pattern switch
    {
        NullPattern => TypeRef.Null,
        TypePattern type => RefineInfo(new(original), ResolveType(frame, type.Type), true, true).Type,
        LiteralPattern { Value: BoolExpr } => TypeRef.Bool,
        LiteralPattern { Value: StringExpr } or RegexPattern => new(original.Kind & (TypeKind.String | TypeKind.Path), original.Element),
        _ => original
    };
    private void CheckExhaustive(Frame frame, Info value, IReadOnlyList<MatchArm> arms, SourceSpan span)
    {
        TypeKind covered = TypeKind.Never; bool hasTrue = false, hasFalse = false;
        foreach (var arm in arms)
        {
            if (arm.Pattern is WildcardPattern or NamePattern) return;
            if (arm.Pattern is NullPattern) covered |= TypeKind.Null;
            if (arm.Pattern is TypePattern type)
            {
                var matched = ResolveType(frame, type.Type);
                if (value.TypeValue is not null && matched.Accepts(value.TypeValue)) return;
                covered |= matched.Kind & ~TypeKind.Type;
                if (matched == TypeRef.Any) covered |= TypeKind.Type;
            }
            if (arm.Pattern is LiteralPattern { Value: BoolExpr boolean }) { hasTrue |= boolean.Value; hasFalse |= !boolean.Value; }
        }
        if (hasTrue && hasFalse) covered |= TypeKind.Bool;
        if ((value.Type.Kind & ~covered) != 0)
        {
            var missing = new TypeRef(value.Type.Kind & ~covered, value.Type.Element);
            string cases = missing.Kind == TypeKind.Bool ? hasTrue ? "false" : hasFalse ? "true" : "true and false" : missing.ToString();
            throw Error(frame, $"Non-exhaustive match for {value.Type}; add the missing cases or '_'.", span,
                label: $"not covered: {cases}",
                help: $"Add arms covering {cases}, or a final '_ => {{ ... }}' arm. Regex and literal patterns do not cover every Path or String.", code: "E0209");
        }
    }


}
