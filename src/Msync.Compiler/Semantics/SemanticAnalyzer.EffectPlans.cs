using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private void ApplyEventPlan(ImmutableArray<SemanticWriteEvent> events, SemanticContext context, HirSourceRange source)
    {
        if (events.IsDefaultOrEmpty) return;
        if (Trace is null) { ApplyEventPlanCore(events, context, source); return; }
        Trace.Run("compiler.semantic.apply_plan", (self: this, events, context, source),
            static state => state.self.ApplyEventPlanCore(state.events, state.context, state.source),
            attributes: HirTelemetry.Source(hir.Sources, source));
    }

    private void ApplyEventPlanCore(ImmutableArray<SemanticWriteEvent> events, SemanticContext context, HirSourceRange source)
    {
        context.ActivateWriteEvents();
        var log = context.WriteEvents!;
        context.WriteEvents = null;
        ReplayPlan(events, context, source);
        context.WriteEvents = log;
        log.AddRange(events);
    }

    private void ReplayPlan(ImmutableArray<SemanticWriteEvent> events, SemanticContext destination, HirSourceRange source)
    {
        foreach (var item in events)
        {
            if (item.Guards.Any(destination.InitializedSingletons.Contains))
            {
                Trace?.Metric("effects.guard_skipped", 1);
                continue;
            }
            if (!item.Branches.IsDefaultOrEmpty)
            {
                Trace?.Metric("effects.branches", item.Branches.Length);
                SemanticContext[] branches = new SemanticContext[item.Branches.Length];
                for (int index = 0; index < branches.Length; index++)
                {
                    var branch = new SemanticContext(destination.Function, destination.Output, destination)
                    {
                        Queries = destination.Queries,
                        DeferredReads = destination.DeferredReads,
                        InitializedSingletons = destination.InitializedSingletons,
                        MaybeInitializedSingletons = destination.MaybeInitializedSingletons,
                        ActiveSingletonGuards = destination.ActiveSingletonGuards
                    };
                    ReplayPlan(item.Branches[index], branch, source);
                    branches[index] = branch;
                }
                MergeContexts(destination, branches);
            }
            if (item.Write is { } write)
            {
                Trace?.Metric("effects.writes", 1);
                var after = write.After;
                var before = destination.ReadWriteTarget(destination.WriteTarget(write));
                if (before is not null)
                    foreach (int guard in item.Guards)
                        if (destination.MaybeInitializedSingletons.Contains(guard))
                            after = Join(after, destination.FactWhenInitialized(guard, write, before));
                var saved = destination.ActiveSingletonGuards;
                destination.ActiveSingletonGuards = item.Guards;
                ApplyPlanWrite(write with { After = after }, destination, source);
                destination.ActiveSingletonGuards = saved;
            }
            if (item.InitializeSingleton is { } singleton)
            {
                Trace?.Metric("effects.singletons", 1);
                destination.InitializedSingletons = destination.InitializedSingletons.Add(singleton);
                destination.MaybeInitializedSingletons = destination.MaybeInitializedSingletons.Add(singleton);
            }
        }
    }

    private void ApplyPlanWrite(SemanticDeferredWrite write, SemanticContext context, HirSourceRange source)
    {
        if (write.Kind == SemanticWriteKind.Location)
        {
            SealWriteStorage(write.Location, write.After, context, source);
            CheckSessionAssignment(write.Location, write.After, source, context);
            context.Values[write.Location] = write.After;
            MarkProgramCell(write.Location, context);
            context.CellWrites.Add(write.Location);
            context.RecordWrite(write);
            return;
        }
        if (write.Kind == SemanticWriteKind.Indexed)
        {
            if (context.Indexed.TryGetValue(write.Region, out var storage))
                context.Indexed[write.Region] = write.IndexedStorage is { } summarized
                    ? JoinIndexed(storage, summarized) with { Element = write.After }
                    : storage with
                    {
                        Element = write.After,
                        FixedCarrier = storage.FixedCarrier ?? (storage.CarrierConstraints.IsDefaultOrEmpty
                            && Runtime(write.After).Kind != TypeKind.Never ? write.After.Type : null)
                    };
            context.IndexedWrites.Add(write.Region);
            context.RecordWrite(write);
            return;
        }
        if (!context.Objects.TryGetValue(write.Region, out var target)) return;
        if (target.Locations.TryGetValue(write.Field!, out var fieldLocation))
        {
            SealWriteStorage(fieldLocation, write.After, context, source);
            CheckSessionMemberAssignment(target, write.Field!, write.After, hir.Locations[fieldLocation.Value].Source, context);
        }
        InvalidateScopeAliases(target, write.Field!, write.After, context);
        target = target with { Fields = target.Fields.SetItem(write.Field!, write.After) };
        context.Objects[write.Region] = target;
        if (target.Locations.TryGetValue(write.Field!, out var location))
        {
            context.Values[location] = write.After;
            MarkProgramCell(location, context);
        }
        context.FieldWrites.Add((write.Region, write.Field!));
        context.RecordWrite(write);
    }

    private void SealWriteStorage(HirLocationId location, SemanticValue value, SemanticContext context, HirSourceRange source)
    {
        var target = StorageType(location, context);
        try { Require(value, target, source, allowImplicit: true, context); }
        catch (CompileException error)
        {
            var declaration = hir.Locations[location.Value];
            SemanticDiagnosticFactory.FixedStorage(error, hir.Sources[declaration.Source.Source.Value],
                declaration, hir.Types[target.Value].RuntimeType);
            throw;
        }
        var resolved = ResolveHirType(target, context);
        if (hir.Locations[location.Value].AdmitsNull) resolved = Nullable(resolved);
        if (resolved != target) SealStorage(location, resolved, context);
    }

}
