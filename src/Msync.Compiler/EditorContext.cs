using System.Collections.Immutable;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

public sealed record EditorContext(IReadOnlyList<SemanticMember> Bindings, SemanticMember? Receiver);

internal sealed record EditorCallTrace(string SourceName, string Source, SourceSpan Span,
    IReadOnlyList<Parameter> Parameters, IReadOnlyList<Msync.Core.TypeRef> Types, EditorCallTrace? Parent)
{
    public DiagnosticLocation Location() => new(SourceName, Source, Span, Parameters.Count == 0 ? "while checking this call"
        : $"while checking this call with {string.Join(", ", Parameters.Zip(Types, (parameter, type) => $"{parameter.Name}: {type}"))}");
}

internal sealed class EditorContextCollector
{
    private sealed record Snapshot(SourceSpan Owner, bool IncludesEnd, int Point, ImmutableDictionary<string, Binding> Bindings, bool Concrete);
    private sealed record Selection(SourceSpan Span, Info Receiver, bool Concrete);
    private readonly List<Snapshot> snapshots = [];
    private readonly List<Selection> selections = [];

    public void Capture(Frame frame, int point) => snapshots.Add(new(frame.ContextOwner, frame.ContextIncludesEnd, point, frame.Environment, frame.EditorReportErrors));
    public void Select(Frame frame, SourceSpan span, Info receiver) => selections.Add(new(span, receiver, frame.EditorReportErrors));

    public EditorContext At(int offset)
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
        return EditorCompiler.DescribeContext(bindings, receivers.Select(item => item.Receiver).ToArray());
    }
}

internal sealed partial class Compilation
{
    private readonly EditorContextCollector? editorContexts = observe is null ? null : new();
    private readonly Dictionary<Template, (ImmutableDictionary<string, Binding> Environment, int Locals)>? editorDefinitions = observe is null ? null : [];
    private readonly List<Template>? editorDefinitionOrder = observe is null ? null : [];
    private readonly HashSet<Template>? editorCalledTemplates = observe is null ? null : [];
    private readonly Dictionary<Template, Dictionary<string, Specialization>>? probeSpecializations = observe is null ? null : [];
    private SourceDiagnostic? editorDiagnostic;
    private bool editorIsolated;
    internal IReadOnlyDictionary<string, Info> BuiltinInfos()
    {
        Frame frame = new(source, sourceName);
        var standard = Standard(frame, new(0, 0));
        Dictionary<string, Info> values = new(StringComparer.Ordinal) { ["$std"] = standard };
        foreach (string name in new[] { "view", "views", "ranges" }) values["$" + name] = standard.Scope!.Members["ranges"];
        foreach (string name in new[] { "local", "wsl", "pwd", "file", "env", "path" }) values["$" + name] = Name(frame, name, new(0, 0), reference: true);
        return values;
    }
    internal EditorContextCollector? EditorContexts => editorContexts;
    internal SourceDiagnostic? EditorDiagnostic => editorDiagnostic;
    internal string EditorSourceName => sourceName;

    private bool IsCurrentSource(Frame frame) => frame.SourceName == sourceName && ReferenceEquals(frame.Source, source);
    private void CaptureContext(Frame frame, int point)
    {
        if (editorContexts is not null && IsCurrentSource(frame)) editorContexts.Capture(frame, point);
    }
    private void CaptureReceiver(Frame frame, SourceSpan span, Info receiver)
    {
        if (editorContexts is not null && IsCurrentSource(frame)) editorContexts.Select(frame, span, receiver);
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
            Locals = sessionLocals,
            ContextOwner = document.Span,
            EditorRecovery = true,
            EditorProbe = editorIsolated,
            EditorReportErrors = true
        };
        if (functions.Count == 0) functions.Add(new("<main>", 0, 0, [], Msync.Core.TypeRef.Null));
        CaptureContext(frame, 0);
        _ = Statements(frame, document.Statements);
        CaptureContext(frame, source.Length);
        sessionEnvironment = frame.Environment;
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
                Locals = definition.Locals,
                ContextOwner = template.Syntax.Body.Span,
                ContextIncludesEnd = template.Syntax.Body.EditorIncomplete,
                EditorRecovery = true,
                EditorProbe = true
            };
            foreach (Parameter parameter in template.Syntax.Parameters)
            {
                if (parameter.Name.Length == 0) continue;
                Info info = UnknownInfo;
                if (parameter.Type is not null)
                {
                    try { info = new(ResolveType(frame, parameter.Type)); }
                    catch (CompileException) { }
                }
                if (parameter.Nullable) info = info with { Type = info.Type.WithNull };
                frame.Bind(parameter.Name, new(frame.Allocate(), info));
                Observe(frame, new(parameter.Span.Start, parameter.Name.Length), info, parameter.Name);
            }
            if (template.Syntax.Name is { } name) frame.Bind(name, new(-1, new(Msync.Core.TypeRef.Function, template), template));
            CaptureContext(frame, template.Syntax.Body.Span.Start);
            _ = Block(frame, template.Syntax.Body);
        }
    }

    internal Compilation ForkForEditor(string text, Action<SourceSpan, Info, string?, bool> observer, Func<int, Msync.Core.Value?>? resolveValue = null)
    {
        EditorSnapshot snapshot = new();
        var environment = sessionEnvironment.ToImmutableDictionary(pair => pair.Key,
            pair => snapshot.Binding(pair.Value, resolveValue?.Invoke(pair.Value.Slot)), StringComparer.Ordinal);
        Compilation fork = new(text, sourceName, 0, observer) { editorIsolated = true, sessionEnvironment = environment, sessionLocals = sessionLocals };
        fork.constants.AddRange(constants);
        foreach (var pair in constantIndices) fork.constantIndices.Add(pair.Key, pair.Value);
        fork.functions.AddRange(functions);
        fork.templates.AddRange(templates);
        return fork;
    }
}
