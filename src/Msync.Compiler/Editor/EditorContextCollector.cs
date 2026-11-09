using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class EditorContextCollector
{
    private sealed record Snapshot(SourceSpan Owner, bool IncludesEnd, int Point, ImmutableDictionary<string, Binding> Bindings,
        ImmutableDictionary<string, ConceptDefinition> Concepts, bool Concrete);
    private sealed record Selection(SourceSpan Span, Info Receiver, bool Concrete, EditorReceiverKind Kind, Expr? Expression);
    private sealed record PathLeaf(Expr Expression, bool Concrete, bool HasOwner = false, ConnectionValue? Endpoint = null);
    private sealed record ImportContext(SourceSpan Span, ImmutableDictionary<string, Binding> Bindings, bool Concrete);
    private readonly List<Snapshot> snapshots = [];
    private readonly List<Selection> selections = [];
    private readonly List<PathLeaf> leaves = [];
    private readonly List<ImportContext> imports = [];
    private readonly HashSet<Expr> physicalExpressions = new(ReferenceEqualityComparer.Instance);

    public void Capture(Frame frame, int point) => snapshots.Add(new(frame.ContextOwner, frame.ContextIncludesEnd, point,
        frame.Environment, frame.Concepts, frame.EditorReportErrors));

    public IReadOnlyList<SemanticMember> ConceptsAt(int offset)
    {
        Snapshot[] containing = snapshots.Where(item => item.Owner.Start <= offset
            && (offset < item.Owner.End || item.IncludesEnd && offset == item.Owner.End) && item.Point <= offset).ToArray();
        if (containing.Length == 0) return [];
        int width = containing.Min(item => item.Owner.Length);
        Snapshot[] inner = containing.Where(item => item.Owner.Length == width).ToArray();
        int point = inner.Max(item => item.Point);
        Snapshot[] latest = inner.Where(item => item.Point == point).ToArray();
        if (latest.Any(item => item.Concrete)) latest = latest.Where(item => item.Concrete).ToArray();
        return latest[0].Concepts.Keys.Where(name => latest.All(item => item.Concepts.ContainsKey(name)))
            .Select(name => DescribeConcept(latest[0].Concepts[name]) with { Name = name })
            .ToArray();
    }

    internal static SemanticMember DescribeConcept(ConceptDefinition definition)
    {
        SemanticMember[] members = definition.Syntax.Members.Select(member => new SemanticMember(member.Name,
            member.Parameters is not null ? "Function" : member.Type is { } type ? definition.Source[type.Span.Start..type.Span.End] : "unknown",
            definition.Source[member.Span.Start..member.Span.End],
            "A required member of this structural concept.", SemanticRole: member.Parameters is null ? "variable" : "function")).ToArray();
        string description = "A named structural constraint. Required fields and callable signatures are checked at specialization.";
        string documentation = ConceptDocumentation(definition);
        if (documentation.Length != 0) description = documentation + "\n\n" + description;
        if (definition.Syntax.Bases.Count != 0)
            description += "\n\nIncludes: " + string.Join(", ", definition.Syntax.Bases.Select(type => $"`{type.Name}`")) + ".";
        if (members.Length != 0)
            description += "\n\nRequired members:\n" + string.Join("\n", members.Select(member => $"- `{member.Signature}`"));
        return new(definition.Syntax.Name, "Type", $"concept {definition.Syntax.Name}", description, members, SemanticRole: "type")
        {
            IsConstraint = true
        };
    }

    private static string ConceptDocumentation(ConceptDefinition definition) => DocumentationAt(definition.Source, definition.Syntax.Span.Start);

    internal static string DocumentationAt(string source, int position)
    {
        int end = position;
        while (end > 0 && source[end - 1] is ' ' or '\t') end--;
        if (end > 0 && source[end - 1] == '\n') end--;
        if (end > 0 && source[end - 1] == '\r') end--;
        List<string> lines = [];
        while (end > 0)
        {
            int start = source.LastIndexOf('\n', end - 1) + 1;
            ReadOnlySpan<char> line = source.AsSpan(start, end - start).Trim();
            if (!line.StartsWith("#", StringComparison.Ordinal)) break;
            lines.Add(line[1..].TrimStart().ToString());
            end = start > 0 ? start - 1 : 0;
            if (end > 0 && source[end - 1] == '\r') end--;
        }
        lines.Reverse();
        return string.Join("\n", lines);
    }
    public void Import(Frame frame, GlobalStatement statement)
    {
        int start = statement.Span.Start + "global".Length;
        int end = statement.Span.End;
        if (end > start && frame.Source[end - 1] == ';') end--;
        else while (end < frame.Source.Length && char.IsWhiteSpace(frame.Source[end])) end++;
        var bindings = frame.GlobalBindings!.Where(pair => !frame.Environment.TryGetValue(pair.Key, out var local)
            || ReferenceEquals(local.Identity, pair.Value.Identity)).ToImmutableDictionary(StringComparer.Ordinal);
        imports.Add(new(new(start, end - start), bindings, frame.EditorReportErrors));
    }
    public void Leaf(Frame frame, Expr expression) => leaves.Add(new(expression, frame.EditorReportErrors));
    public EditorPathFlowStart BeginPhysicalPathFlow(Expr expression)
    {
        physicalExpressions.Add(expression);
        return new(selections.Count, leaves.Count);
    }
    public void Select(Frame frame, SourceSpan span, Info receiver, EditorReceiverKind kind, Expr? expression) =>
        selections.Add(new(span, receiver, frame.EditorReportErrors, kind, expression));

    public void QualifyPathFlow(Expr expression, EditorPathFlowStart first, ConnectionValue? connection)
    {
        HashSet<Expr> flow = new(ReferenceEqualityComparer.Instance);
        CollectPathFlow(expression, flow);
        for (int index = first.Leaves; index < leaves.Count; index++)
        {
            PathLeaf leaf = leaves[index];
            if (!flow.Contains(leaf.Expression)) continue;
            bool unknown = leaf.HasOwner && !Compilation.HasStaticPathProjection(leaf.Endpoint)
                || connection is null;
            leaves[index] = leaf with { HasOwner = true, Endpoint = unknown ? null : connection };
        }
        for (int index = first.Selections; index < selections.Count; index++)
        {
            Selection selection = selections[index];
            if (selection.Kind != EditorReceiverKind.PathJoin || selection.Expression is null || !flow.Contains(selection.Expression)) continue;
            var path = selection.Receiver.KnownPath ?? (selection.Receiver.KnownText is { } text ? new PathValue(text) : null);
            if (path is null) continue;
            var receiver = selection.Receiver with { KnownPath = Compilation.QualifyKnownPath(path, connection), KnownText = null };
            selections[index] = selection with { Receiver = receiver };
        }
    }

    private void CollectPathFlow(Expr expression, HashSet<Expr> flow)
    {
        switch (expression)
        {
            case BinaryExpr { Operator: "/" } joined:
                flow.Add(joined);
                CollectPathFlow(joined.Left, flow);
                CollectPathFlow(joined.Right, flow);
                break;
            case BinaryExpr { Operator: "@" } qualified when physicalExpressions.Contains(qualified):
                CollectPathFlow(qualified.Left, flow);
                break;
            case BlockExpr { Statements.Count: > 0 } block when block.Statements[^1] is ExpressionStatement { Discarded: false } tail:
                CollectPathFlow(tail.Expression, flow);
                break;
            case IfExpr conditional:
                CollectPathFlow(conditional.Then, flow);
                if (conditional.Else is not null) CollectPathFlow(conditional.Else, flow);
                break;
            case MatchExpr matched:
                foreach (MatchArm arm in matched.Arms) CollectPathFlow(arm.Value, flow);
                break;
            case PathExpr or StringExpr or NameExpr or MissingExpr:
                flow.Add(expression);
                break;
        }
    }

    private sealed record SelectedContext(IReadOnlyList<ImmutableDictionary<string, Binding>> Bindings,
        Info[] Receivers, EditorReceiverKind Kind, ConnectionValue? Endpoint, bool HasOwner);

    public EditorContext At(int offset)
    {
        var targets = ImportsAt(offset);
        if (targets.Count != 0) return EditorCompiler.DescribeContext(targets, []) with { IsGlobalImport = true };
        var selected = SelectAt(offset);
        return EditorCompiler.DescribeContext(selected.Bindings, selected.Receivers, selected.Kind, selected.Endpoint, selected.HasOwner);
    }

    public SemanticMember? Resolve(int offset, string name)
    {
        var targets = ImportsAt(offset);
        if (targets.Count != 0) return EditorCompiler.ResolveContext(targets, [], EditorReceiverKind.Member, name);
        var selected = SelectAt(offset);
        return EditorCompiler.ResolveContext(selected.Bindings, selected.Receivers, selected.Kind, name);
    }

    private IReadOnlyList<ImmutableDictionary<string, Binding>> ImportsAt(int offset)
    {
        var selected = imports.Where(item => item.Span.Start <= offset && offset <= item.Span.End).ToArray();
        if (selected.Any(item => item.Concrete)) selected = selected.Where(item => item.Concrete).ToArray();
        return selected.Select(item => item.Bindings).ToArray();
    }

    private SelectedContext SelectAt(int offset)
    {
        var containing = snapshots.Where(item => item.Owner.Start <= offset && (offset < item.Owner.End || item.IncludesEnd && offset == item.Owner.End) && item.Point <= offset).ToArray();
        IReadOnlyList<ImmutableDictionary<string, Binding>> bindings = [];
        if (containing.Length != 0)
        {
            int width = containing.Min(item => item.Owner.Length);
            var inner = containing.Where(item => item.Owner.Length == width).ToArray();
            int point = inner.Max(item => item.Point);
            var latest = inner.Where(item => item.Point == point).ToArray();
            if (latest.Any(item => item.Concrete)) latest = latest.Where(item => item.Concrete).ToArray();
            bindings = latest.Select(item => item.Bindings).ToArray();
        }
        var receivers = selections.Where(item => item.Span.Start <= offset && offset <= item.Span.End).ToArray();
        if (receivers.Length != 0)
        {
            int width = receivers.Min(item => item.Span.Length);
            receivers = receivers.Where(item => item.Span.Length == width).ToArray();
            if (receivers.Any(item => item.Concrete)) receivers = receivers.Where(item => item.Concrete).ToArray();
        }
        var owners = leaves.Where(item => item.HasOwner && item.Expression.Span.Start <= offset && offset <= item.Expression.Span.End).ToArray();
        if (owners.Length != 0)
        {
            int width = owners.Min(item => item.Expression.Span.Length);
            owners = owners.Where(item => item.Expression.Span.Length == width).ToArray();
            if (owners.Any(item => item.Concrete)) owners = owners.Where(item => item.Concrete).ToArray();
        }
        ConnectionValue? endpoint = owners.Length == 0 ? null : owners[0].Endpoint;
        if (endpoint is not null && owners.Any(item => item.Endpoint != endpoint)) endpoint = null;
        return new(bindings, receivers.Select(item => item.Receiver).ToArray(),
            receivers.Length == 0 ? EditorReceiverKind.Member : receivers[0].Kind, endpoint, owners.Length != 0);
    }
}
