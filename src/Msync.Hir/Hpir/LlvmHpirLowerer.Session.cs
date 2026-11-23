using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int aliasLayout = graph.SessionGeneration?.HpirLayouts?.AliasLayout ?? -1;
        private Dictionary<int, HpirSessionField[]>? newSessionScopeLayouts;
        private Dictionary<HpirSessionAllocation, int>? sessionAllocationImports;
        internal readonly List<HpirSessionAllocation> SessionAllocations = [];

        private bool TrySessionAllocation(LlvmHeapAllocInst allocation, int layout, out int id)
        {
            id = -1;
            if (graph.SessionGeneration is not { } session || allocation.LogicalAllocationSite < 0)
                return false;
            var entry = new HpirSessionAllocation(layout, session.Site(allocation.LogicalAllocationSite));
            var imports = sessionAllocationImports ??= [];
            if (imports.TryGetValue(entry, out id)) return true;
            imports.Add(entry, id = Imports.Count);
            Imports.Add(new(HpirService.AllocateSessionScope, SessionAllocations.Count, HpirWidth.U64, []));
            SessionAllocations.Add(entry);
            return true;
        }

        private void RememberSessionScopeLayout(int logical, int physical)
        {
            if (graph.SessionScopeLayouts?.GetValueOrDefault(logical) is not { } structure) return;
            var fields = graph.Layouts[logical].Fields;
            var result = new HpirSessionField[fields.Count];
            for (int index = 0; index < fields.Count; ++index)
            {
                var type = structure.Elements[index];
                int offset = FieldOffset(structure, (uint)index);
                int completion = TailAtPayloadOffset(logical, offset) is { } tail ? offset + tail : -1;
                result[index] = new(fields[index].Name, offset, graph.Types[fields[index].Type].Kind,
                    SessionUnion(type), completion);
            }
            var previous = graph.SessionGeneration!.HpirLayouts?.ScopeLayouts;
            (newSessionScopeLayouts ??= previous is null ? [] : new(previous)).Add(physical, result);
        }

        private HpirSessionUnion? SessionUnion(LlvmType type) =>
            Union(type) == TypeKind.Never ? null : new(FieldOffset(type, 1), FieldOffset(type, 2));

        private int SessionAliasLayout()
        {
            if (aliasLayout >= 0) return aliasLayout;
            aliasLayout = Layouts.Count;
            Layouts.Add(new(sizeof(ulong), sizeof(ulong), []) { BorrowOffsets = [0] });
            return aliasLayout;
        }

        internal int SessionTextLayout() =>
            graph.SessionGeneration is { } && (TextLiterals.Count != 0 || PathAnchorsNeeded)
                ? TextLayout() : TextLayoutIndex;

        internal HpirSessionSubmission? FinishSession(HpirProgram program, CompilationTrace? trace)
        {
            if (graph.SessionGeneration is not { } session) return null;
            var resultType = graph.Types[graph.SourceFunctions[graph.Entry].ResultType];
            string? scopeBoundary = graph.FutureFields.Count == 0 ? null : PrivateSessionResultBoundary(resultType);
            bool displaysFutures = graph.FutureFields.Count != 0 && scopeBoundary is null;
            trace?.Metric("session.scope_future_fields", graph.FutureFields.Count);
            trace?.Metric("session.scope_fields_at_display", displaysFutures ? graph.FutureFields.Count : 0);
            trace?.Metric("session.future_parameters", graph.FutureParameters.Count);
            trace?.Metric("session.future_results", graph.FutureResults.Count);
            if (scopeBoundary is not null || graph.FutureParameters.Count != 0 || graph.FutureResults.Count != 0
                || program.Imports.Any(import => import.Service == HpirService.LiveTransferFutureText
                    || import.Service == HpirService.LiveTransfer && import.Result != HpirWidth.Void))
                throw new MsyncException("This HPIR session cannot publish a cross-submission pending carrier"
                    + (scopeBoundary is null ? "." : $": {scopeBoundary}."));
            TypeKind kind = resultType.Kind;
            var resultUnion = SessionUnion(graph.Entry.ReturnType);
            const TypeKind records = TypeKind.Null | TypeKind.Bool | TypeKind.Number | TypeKind.String | TypeKind.Type
                | TypeKind.Scope | TypeKind.Path | TypeKind.Function;
            int resultBytes = resultUnion.HasValue && (kind & ~records) == 0
                ? checked((int)Data.GetTypeAllocSize(graph.Entry.ReturnType).GetFixedValue()) : kind switch
            {
                TypeKind.Null or TypeKind.Bool or TypeKind.Number or TypeKind.String or TypeKind.Type or TypeKind.Scope or TypeKind.Process or TypeKind.Connection => 0,
                TypeKind.Path => HpirPathImports.PathBytes,
                TypeKind.Function => HpirCallableAbi.Bytes,
                _ => throw new MsyncException($"HPIR session result presentation is not supported for {kind}.")
            };
            trace?.Metric("session.generation", session.Number);
            trace?.Metric("session.layout_lookup.inherited_hits", inheritedLayoutHits);
            trace?.Metric("session.layout_entries", program.Layouts.Count);
            trace?.Metric("session.result_record_bytes", resultBytes);
            var sessionScopeLayouts = newSessionScopeLayouts ?? session.HpirLayouts?.ScopeLayouts ?? [];
            trace?.Metric("session.scope_presentations", sessionScopeLayouts.Count);
            trace?.Metric("session.scope_allocation_sites", SessionAllocations.Count);
            trace?.Metric("session.type_witnesses.inherited", session.HpirTypes?.Literals.Count ?? 0);
            trace?.Metric("session.type_witnesses.total", program.TypeLiterals.Count);
            TypeRef[] witnesses = sessionTypeWitnesses!.ToArray();
            var textSlots = TextSlots();
            var snapshots = SessionSnapshots();
            trace?.Metric("session.vector_layouts", snapshots.Count);
            var sessionIds = displaysFutures ? SessionLayoutsWithoutPresentedScope() : layoutIds;
            session.HpirLayouts = new(program.Layouts, sessionIds, TextLayoutIndex, aliasLayout, sessionPathLayout,
                sessionScopeLayouts, textSlots, snapshots);
            session.HpirTypes = new(program.TypeLiterals, typeIds, witnesses);
            bool startsProcesses = program.Imports.Any(import => import.Service is HpirService.ProcessStart or HpirService.ProcessSpawn);
            trace?.Metric("session.process_owner", startsProcesses ? 1 : 0);
            trace?.Metric("session.path_origins.relocations", sessionPathRelocations?.Count ?? 0);
            trace?.Metric("session.path_origins.prepared", preparedPathOrigins?.Count ?? 0);
            return new(program, session.Number, kind, sessionCellRelocations ?? new Dictionary<int, HpirSessionCell>())
            {
                Owner = session.Owner, Witnesses = witnesses, ReturnBytes = resultBytes, ResultUnion = resultUnion,
                ScopeLayouts = sessionScopeLayouts, StartsProcesses = startsProcesses, TextSlots = textSlots,
                StreamsRange = graph.SessionStreamsRange, DisplaysFutureScope = displaysFutures, SnapshotLayouts = snapshots,
                PathRelocations = sessionPathRelocations, PreparedPathOrigins = preparedPathOrigins
            };
        }
    }
}
