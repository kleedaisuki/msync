using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Compiler.Semantics;
using Msync.Hir;

namespace Msync.Compiler.Session;

// Semantic generations own declarations and facts; the lowering session owns
// physical slots. A pending generation is published only after execution reports
// which exported locations were actually initialized.
internal sealed partial class SemanticSession(string sourceName, int optimization)
{
    private readonly HirBuilder workspace = new();
    private readonly LlvmHirLoweringSession lowering = new();
    private readonly SemanticInstanceCache instances = new();
    private BoundProgram? accepted;
    private SemanticContext? facts;
    private PendingSubmission? pending;
    private SemanticObservationView? descriptions;
    private SemanticSessionSites? acceptedSites;
    private SemanticSessionDispatch dispatches = new();

    private sealed record PendingSubmission(BoundProgram Program, SemanticContext Facts,
        HirLoweringLayout Layout, SemanticSessionDispatch Dispatches);

    public IReadOnlyCollection<string> BindingNames => accepted?.Exports.Keys.ToArray() ?? [];

    public HpirSessionSubmission CompilePhysical(string source, Document? document = null, CompilationTrace? trace = null)
    {
        using var transaction = workspace.BeginTransaction();
        using var additions = instances.BeginAdditions();
        BoundProgram bound = Compiler.Bind(Binder(), source, sourceName, document, trace);
        SemanticSessionDispatch nextDispatches = dispatches.Fork();
        SemanticAnalyzer analyzer = new(bound, codeCache: instances, sessionDispatch: nextDispatches);
        HirModule module = Compiler.Analyze(analyzer, facts, trace);
        LlvmHirLoweringResult graph = trace is null ? lowering.LowerPhysical(module, bound.SessionShadows, null)
            : trace.MeasureDetailed("compiler.lower", (lowering, module, shadows: bound.SessionShadows, trace),
                static state => state.lowering.LowerPhysical(state.module, state.shadows, state.trace),
                metrics: static lowered => new Dictionary<string, ulong>
                {
                    ["functions"] = (ulong)lowered.SourceFunctions.Count,
                    ["blocks"] = (ulong)lowered.Module.Functions.Sum(function => function.BasicBlocks.Count),
                    ["instructions"] = (ulong)lowered.Module.Functions.Sum(function => function.BasicBlocks.Sum(block => block.Instructions.Count)),
                    ["types"] = (ulong)lowered.Types.Count,
                    ["layouts"] = (ulong)lowered.Layouts.Count,
                    ["session.cells"] = (ulong)lowered.SessionCells.Count
                });
        graph = Compiler.OptimizeLlvm(graph, optimization, trace);
        Compiler.PrepareOptimizedHpirGraph(graph, optimization, trace);
        var (submission, layout) = lowering.EmitPhysical(graph, trace);
        pending = new(bound, analyzer.FinalState, layout, nextDispatches);
        additions.Commit();
        transaction.Commit();
        return submission with { Program = submission.Program with { OptimizationLevel = optimization } };
    }

    internal SemanticEditorResult AnalyzeEditor(string source, Document? document = null,
        CompilationTrace? trace = null)
    {
        using var transaction = workspace.BeginTransaction();
        using var additions = instances.BeginAdditions();
        BoundProgram bound = Compiler.Bind(Binder(observations: true, editorMode: true), source, sourceName, document, trace);
        SemanticObservationBuilder observations = new(bound);
        SourceDiagnostic? diagnostic = bound.Diagnostics.FirstOrDefault();
        SemanticAnalyzer analyzer = new(bound, observations, instances, dispatches.Fork()) { Trace = trace?.Detail };
        try { analyzer.AnalyzeForEditor(facts); }
        catch (CompileException error)
        {
            diagnostic ??= error.Diagnostic;
        }
        return new(observations.Freeze(), diagnostic);
    }

    public IReadOnlyList<SemanticMember> DescribeBindings()
    {
        if (accepted is null || facts is null) return [];
        descriptions ??= SemanticObservationBuilder.DescribeSession(accepted, facts);
        return new EditorSemanticIndex(descriptions).At(0).Bindings;
    }

    public IReadOnlyList<SemanticMember> DescribeConcepts()
    {
        if (accepted is null || facts is null) return [];
        descriptions ??= SemanticObservationBuilder.DescribeSession(accepted, facts);
        return new EditorSemanticIndex(descriptions).ConceptsAt(0);
    }

    public void AcceptSubmission(Func<int, HpirSessionValue?> observe, CompilationTrace? trace = null)
    {
        if (pending is not { } submission) return;
        accepted = submission.Program;
        dispatches = submission.Dispatches;
        acceptedSites = new(accepted, submission.Layout);
        facts = submission.Facts.QueryFork();
        RefreshPhysical(submission.Layout, observe, trace);
        facts.DetachFacts();
        pending = null;
        descriptions = null;
    }

    public void ReconcileSubmission(Func<int, bool> initialized, Func<int, HpirSessionValue?> observe,
        CompilationTrace? trace = null)
    {
        if (pending is not { } submission) return;
        var exports = accepted?.Exports ?? ImmutableDictionary<string, HirLocationId>.Empty;
        foreach (var shadow in submission.Program.SessionShadows)
            Admit(shadow.Name, shadow.Location);
        foreach (var (name, location) in submission.Program.Exports)
            Admit(name, location);
        accepted = submission.Program;
        dispatches = submission.Dispatches;
        accepted.Exports = exports;
        dispatches.Reconcile();
        acceptedSites = new(accepted, submission.Layout);
        facts = submission.Facts.QueryFork();
        RefreshPhysical(submission.Layout, observe, trace);
        facts.DetachFacts();
        pending = null;
        descriptions = null;

        void Admit(string name, HirLocationId location)
        {
            if (exports.TryGetValue(name, out var prior) && prior == location) return;
            if (submission.Layout.Locations.TryGetValue(new(submission.Program.Entry, location), out int slot)
                && initialized(slot)) exports = exports.SetItem(name, location);
        }
    }

    private void RefreshPhysical(HirLoweringLayout layout, Func<int, HpirSessionValue?> resolve, CompilationTrace? trace)
    {
        if (trace?.Detail is not { } detail) RefreshPhysicalCore(layout, resolve, null);
        else detail.Run("compiler.session.observe_physical", (owner: this, layout, resolve, detail),
            static work => work.owner.RefreshPhysicalCore(work.layout, work.resolve, work.detail));
    }

    private void RefreshPhysicalCore(HirLoweringLayout layout, Func<int, HpirSessionValue?> resolve, CompilationTrace? trace)
    {
        BoundProgram program = accepted!;
        SemanticContext context = facts!;
        List<(HirLocationId Location, HpirSessionValue Value)>? references = null;
        int scalars = 0, callables = 0, snapshots = 0;
        foreach (HirLocationId location in program.Exports.Values)
        {
            if (!layout.Locations.TryGetValue(new(program.Entry, location), out int slot)
                || resolve(slot) is not { } value) continue;
            if (value.Kind is TypeKind.Function or TypeKind.Scope)
                (references ??= []).Add((location, value));
            else if (value.Kind == TypeKind.Range)
            {
                if (context.Values.TryGetValue(location, out var range))
                    context.Values[location] = range with { SessionSnapshot = value.Snapshot };
                if (value.Snapshot.HasValue) ++snapshots;
            }
            else
            {
                context.Values[location] = ReadScalar(program, value);
                ++scalars;
            }
        }
        trace?.Metric("scalars", scalars);
        trace?.Metric("range.snapshots", snapshots);
        if (references is null) return;
        ScopeRecovery? recovery = null;
        foreach (var (location, observed) in references)
        {
            SemanticValue hint = context.Values.GetValueOrDefault(location)
                ?? new(program.Hir.Locations[location.Value].StorageType, HirNodeId.None);
            SemanticValue current;
            if (observed.Scope is { } scope)
            {
                recovery ??= new(program, context, acceptedSites!,
                    (prior, value) => ReadCallable(layout, prior, value));
                current = recovery.Read(hint, scope);
            }
            else
            {
                current = ReadCallable(layout, hint, observed);
                dispatches.ObserveRoot(location, hint, current);
                ++callables;
            }
            context.Values[location] = current;
        }
        trace?.Metric("callables", callables);
        trace?.Metric("scope.instances", recovery?.Count ?? 0);
        trace?.Metric("scope.fields", recovery?.Fields ?? 0);
    }

    private SemanticValue ReadCallable(HirLoweringLayout layout, SemanticValue hint, HpirSessionValue observed)
    {
        BoundProgram program = accepted!;
        SemanticContext context = facts!;
        SemanticCallable actual;
        if (observed.Text is { } native) actual = new(NativeName: native);
        else
        {
            int code = checked((int)observed.Bits - 1);
            if (!layout.Callables.TryGetValue(code, out var identity))
                throw new InvalidOperationException("A retained HPIR function has no producer-owned callable identity.");
            HirTemplateId? template = identity.IsTemplate ? new(identity.Value) : program.Hir.Functions[identity.Value].Template;
            if (template is null) return hint;
            var prior = hint.Callables.IsDefaultOrEmpty ? null : hint.Callables.FirstOrDefault(item => item.Template == template);
            var definition = program.Hir.Templates[template.Value.Value];
            var captures = ImmutableArray.CreateBuilder<SemanticCapture>(definition.Captures.Length);
            for (int index = 0; index < definition.Captures.Length; ++index)
            {
                var capture = definition.Captures[index];
                var previous = prior is not null && !prior.Captures.IsDefaultOrEmpty && index < prior.Captures.Length
                    ? prior.Captures[index] : null;
                HirLocationId source = previous?.Source ?? capture.SourceLocation;
                SemanticValue value = context.Values.GetValueOrDefault(source) ?? previous?.Value
                    ?? new(program.Hir.Locations[source.Value].StorageType, HirNodeId.None);
                captures.Add(new(source, value, capture.Mode));
            }
            actual = new(template, Captures: captures.ToImmutable(), RuntimeIdentity: identity);
        }
        return new(program.Hir.Type(TypeRef.Function), HirNodeId.None, Callables: [actual]);
    }

    private SourceBinder Binder(bool observations = false, bool editorMode = false) => accepted is null
        ? new(workspace, observations: observations, editorMode: editorMode, isSession: true)
        : new(accepted, observations: observations, editorMode: editorMode);
}
