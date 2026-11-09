using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// Semantic generations own declarations and facts; the lowering session owns
// physical slots. A pending generation is published only after execution reports
// which exported locations were actually initialized.
internal sealed class SemanticSession(string sourceName, int optimization)
{
    private readonly HirBuilder workspace = new();
    private readonly HirLoweringSession lowering = new();
    private BoundProgram? accepted;
    private SemanticContext? facts;
    private PendingSubmission? pending;
    private HirLoweringLayout acceptedLayout = HirLoweringLayout.Empty;
    private SemanticObservationView? descriptions;

    private sealed record PendingSubmission(BoundProgram Program, SemanticContext Facts,
        HirLoweringLayout Layout);

    public IReadOnlyCollection<string> BindingNames => accepted?.Exports.Keys.ToArray() ?? [];

    public Module Compile(string source, Document? document = null)
    {
        using var transaction = workspace.BeginTransaction();
        BoundProgram bound = Binder().Bind(source, sourceName, document);
        SemanticAnalyzer analyzer = new(bound);
        HirModule module = analyzer.Analyze(facts);
        HirLoweringResult result = lowering.Lower(module, new(optimization, PreserveLocals: true));
        pending = new(bound, analyzer.FinalState, result.Layout);
        transaction.Commit();
        return result.Module;
    }

    internal SemanticEditorResult AnalyzeEditor(string source, Document? document = null,
        Func<int, Value?>? resolveAcceptedValue = null)
    {
        using var transaction = workspace.BeginTransaction();
        SemanticContext? seed = facts;
        if (resolveAcceptedValue is not null && accepted is not null && facts is not null)
        {
            seed = facts.QueryFork();
            new SemanticSessionValues(accepted, acceptedLayout, seed)
                .Refresh(accepted.Exports.Values, resolveAcceptedValue);
        }
        BoundProgram bound = Binder(observations: true, editorMode: true).Bind(source, sourceName, document);
        SemanticObservationBuilder observations = new(bound);
        SourceDiagnostic? diagnostic = bound.Diagnostics.FirstOrDefault();
        SemanticAnalyzer analyzer = new(bound, observations);
        HirModule module;
        try { module = analyzer.Analyze(seed); }
        catch (CompileException error)
        {
            diagnostic ??= error.Diagnostic;
            module = bound.Hir.Freeze(bound.Entry, isSession: true);
        }
        return new(observations.Freeze(module), diagnostic);
    }

    public IReadOnlyList<SemanticMember> DescribeBindings()
    {
        descriptions ??= AnalyzeEditor("").View;
        return new EditorSemanticIndex(descriptions).At(0).Bindings;
    }

    public IReadOnlyList<SemanticMember> DescribeConcepts()
    {
        descriptions ??= AnalyzeEditor("").View;
        return new EditorSemanticIndex(descriptions).ConceptsAt(0);
    }

    public void AcceptSubmission(Func<int, Value?>? resolve = null)
    {
        if (pending is not { } submission) return;
        accepted = submission.Program;
        acceptedLayout = submission.Layout;
        facts = submission.Facts.QueryFork();
        if (resolve is not null) Refresh(submission.Layout, resolve);
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
        accepted.Exports = exports;
        acceptedLayout = submission.Layout;
        facts = submission.Facts.QueryFork();
        if (resolve is not null) Refresh(submission.Layout, resolve);
        pending = null;
        descriptions = null;
    }

    private void Refresh(HirLoweringLayout layout, Func<int, Value?> resolve) =>
        new SemanticSessionValues(accepted!, layout, facts!).Refresh(accepted!.Exports.Values, resolve);

    private SourceBinder Binder(bool observations = false, bool editorMode = false) => accepted is null
        ? new(workspace, observations: observations, editorMode: editorMode, isSession: true)
        : new(accepted, observations: observations, editorMode: editorMode);
}
