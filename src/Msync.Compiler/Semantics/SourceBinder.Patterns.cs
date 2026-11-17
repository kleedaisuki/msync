using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed record PatternCoverageTerm(Pattern Syntax, HirTypeId? Type = null,
    PatternCoverageTerm? Left = null, PatternCoverageTerm? Right = null, ImmutableArray<PatternCoverageTerm> Items = default);
internal sealed record PatternCoverageSite(HirLocationId Subject, ImmutableArray<PatternCoverageTerm> Arms, bool HasGuards);

internal sealed partial class SourceBinder
{
    private HirTypeId BoolType => hir.Type(TypeRef.Bool);

    private HirNodeId Boolean(bool value, SourceSpan span) =>
        hir.Literal(new(HirLiteralKind.Bool, Boolean: value), BoolType, Range(span));

    private HirNodeId Branch(HirNodeId condition, HirNodeId yes, HirNodeId no, SourceSpan span)
    {
        var branch = Node(HirKind.If, OpenType("pattern branch"), span, [condition, yes, no]);
        program.PatternBranches.Add(branch);
        return branch;
    }

    private static bool PatternBinds(Pattern pattern) => pattern switch
    {
        NamePattern { Name: not "_" } or CapturePattern => true,
        _ => SyntaxTraversal.Children(pattern).Any(node => node switch
        {
            Pattern child => PatternBinds(child),
            PatternField field => PatternBinds(field.Pattern),
            _ => false
        })
    };

    private Dictionary<string, HirLocationId> DeclarePattern(Pattern pattern)
    {
        var names = Collect(pattern);
        return names.ToDictionary(binding => binding.Key, binding =>
            Declare(binding.Key, binding.Value.Span, DeclarationKind.Pattern, OpenType(binding.Key), nullable: true),
            StringComparer.Ordinal);

        Dictionary<string, NamePattern> Collect(Pattern node)
        {
            if (node is NamePattern name)
                return name.Name == "_" ? [] : new(StringComparer.Ordinal) { [name.Name] = name };
            if (node is NotPattern not)
            {
                if (PatternBinds(not.Inner)) throw Error("'not' patterns do not bind names.", not.Span, "E0209");
                return [];
            }
            if (node is BinaryPattern { Conjunction: false } alternative)
            {
                var left = Collect(alternative.Left);
                var right = Collect(alternative.Right);
                if (left.Count != right.Count || !left.Keys.All(right.ContainsKey))
                    throw Error("Both sides of 'or' must bind the same names.", alternative.Span, "E0209");
                return left;
            }
            var result = new Dictionary<string, NamePattern>(StringComparer.Ordinal);
            foreach (var child in SyntaxTraversal.Children(node))
            {
                var nested = child switch { Pattern p => p, PatternField f => f.Pattern, _ => null };
                if (nested is null) continue;
                foreach (var (key, binding) in Collect(nested))
                    if (!result.TryAdd(key, binding))
                        throw Error($"Pattern binds '{key}' more than once.", binding.Span, "E0209");
            }
            return result;
        }
    }

    private HirNodeId PatternTest(PatternTestExpr test)
    {
        var value = Expression(test.Value);
        if (!NeedsPatternDecision(test.Pattern)) return AtomicTest(test.Pattern, value);
        var ordinary = scope.Values;
        try
        {
            return SavePatternValue(value, test.Span, (_, read) =>
            {
                var bindings = DeclarePattern(test.Pattern);
                return WithPatternCursor([test.Pattern], read, test.Span,
                    () => Decide(test.Pattern, read, bindings, Boolean(true, test.Span), Boolean(false, test.Span)));
            });
        }
        finally { scope.Values = ordinary; }
    }

    private static bool NeedsPatternDecision(Pattern pattern) =>
        pattern is not (TypePattern or NullPattern or DenotationPattern
            or NotPattern { Inner: TypePattern or NullPattern or DenotationPattern });

    private HirNodeId ConditionalPattern(IfExpr conditional, PatternTestExpr test)
    {
        var no = conditional.Else is { } other ? Expression(other) : Null(conditional.Span);
        return BindConditionalGuard(test, () => Block(conditional.Then), no);
    }

    private static bool HasPatternBinding(Expr expression) => expression switch
    {
        PatternTestExpr test => PatternBinds(test.Pattern),
        BinaryExpr { Operator: "and" } and => HasPatternBinding(and.Left) || HasPatternBinding(and.Right),
        _ => false
    };

    private HirNodeId BindConditionalGuard(Expr condition, Func<HirNodeId> accepted, HirNodeId no)
    {
        if (condition is BinaryExpr { Operator: "and" } both && HasPatternBinding(both))
            return BindConditionalGuard(both.Left, () => BindConditionalGuard(both.Right, accepted, no), no);
        if (condition is not PatternTestExpr test || !NeedsPatternDecision(test.Pattern))
            return Branch(Expression(condition), accepted(), no, condition.Span);
        var value = Expression(test.Value);
        var parent = scope;
        scope = new(parent, Range(condition.Span), parent.Template);
        try
        {
            var bindings = DeclarePattern(test.Pattern);
            var yes = accepted();
            return SavePatternValue(value, condition.Span, (_, read) => WithPatternCursor([test.Pattern], read, condition.Span,
                () => Decide(test.Pattern, read, bindings, yes, no)));
        }
        finally { scope = parent; }
    }

    private HirNodeId LetElse(LetStatement let)
    {
        if (let.Type is not null || let.Nullable)
            throw Error("Put type and null constraints inside the let-else pattern.", let.Pattern.Span, "E0209");
        var subject = Expression(let.Value);
        var failure = Block(let.Failure!);
        program.PatternExits.Add(failure);
        var bindings = DeclarePattern(let.Pattern);
        return SavePatternValue(subject, let.Span, (_, read) => WithPatternCursor([let.Pattern], read, let.Span,
            () => Decide(let.Pattern, read, bindings, Null(let.Span), failure)));
    }

    private HirNodeId CompoundMatch(MatchExpr match) => Trace is null ? BindCompoundMatch(match)
        : Trace.MeasureDetailed("compiler.bind.match_decision", (self: this, match),
            static state => state.self.BindCompoundMatch(state.match),
            attributes: HirTelemetry.Source(hir.Sources, Range(match.Span)));

    private HirNodeId BindCompoundMatch(MatchExpr match)
    {
        Trace?.Metric("arms", match.Arms.Count);
        var subject = Expression(match.Value);
        var parents = scope;
        return SavePatternValue(subject, match.Span, (location, read) => MatchWithSharedPrefix(match, read, () => Build(location, read)));

        HirNodeId Build(HirLocationId location, HirNodeId read)
        {
            var covered = ImmutableArray.CreateBuilder<PatternCoverageTerm>();
            HirNodeId remaining = Node(HirKind.Block, hir.Type(new TypeRef(TypeKind.Never)), match.Span, []);
            program.PatternUnreachable.Add(remaining);
            for (int i = match.Arms.Count - 1; i >= 0; i--)
            {
                var arm = match.Arms[i];
                scope = new(parents, Range(arm.Span), parents.Template);
                try
                {
                    var bindings = DeclarePattern(arm.Pattern);
                    var accepted = Expression(arm.Value);
                    if (arm.Guard is { } guard && guard is not BoolExpr { Value: true })
                    {
                        if (bindings.Count == 0 && arm.Pattern is (TypePattern or NullPattern or WildcardPattern
                            or Syntax.LiteralPattern or Syntax.RegexPattern or RelationPattern))
                        {
                            var predicate = Expression(guard);
                            var tested = AtomicTest(arm.Pattern, read);
                            var condition = Branch(tested, predicate, Boolean(false, guard.Span), arm.Span);
                            program.PatternGuardConditions.Add(condition, (tested, predicate));
                            remaining = Branch(condition, accepted, remaining, arm.Span);
                            continue;
                        }
                        accepted = Branch(Expression(guard), accepted, remaining, arm.Span);
                    }
                    else covered.Add(Coverage(arm.Pattern));
                    remaining = Decide(arm.Pattern, read, bindings, accepted, remaining);
                }
                finally { scope = parents; }
            }
            var decision = Node(HirKind.Block, program.Node(remaining).Type, match.Span, [remaining]);
            program.PatternCoverage.Add(decision, new(location, covered.ToImmutable(),
                match.Arms.Any(arm => arm.Guard is not null and not BoolExpr { Value: true })));
            return decision;
        }
    }

    private readonly Dictionary<HirNodeId, HirNodeId> sharedPatternPrefixes = [];
    private readonly Dictionary<HirNodeId, int> sharedPatternCursors = [];

    private HirNodeId MatchWithSharedPrefix(MatchExpr match, HirNodeId subject, Func<HirNodeId> build)
    {
        if (match.Arms.FirstOrDefault()?.Pattern is not TuplePattern first) return SharedCursor();
        int count = TuplePrefixCap(first);
        if (count == 0) return SharedCursor();
        int users = 0;
        foreach (var arm in match.Arms)
        {
            if (arm.Pattern is TuplePattern tuple)
            {
                if (TuplePrefixCap(tuple) != count) return SharedCursor();
                users++;
            }
            else if (!IsMatchLeaf(arm.Pattern)) return SharedCursor();
        }
        if (users < 2) return build();
        Trace?.Metric("pattern.prefix_sharing", users);
        var type = hir.Type(new TypeRef(TypeKind.Range));
        var prefix = PatternPrefix(subject, count, first.Span, type);
        var empty = RangeValue(new RangeExpr([], first.Span));
        var snapshot = Branch(TypeTest(subject, type, first.Span), prefix, empty, first.Span);
        return SavePatternValue(snapshot, first.Span, (_, read) =>
        {
            sharedPatternPrefixes.Add(subject, read);
            try { return build(); }
            finally { sharedPatternPrefixes.Remove(subject); }
        });

        HirNodeId SharedCursor() => WithPatternCursor(match.Arms.Select(arm => arm.Pattern), subject, match.Span, build);
    }

    private static int TuplePrefixCap(TuplePattern tuple) => tuple.Items.Count + (tuple.Prefix ? 0 : 1);

    private HirNodeId WithPatternCursor(IEnumerable<Pattern> patterns, HirNodeId subject, SourceSpan span, Func<HirNodeId> build)
    {
        int count = 0, capacity = 0;
        foreach (var pattern in patterns) Measure(pattern);
        if (count < 2 || capacity == 0 || sharedPatternCursors.ContainsKey(subject)) return build();
        Trace?.Metric("pattern.cursor_sites", count);
        Trace?.Metric("pattern.cursor_capacity", capacity);
        int site = program.Node(subject).Data;
        sharedPatternCursors.Add(subject, site);
        try
        {
            var body = build();
            return Node(HirKind.PatternRegion, program.Node(body).Type, span, [subject, body], site,
                capacity.ToString(System.Globalization.CultureInfo.InvariantCulture), HirEffects.Enumerate | HirEffects.MayFail);
        }
        finally { sharedPatternCursors.Remove(subject); }

        void Measure(Pattern pattern)
        {
            switch (pattern)
            {
                case TuplePattern tuple:
                    int required = TuplePrefixCap(tuple);
                    if (required != 0) count++;
                    capacity = Math.Max(capacity, required);
                    break;
                case CapturePattern capture: Measure(capture.Inner); break;
                case NotPattern not: Measure(not.Inner); break;
                case BinaryPattern binary: Measure(binary.Left); Measure(binary.Right); break;
            }
        }
    }

    private PatternCoverageTerm Coverage(Pattern pattern) => pattern switch
    {
        TypePattern typed => new(pattern, Type(typed.Type)),
        BinaryPattern binary => new(pattern, Left: Coverage(binary.Left), Right: Coverage(binary.Right)),
        CapturePattern captured => new(pattern, Left: Coverage(captured.Inner)),
        NotPattern negated => new(pattern, Left: Coverage(negated.Inner)),
        TuplePattern sequence => new(pattern, Items: sequence.Items.Select(Coverage).ToImmutableArray()),
        ScopePattern record => new(pattern, Items: record.Fields.Select(field => Coverage(field.Pattern)).ToImmutableArray()),
        _ => new(pattern)
    };

    private HirNodeId Decide(Pattern pattern, HirNodeId value, Dictionary<string, HirLocationId> bindings,
        HirNodeId yes, HirNodeId no)
    {
        switch (pattern)
        {
            case WildcardPattern or NamePattern { Name: "_" }: return yes;
            case NamePattern binding: return BindMatched(binding);
            case CapturePattern capture:
                return Decide(capture.Inner, value, bindings, BindMatched(capture.Binding), no);
            case NotPattern not: return Decide(not.Inner, value, bindings, no, yes);
            case BinaryPattern binary:
                var tail = Decide(binary.Right, value, bindings, yes, no);
                return binary.Conjunction
                    ? Decide(binary.Left, value, bindings, tail, no)
                    : Decide(binary.Left, value, bindings, yes, tail);
            case ScopePattern scopePattern:
                return DecideFields(scopePattern, value, bindings, yes, no);
            case TuplePattern sequence:
                return DecideSequence(sequence, value, bindings, yes, no);
            default:
                return Branch(AtomicTest(pattern, value), yes, no, pattern.Span);
        }

        HirNodeId BindMatched(NamePattern name)
        {
            if (name.Name == "_") return yes;
            var declaration = Node(HirKind.Declare, hir.Type(TypeRef.Null), name.Span, [value], bindings[name.Name].Value);
            return Node(HirKind.Block, program.Node(yes).Type, pattern.Span, [declaration, yes]);
        }
    }

    private HirNodeId AtomicTest(Pattern pattern, HirNodeId value) => pattern switch
    {
        WildcardPattern => Boolean(true, pattern.Span),
        NullPattern => Node(HirKind.IsNull, BoolType, pattern.Span, [value]),
        TypePattern typed => TypeTest(value, Type(typed.Type), pattern.Span),
        DenotationPattern denoted => Node(HirKind.IsType, BoolType, pattern.Span, [value, Expression(denoted.TypeValue)]),
        NotPattern not => Node(HirKind.Unary, BoolType, pattern.Span, [AtomicTest(not.Inner, value)], (int)HirUnaryOperator.Not),
        RelationPattern relation => RelationalTest(value, relation),
        Syntax.LiteralPattern literal => EqualityTest(value, literal),
        _ => LeafMatch(value, Pattern(pattern, program.Node(value).Type), pattern.Span)
    };

    private HirNodeId EqualityTest(HirNodeId value, Syntax.LiteralPattern pattern)
    {
        var constant = Expression(pattern.Value);
        var expected = pattern.Value switch
        {
            BoolExpr => TypeRef.Bool,
            StringExpr => new TypeRef(TypeKind.String | TypeKind.Path),
            _ => TypeRef.Number
        };
        var compare = Node(HirKind.Binary, BoolType, pattern.Span, [value, constant], (int)HirBinaryOperator.Equal);
        return Node(HirKind.Binary, BoolType, pattern.Span,
            [TypeTest(value, hir.Type(expected), pattern.Span), compare], (int)HirBinaryOperator.And);
    }

    private HirNodeId TypeTest(HirNodeId value, HirTypeId type, SourceSpan span)
    {
        var expected = Node(HirKind.TypeValue, hir.Type(TypeRef.Type), span, data: type.Value);
        return Node(HirKind.IsType, BoolType, span, [value, expected]);
    }

    private HirNodeId RelationalTest(HirNodeId value, RelationPattern relation)
    {
        var operation = relation.Operation switch
        {
            "<" => HirBinaryOperator.Less, "<=" => HirBinaryOperator.LessEqual,
            ">" => HirBinaryOperator.Greater, _ => HirBinaryOperator.GreaterEqual
        };
        var compare = Node(HirKind.Binary, BoolType, relation.Span, [value, Expression(relation.Limit)], (int)operation);
        return Node(HirKind.Binary, BoolType, relation.Span,
            [TypeTest(value, hir.Type(TypeRef.Number), relation.Span), compare], (int)HirBinaryOperator.And);
    }

    private HirNodeId LeafMatch(HirNodeId value, HirPattern pattern, SourceSpan span)
    {
        int descriptor = hir.Matches.Count;
        hir.Matches.Add(new([new(pattern, Boolean(true, span)), new(new(HirPatternKind.Any), Boolean(false, span))]));
        return Node(HirKind.Match, BoolType, span, [value], descriptor);
    }

    private HirNodeId DecideFields(ScopePattern pattern, HirNodeId value, Dictionary<string, HirLocationId> bindings,
        HirNodeId yes, HirNodeId no)
    {
        for (int i = pattern.Fields.Count - 1; i >= 0; i--)
        {
            var field = pattern.Fields[i];
            var match = yes;
            var member = Node(HirKind.MemberRead, OpenType(field.Name), field.Span, [value], text: field.Name,
                effects: HirEffects.ReadMemory);
            yes = SavePatternValue(member, field.Span, (_, temporary) => WithPatternCursor([field.Pattern], temporary, field.Span,
                () => Decide(field.Pattern, temporary, bindings, match, no)));
        }
        for (int i = pattern.Fields.Count - 1; i >= 0; i--)
        {
            var field = pattern.Fields[i];
            var memberTest = Node(HirKind.IsType, BoolType, field.Span, [value], text: field.Name);
            program.PatternFieldTests.Add(memberTest);
            yes = Branch(memberTest, yes, no, field.Span);
        }
        return Branch(TypeTest(value, hir.Type(TypeRef.Scope), pattern.Span), yes, no, pattern.Span);
    }

    private HirNodeId DecideSequence(TuplePattern pattern, HirNodeId value, Dictionary<string, HirLocationId> bindings,
        HirNodeId yes, HirNodeId no)
    {
        var type = hir.Type(new TypeRef(TypeKind.Range));
        if (pattern.Prefix && pattern.Items.Count == 0)
            return Branch(TypeTest(value, type, pattern.Span), yes, no, pattern.Span);
        if (sharedPatternCursors.TryGetValue(value, out int site))
        {
            int count = pattern.Items.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                var child = pattern.Items[i];
                var element = Node(HirKind.PatternRead, OpenType("pattern item"), child.Span, [], site, Index(i));
                var matched = yes;
                yes = SavePatternValue(element, child.Span, (_, read) => WithPatternCursor([child], read, child.Span,
                    () => Decide(child, read, bindings, matched, no)));
            }
            var demand = Node(HirKind.PatternDemand, BoolType, pattern.Span, [], site, Index(TuplePrefixCap(pattern)),
                HirEffects.Enumerate | HirEffects.MayFail);
            if (pattern.Prefix) yes = Branch(demand, yes, no, pattern.Span);
            else
            {
                yes = Branch(demand, no, yes, pattern.Span);
                if (count != 0)
                {
                    var required = Node(HirKind.PatternDemand, BoolType, pattern.Span, [], site, Index(count),
                        HirEffects.Enumerate | HirEffects.MayFail);
                    yes = Branch(required, yes, no, pattern.Span);
                }
            }
            return Branch(TypeTest(value, type, pattern.Span), yes, no, pattern.Span);
        }
        var len = Number(pattern.Items.Count);
        var sequence = sharedPatternPrefixes.TryGetValue(value, out var shared) ? Match(shared)
            : SavePatternValue(PatternPrefix(value, TuplePrefixCap(pattern), pattern.Span, type), pattern.Span, (_, read) => Match(read));
        return Branch(TypeTest(value, type, pattern.Span), sequence, no, pattern.Span);

        HirNodeId Match(HirNodeId read)
        {
            for (int i = pattern.Items.Count - 1; i >= 0; i--)
            {
                var item = CallNative("ranges.at", OpenType("pattern item"), pattern.Items[i].Span, [read, Number(i)]);
                var child = pattern.Items[i];
                var match = yes;
                yes = SavePatternValue(item, child.Span, (_, element) => Decide(child, element, bindings, match, no));
            }
            var size = CallNative("ranges.size", hir.Type(TypeRef.Number), pattern.Span, [read]);
            var count = Node(HirKind.Binary, BoolType, pattern.Span, [size, len], (int)HirBinaryOperator.Equal);
            return Branch(count, yes, no, pattern.Span);
        }

        HirNodeId Number(int n) => hir.Literal(new(HirLiteralKind.Number, Number: n), hir.Type(TypeRef.Number), Range(pattern.Span));
        static string Index(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private HirNodeId PatternPrefix(HirNodeId input, int count, SourceSpan span, HirTypeId type)
    {
        var limit = hir.Literal(new(HirLiteralKind.Number, Number: count), hir.Type(TypeRef.Number), Range(span));
        return CallNative("ranges.pattern_prefix", type, span, [input, limit]);
    }

    private HirNodeId SavePatternValue(HirNodeId value, SourceSpan span, Func<HirLocationId, HirNodeId, HirNodeId> action)
    {
        var previous = scope.Values;
        var type = program.Node(value).Type;
        var saved = Declare($"<pattern:{hir.Locations.Count}>", span, DeclarationKind.Local, type, nullable: true);
        if (program.Node(value).Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            program.PatternOrigins[saved] = value;
        var initialize = Node(HirKind.Declare, hir.Type(TypeRef.Null), span, [value], saved.Value);
        var read = Node(HirKind.ReadLocation, type, span, data: saved.Value, effects: HirEffects.ReadLocation);
        try
        {
            var body = action(saved, read);
            return Node(HirKind.Block, program.Node(body).Type, span, [initialize, body]);
        }
        finally { scope.Values = previous; }
    }
}
