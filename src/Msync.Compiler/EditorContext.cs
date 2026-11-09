using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public enum EditorReceiverKind { Member, PathJoin }
public sealed record EditorContext(IReadOnlyList<SemanticMember> Bindings, SemanticMember? Receiver,
    EditorReceiverKind ReceiverKind = EditorReceiverKind.Member, ConnectionValue? PhysicalEndpoint = null, bool HasPhysicalOwner = false)
{
    public bool IsTypePosition { get; init; }
    public bool IsGlobalImport { get; init; }
}

internal readonly record struct EditorPathFlowStart(int Selections, int Leaves);

internal sealed record EditorCallTrace(string SourceName, string Source, SourceSpan Span,
    IReadOnlyList<Parameter> Parameters, IReadOnlyList<Msync.Core.TypeRef> Types, EditorCallTrace? Parent)
{
    public DiagnosticLocation Location() => new(SourceName, Source, Span, Parameters.Count == 0 ? "while checking this call"
        : $"while checking this call with {string.Join(", ", Parameters.Zip(Types, (parameter, type) => $"{parameter.Name}: {type}"))}");
}

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

    private static string ConceptDocumentation(ConceptDefinition definition)
    {
        int end = definition.Syntax.Span.Start;
        while (end > 0 && definition.Source[end - 1] is ' ' or '\t') end--;
        if (end > 0 && definition.Source[end - 1] == '\n') end--;
        if (end > 0 && definition.Source[end - 1] == '\r') end--;
        List<string> lines = [];
        while (end > 0)
        {
            int start = definition.Source.LastIndexOf('\n', end - 1) + 1;
            ReadOnlySpan<char> line = definition.Source.AsSpan(start, end - start).Trim();
            if (!line.StartsWith("#", StringComparison.Ordinal)) break;
            lines.Add(line[1..].TrimStart().ToString());
            end = start > 0 ? start - 1 : 0;
            if (end > 0 && definition.Source[end - 1] == '\r') end--;
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

internal sealed partial class Compilation
{
    private readonly EditorContextCollector? editorContexts = observe is null ? null : new();
    private readonly Dictionary<Template, (ImmutableDictionary<string, Binding> Environment, int Locals)>? editorDefinitions = observe is null ? null : [];
    private readonly List<Template>? editorDefinitionOrder = observe is null ? null : [];
    private readonly HashSet<Template>? editorCalledTemplates = observe is null ? null : [];
    private readonly Dictionary<Template, Dictionary<string, Specialization>>? probeSpecializations = observe is null ? null : [];
    private readonly HashSet<FunctionExpr>? activeEditorSpecializations = observe is null ? null : new(ReferenceEqualityComparer.Instance);
    private SourceDiagnostic? editorDiagnostic;
    private bool editorIsolated;
    internal IReadOnlyDictionary<string, Info> BuiltinInfos()
    {
        Frame frame = new(source, sourceName);
        var standard = Standard(frame, new(0, 0));
        Dictionary<string, Info> values = new(StringComparer.Ordinal) { ["$std"] = standard };
        foreach (string name in new[] { "local", "wsl", "pwd", "file", "env", "path" }) values["$" + name] = Name(frame, name, new(0, 0), reference: true);
        return values;
    }
    internal IReadOnlyList<SemanticMember> DescribeConcepts() => sessionConcepts
        .Select(pair => EditorContextCollector.DescribeConcept(pair.Value) with { Name = pair.Key }).ToArray();
    internal EditorContextCollector? EditorContexts => editorContexts;
    internal SourceDiagnostic? EditorDiagnostic => editorDiagnostic;
    internal string EditorSourceName => sourceName;

    private bool IsCurrentSource(Frame frame) => frame.SourceName == sourceName && ReferenceEquals(frame.Source, source);
    private void CaptureContext(Frame frame, int point)
    {
        if (editorContexts is not null && IsCurrentSource(frame)) editorContexts.Capture(frame, point);
    }
    private void CaptureReceiver(Frame frame, SourceSpan span, Info receiver, EditorReceiverKind kind = EditorReceiverKind.Member, Expr? expression = null)
    {
        if (editorContexts is not null && IsCurrentSource(frame)) editorContexts.Select(frame, span, receiver, kind, expression);
    }
    private void CapturePathLeaf(Frame frame, Expr expression)
    {
        if (editorContexts is not null && IsCurrentSource(frame) && expression is PathExpr or StringExpr or NameExpr or MissingExpr)
            editorContexts.Leaf(frame, expression);
    }
    private EditorPathFlowStart BeginPhysicalPathFlow(Frame frame, Expr expression) => editorContexts is not null && IsCurrentSource(frame)
        ? editorContexts.BeginPhysicalPathFlow(expression) : new(-1, -1);
    private void CompletePhysicalPathFlow(Expr expression, EditorPathFlowStart first, ConnectionValue? connection)
    {
        if (first.Selections >= 0) editorContexts!.QualifyPathFlow(expression, first, connection);
    }
    private void EditorFailure(Frame frame, CompileException error)
    {
        if (!frame.EditorReportErrors || editorDiagnostic is not null) return;
        for (var call = frame.EditorCallTrace; call is not null; call = call.Parent) error.Diagnostic.Related.Add(call.Location());
        editorDiagnostic = error.Diagnostic;
    }
    private static Info UnknownInfo => new(Msync.Core.TypeRef.Any, EditorUnknown: true);

    internal void AnalyzeDocument(Document document)
    {
        Frame frame = new(source, sourceName, sessionEnvironment)
        {
            SingleExecution = true,
            Concepts = sessionConcepts,
            Locals = sessionLocals,
            ContextOwner = document.Span,
            EditorRecovery = true,
            EditorProbe = editorIsolated,
            EditorReportErrors = true
        };
        if (functions.Count == 0) functions.Add(new("<main>", 0, 0, [], Msync.Core.TypeRef.Null));
        SnapshotFacts(frame);
        CaptureContext(frame, 0);
        var result = Statements(frame, document.Statements);
        CaptureContext(frame, source.Length);
        functions[0] = new("<main>", 0, frame.Locals, frame.Code.ToArray(), result.Type);
        try { _ = FinalizeMutableCalls(); }
        catch (CompileException error) { EditorFailure(frame, error); }
        sessionEnvironment = frame.Environment;
        sessionConcepts = frame.Concepts;
        sessionLocals = frame.Locals;
        ProbeUncalledFunctions();
    }

    private void ProbeUncalledFunctions()
    {
        if (editorDefinitions is null) return;
        for (int index = 0; index < editorDefinitionOrder!.Count; index++)
        {
            Template template = editorDefinitionOrder[index];
            if (editorCalledTemplates!.Contains(template)) continue;
            var definition = editorDefinitions[template];
            Frame frame = new(template.Source, template.SourceName, definition.Environment)
            {
                Concepts = template.Concepts,
                Locals = definition.Locals,
                ContextOwner = template.Syntax.Body.Span,
                ContextIncludesEnd = template.Syntax.Body.EditorIncomplete,
                EditorRecovery = true,
                EditorProbe = true,
                GlobalBindings = definition.Environment
            };
            try { ValidateGlobalImports(frame, template.Syntax); }
            catch (CompileException error)
            {
                var diagnostic = CloneFrame(frame);
                diagnostic.EditorReportErrors = true;
                EditorFailure(diagnostic, error);
            }
            if (template.Syntax.ReturnType is { } returnType)
            {
                try
                {
                    frame.ExpectedReturn = ResolveType(frame, returnType);
                    frame.ExpectedReturnSpan = returnType.Span;
                    frame.ExpectedConceptReturn = TryConcept(frame, returnType.Name, out _) ? returnType : null;
                }
                catch (CompileException) { }
            }
            foreach (Parameter parameter in template.Syntax.Parameters)
            {
                if (parameter.Name.Length == 0) continue;
                Info info = UnknownInfo;
                if (parameter.Type is not null)
                {
                    try
                    {
                        CaptureConceptDefinition(frame, parameter.Type);
                        info = TryConcept(frame, parameter.Type.Name, out _)
                            ? ConceptInfo(frame, parameter.Type) : new(ResolveType(frame, parameter.Type));
                    }
                    catch (CompileException) { }
                }
                if (parameter.Nullable) info = info with { Type = info.Type.WithNull };
                var origin = DeclarationOrigin(frame, new(parameter.Span.Start, parameter.Name.Length));
                frame.Bind(parameter.Name, new(frame.Allocate(), info, Origin: origin) { IsParameter = true });
                CaptureParameter(frame, new(parameter.Span.Start, parameter.Name.Length));
                if (origin is not null) CaptureDefinition(frame, origin.Span, [origin]);
                Observe(frame, new(parameter.Span.Start, parameter.Name.Length), info, parameter.Name);
            }
            if (template.Syntax.Name is { } name) frame.Bind(name, new(-1, new(Msync.Core.TypeRef.Function, template), template,
                DeclarationOrigin(frame, template.Syntax.NameSpan)));
            CaptureContext(frame, template.Syntax.Body.Span.Start);
            _ = Block(frame, template.Syntax.Body);
        }
    }

    internal Compilation ForkForEditor(string text, Action<SourceSpan, Info, string?, bool> observer, Func<int, Msync.Core.Value?>? resolveValue = null)
    {
        EditorSnapshot snapshot = new();
        var environment = sessionEnvironment.ToImmutableDictionary(pair => pair.Key,
            pair => snapshot.Binding(pair.Value, resolveValue?.Invoke(pair.Value.Slot)), StringComparer.Ordinal);
        Compilation fork = new(text, sourceName, 0, observer)
        {
            editorIsolated = true, sessionEnvironment = environment, sessionLocals = sessionLocals,
            sessionConcepts = sessionConcepts
        };
        fork.constants.AddRange(constants);
        foreach (var pair in constantIndices) fork.constantIndices.Add(pair.Key, pair.Value);
        fork.functions.AddRange(functions.Select(RenderMutableFunction));
        fork.templates.AddRange(templates);
        return fork;
    }
}
