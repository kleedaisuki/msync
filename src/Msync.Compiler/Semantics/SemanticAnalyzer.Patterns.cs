using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<int, (SemanticValue Range, SemanticValue Element)> patternCursors = [];

    private SemanticValue PatternCursor(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        if (node.Kind == HirKind.PatternRead)
        {
            var element = patternCursors[node.Data].Element;
            return element with { Node = Add(node, element.Type, [], context) };
        }
        if (node.Kind == HirKind.PatternDemand)
        {
            ApplyDeferredEffects(patternCursors[node.Data].Range, context);
            var boolean = hir.Type(TypeRef.Bool);
            return new(boolean, Add(node, boolean, [], context));
        }
        var range = Analyze(children[0], context);
        var elementValue = range.Element ?? ValueForType(hir.Type(Runtime(range).Element ?? TypeRef.Any));
        bool nested = patternCursors.TryGetValue(node.Data, out var enclosing);
        patternCursors[node.Data] = (range, elementValue);
        try
        {
            var result = Analyze(children[1], context);
            if (context.Output is not { } output) return result;
            var descriptor = output.Add(HirKind.TypeValue, hir.Type(TypeRef.Type), node.Source, data: elementValue.Type.Value);
            return result with { Node = Add(node, result.Type, [range.Node, result.Node, descriptor], context) };
        }
        finally
        {
            if (nested) patternCursors[node.Data] = enclosing;
            else patternCursors.Remove(node.Data);
        }
    }

    private SemanticValue AnalyzePatternField(HirNode node, SemanticValue value, SemanticContext context)
    {
        var domain = hir.Types[value.Type.Value].ScopeDomain;
        if (domain.Count > 1)
        {
            var matching = PatternFieldLayouts(domain, node.Text!, present: true);
            Trace?.Metric("pattern.field.layout_candidates", domain.Count);
            if (matching.Length != 0 && matching.Length != domain.Count)
            {
                HirNodeId tested = HirNodeId.None;
                if (context.Output is { } output)
                    foreach (int layout in matching)
                    {
                        var exact = hir.Type(HirType.ConstructedScope(layout));
                        var check = output.Add(HirKind.IsType, hir.Type(TypeRef.Bool), node.Source, [value.Node],
                            data: exact.Value, text: "pattern.layout");
                        tested = tested.IsNone ? check : output.Add(HirKind.Binary, hir.Type(TypeRef.Bool),
                            node.Source, [tested, check], data: (int)HirBinaryOperator.Or);
                    }
                return new(hir.Type(TypeRef.Bool), tested);
            }
            return KnownPatternField(node.Source, matching.Length != 0, context);
        }
        var shape = value.Scope ?? ValueForType(value.Type).Scope;
        if (shape is { } known && context.Objects.TryGetValue(known.Region, out var latest)) shape = latest;
        if (shape is null)
            throw Error(node.Source, $"Scope pattern field '{node.Text}' needs a statically described Scope.", "E0205");
        return KnownPatternField(node.Source, TryScopeField(shape, node.Text!, context, out _), context);
    }

    private SemanticValue KnownPatternField(HirSourceRange source, bool present, SemanticContext context)
    {
        Trace?.Metric(present ? "pattern.field.present" : "pattern.field.absent", 1);
        var literal = new HirLiteral(HirLiteralKind.Bool, Boolean: present);
        var type = hir.Type(TypeRef.Bool);
        return new(type, context.Output?.Literal(literal, type, source) ?? HirNodeId.None, literal);
    }

    private int[] PatternFieldLayouts(HirScopeDomain domain, string field, bool present)
    {
        List<int> result = [];
        for (int i = 0; i < domain.Count; i++)
            if (hir.ScopeLayouts[domain[i]].Fields.Any(member => member.Name == field) == present)
                result.Add(domain[i]);
        return result.ToArray();
    }

    private void RefinePatternField(HirNodeId operand, string field, bool present, SemanticContext context)
    {
        if (Subject(operand, context) is not { } current) return;
        var domain = hir.Types[current.Type.Value].ScopeDomain;
        if (domain.Count < 2) return;
        var selected = PatternFieldLayouts(domain, field, present);
        if (selected.Length == 0 || selected.Length == domain.Count) return;
        var narrowed = selected.Select(layout => ValueForType(hir.Type(HirType.ConstructedScope(layout)))).Aggregate(Join);
        SetSubject(operand, current with { Type = narrowed.Type }, context);
    }

    private SemanticValue UnreachableMatch(HirNode node, SemanticContext context)
    {
        HirTypeId never = hir.Type(new TypeRef(TypeKind.Never));
        if (context.Output is not { } output) return new(never, HirNodeId.None);
        int match = hir.Matches.Count;
        output.AddMatch(new([]));
        return new(never, output.Add(HirKind.Match, never, node.Source, [Null(node.Source, context).Node], match));
    }

    private SemanticValue AnalyzeCoveredMatch(PatternCoverageSite coverage, HirNode node,
        ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        var subject = context.Values[context.Location(coverage.Subject)];
        CheckPatternCoverage(subject, coverage.Arms, coverage.HasGuards, node.Source, context);
        return Analyze(children[0], context);
    }

    private void CheckPatternCoverage(SemanticValue subject, ImmutableArray<PatternCoverageTerm> arms, bool guards,
        HirSourceRange source, SemanticContext context)
    {
        Trace?.Metric("pattern.coverage.arms", arms.Length);
        if (arms.Any(arm => arm.Syntax is NamePattern or WildcardPattern)) return;
        var checker = new PatternCoverage(this, context, arms, guards);
        if (checker.FindMissing(subject) is { } missing)
            throw Error(source, $"Non-exhaustive match; missing {missing} or a final '_'.", "E0209");
        Trace?.Metric("pattern.coverage.partitions", checker.Partitions);
    }
}
