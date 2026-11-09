using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

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
            EditorReportErrors = true,
            UnwrittenLocations = LexicalWriteAnalysis.ForDocument(document, !editorIsolated)
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
                GlobalBindings = definition.Environment,
                UnwrittenLocations = LexicalWriteAnalysis.ForFunction(template.Syntax)
            };
            try { ValidateGlobalImports(frame, template.Syntax); }
            catch (CompileException error)
            {
                var diagnostic = Frame.Fork(frame);
                diagnostic.EditorReportErrors = true;
                EditorFailure(diagnostic, error);
            }
            TypeEnvironment typeEnvironment = new(template.Concepts, template.StandardShadowed, template.Source, template.SourceName);
            Frame annotationFrame = TypeFrame(frame, typeEnvironment);
            frame.ExpectedTypeEnvironment = typeEnvironment;
            if (template.Syntax.ReturnType is { } returnType)
            {
                try
                {
                    frame.ExpectedReturn = ResolveType(annotationFrame, returnType);
                    frame.ExpectedReturnSpan = returnType.Span;
                    frame.ExpectedConceptReturn = TryConcept(annotationFrame, returnType.Name, out _) ? returnType : null;
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
                        CaptureConceptDefinition(annotationFrame, parameter.Type);
                        info = TryConcept(annotationFrame, parameter.Type.Name, out _)
                            ? ConceptInfo(annotationFrame, parameter.Type) : new(ResolveType(annotationFrame, parameter.Type));
                    }
                    catch (CompileException) { }
                }
                if (parameter.Nullable) info = info with { Type = info.Type.WithNull };
                var origin = DeclarationOrigin(frame, new(parameter.Span.Start, parameter.Name.Length));
                Binding bound = new(frame.Allocate(), info, Origin: origin) { IsParameter = true };
                bound.Storage.IsUnwritten = frame.UnwrittenLocations.Contains(parameter.Span.Start);
                frame.Bind(parameter.Name, bound);
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
