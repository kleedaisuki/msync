using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Compiler.Semantics;
using Msync.Hir;

namespace Msync.Compiler.Session;

// Semantic generations own declarations and facts; the lowering session owns
// physical slots. A pending generation is published only after execution reports
// which exported locations were actually initialized.
internal sealed class SemanticSession(string sourceName, int optimization)
{
    private readonly HirBuilder workspace = new();
    private readonly HirLoweringSession lowering = new();
    private readonly SemanticInstanceCache instances = new();
    private BoundProgram? accepted;
    private SemanticContext? facts;
    private PendingSubmission? pending;
    private HirLoweringLayout acceptedLayout = HirLoweringLayout.Empty;
    private SemanticObservationView? descriptions;
    private SemanticSessionSites? acceptedSites;
    private SemanticSessionDispatch dispatches = new();

    private sealed record PendingSubmission(BoundProgram Program, SemanticContext Facts,
        HirLoweringLayout Layout, SemanticSessionDispatch Dispatches);

    public IReadOnlyCollection<string> BindingNames => accepted?.Exports.Keys.ToArray() ?? [];

    public Module Compile(string source, Document? document = null, CompilationTrace? trace = null)
    {
        using var transaction = workspace.BeginTransaction();
        using var additions = instances.BeginAdditions();
        BoundProgram bound = Compiler.Bind(Binder(), source, sourceName, document, trace);
        SemanticSessionDispatch nextDispatches = dispatches.Fork();
        SemanticAnalyzer analyzer = new(bound, codeCache: instances, sessionDispatch: nextDispatches);
        HirModule module = Compiler.Analyze(analyzer, facts, trace);
        var options = new HirLoweringOptions(optimization, PreserveLocals: true);
        HirLoweringResult result = trace is null ? lowering.Lower(module, options)
            : trace.Measure("compiler.lower", (lowering, module, options),
                static state => state.lowering.Lower(state.module, state.options),
                size: static result => CompilationSize.Of(result.Module));
        Module executable = Compiler.Optimize(result.Module, optimization, trace);
        pending = new(bound, analyzer.FinalState, result.Layout, nextDispatches);
        additions.Commit();
        transaction.Commit();
        return executable;
    }

    internal SemanticEditorResult AnalyzeEditor(string source, Document? document = null,
        Func<int, Value?>? resolveAcceptedValue = null)
    {
        using var transaction = workspace.BeginTransaction();
        using var additions = instances.BeginAdditions();
        SemanticContext? seed = facts;
        if (resolveAcceptedValue is not null && accepted is not null && facts is not null)
        {
            seed = facts.QueryFork();
            new SemanticSessionValues(accepted, acceptedLayout, seed, acceptedSites!)
                .Refresh(accepted.Exports.Values, resolveAcceptedValue);
        }
        BoundProgram bound = Binder(observations: true, editorMode: true).Bind(source, sourceName, document);
        SemanticObservationBuilder observations = new(bound);
        SourceDiagnostic? diagnostic = bound.Diagnostics.FirstOrDefault();
        SemanticAnalyzer analyzer = new(bound, observations, instances, dispatches.Fork());
        try { analyzer.AnalyzeForEditor(seed); }
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

    public void AcceptSubmission(Func<int, Value?>? resolve = null)
    {
        if (pending is not { } submission) return;
        accepted = submission.Program;
        dispatches = submission.Dispatches;
        acceptedLayout = submission.Layout;
        acceptedSites = new(accepted, acceptedLayout);
        facts = submission.Facts.QueryFork();
        if (resolve is not null) Refresh(submission.Layout, resolve);
        facts.DetachFacts();
        pending = null;
        descriptions = null;
    }

    public void ReconcileSubmission(Func<int, bool> initialized, Func<int, Value?>? resolve = null)
    {
        if (pending is not { } submission) return;
        var exports = accepted?.Exports ?? ImmutableDictionary<string, HirLocationId>.Empty;
        foreach (var (name, location) in submission.Program.Exports)
        {
            if (exports.TryGetValue(name, out var prior) && prior == location) continue;
            if (submission.Layout.Locations.TryGetValue(new(submission.Program.Entry, location), out int slot)
                && initialized(slot)) exports = exports.SetItem(name, location);
        }
        accepted = submission.Program;
        dispatches = submission.Dispatches;
        accepted.Exports = exports;
        dispatches.Reconcile();
        acceptedLayout = submission.Layout;
        acceptedSites = new(accepted, acceptedLayout);
        facts = submission.Facts.QueryFork();
        if (resolve is not null) Refresh(submission.Layout, resolve);
        facts.DetachFacts();
        pending = null;
        descriptions = null;
    }

    private void Refresh(HirLoweringLayout layout, Func<int, Value?> resolve) =>
        new SemanticSessionValues(accepted!, layout, facts!, acceptedSites!, dispatches).Refresh(accepted!.Exports.Values, resolve);

    private SourceBinder Binder(bool observations = false, bool editorMode = false) => accepted is null
        ? new(workspace, observations: observations, editorMode: editorMode, isSession: true)
        : new(accepted, observations: observations, editorMode: editorMode);
}
