using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal sealed record FilterRecipe(LlvmFunction Predicate, LlvmType Item, bool Empty, bool Bounded = false);

        internal bool IsWordFilter(FilterRecipe shape) => shape.Item.IsDoubleTy() || IsGuestPointer(shape.Item);
        private Dictionary<LlvmValue, FilterRecipe?>? filters;

        internal FilterRecipe? FilterShape(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            filters ??= [];
            if (filters.TryGetValue(value, out var seen)) return seen;
            filters.Add(value, null);
            if (value is LlvmCallInstruction call)
            {
                if (NativeRangeCall(call, "ranges.filter")) return filters[value] = SnapshotFilter(call);
                if (IsTransformTake(call) && FilterShape(call.GetArgument(0)) is { } bounded)
                    return filters[value] = bounded with { Bounded = true };
            }
            if (RangeInputs(value) is not { Count: > 0 } inputs || FilterShape(inputs[0]) is not { } first)
                return null;
            for (int i = 1; i < inputs.Count; ++i)
                if (FilterShape(inputs[i]) != first) return null;
            return filters[value] = first;
        }

        private FilterRecipe? SnapshotFilter(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || (SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0))) is not { } vector
                || UnaryCallback(call.GetArgument(1)) is not { } predicate || !predicate.ReturnType.IsIntegerTy(1)) return null;
            LlvmType item = predicate.Arguments[Captures(predicate)].Type;
            if ((!vector.Empty && item != vector.Element) || FutureArguments(predicate)) return null;
            if (IsPathRecord(item)) CheckPathRecord(item, call.Source);
            return new(predicate, item, vector.Empty);
        }
    }

    private sealed partial class Body
    {
        private readonly record struct FilterCursor(LlvmValue Owner, Geometry.FilterRecipe Recipe, int[] Captures,
            VectorTransformCursor Snapshot, int Limit);

        private Dictionary<LlvmCallInstruction, FilterCursor>? filterCursors;
        private Dictionary<int, int>? filterDemands;
        private int filterPlans, filterOpens, filterPulls, filterWaits, filterSnapshots;
        private int pathFilterPlans, pathFilterOpens, pathFilterPulls, filterWindows, filterFronts;

        private bool TryVectorFilter(LlvmCallInstruction call)
        {
            if ((geometry.NativeRangeCall(call, "ranges.filter") || geometry.IsTransformTake(call))
                && geometry.FilterShape(call) is { } selected)
            {
                if (geometry.IsTransformTake(call) && FutureRoot(call.GetArgument(1))) return false;
                EmitFilterPlan(call, selected);
                return true;
            }
            if (call.ArgumentCount == 1 && geometry.VectorAccessFor(call) is VectorAccess.Front or VectorAccess.First
                && geometry.FilterShape(call.GetArgument(0)) is { } front && call.Type == front.Item)
            {
                EmitFilterFront(call, front);
                return true;
            }
            if (call.ArgumentCount == 1 && geometry.NativeRangeCall(call, "ranges.materialize")
                && geometry.FilterShape(call.GetArgument(0)) is { } collect && geometry.IsWordFilter(collect))
            {
                MaterializeFilter(call, collect);
                return true;
            }
            if (call.ArgumentCount == 0 || call.CalledFunction is not { } target || !geometry.IteratorOperation(target, out var iterator))
                return false;
            LlvmValue first = call.GetArgument(0);
            if (iterator == LlvmIteratorOperation.Open && geometry.FilterShape(first) is { } recipe)
            {
                (filterCursors ??= []).Add(call, OpenFilter(call, first, recipe));
                return true;
            }
            if (first is not LlvmCallInstruction opened || filterCursors is not { } cursors || !cursors.TryGetValue(opened, out var cursor))
                return false;
            if (iterator == LlvmIteratorOperation.Close) return true;
            if (iterator != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction output
                || output.AllocatedType != cursor.Recipe.Item) return false;
            PullFilter(call, cursor, storage[output], Location(call));
            return true;
        }

        private void EmitFilterPlan(LlvmCallInstruction call, Geometry.FilterRecipe recipe)
        {
            bool take = geometry.IsTransformTake(call);
            int limit = take ? EmitRangeBound(call, call.GetArgument(1), HpirRangeBound.Take) : Reserve(8, 8);
            int plan = Location(call);
            EmitCall(call, ~geometry.VectorTransformAllocator(), plan, []);
            WriteVectorCallbackPlan(call, Location(call.GetArgument(0)), plan, limit, take);
            if (take) ++filterWindows;
            if (Geometry.IsPathRecord(recipe.Item)) ++pathFilterPlans;
            ++filterPlans;
        }

        private FilterCursor OpenFilter(LlvmCallInstruction call, LlvmValue owner, Geometry.FilterRecipe recipe)
        {
            ++filterOpens;
            if (Geometry.IsPathRecord(recipe.Item)) ++pathFilterOpens;
            if (recipe.Empty) return new(owner, recipe, [], default, -1);
            int plan = Location(owner), snapshot = Reserve(8, 8), remaining = Reserve(8, 8);
            int limit = recipe.Bounded ? Reserve(8, 8) : -1;
            Copy(HpirWidth.U64, snapshot, ~plan, call.Source);
            Copy(HpirWidth.U64, remaining, ~snapshot, call.Source);
            if (limit >= 0) Copy(HpirWidth.U64, limit, Indirect(plan, VectorTransformPlan.Limit, call), call.Source);
            return new(owner, recipe, ReadPlanCaptures(call, plan, VectorTransformPlan.Environment, recipe.Predicate),
                OpenSnapshotWords(call, snapshot, remaining), limit);
        }

        private void EmitFilterFront(LlvmCallInstruction call, Geometry.FilterRecipe recipe)
        {
            var cursor = OpenFilter(call, call.GetArgument(0), recipe);
            int available = Reserve(1, 1);
            int item = Aggregate(call.Type) ? RecordLocation(call) : Location(call);
            PullFilter(call, cursor, item, available);
            int present = CursorBranch(available, call.Source);
            VectorAccessFailure(call, geometry.VectorAccessFor(call), -1);
            LinkCursorBranch(present, code.Count, present + 1);
            ++filterFronts;
        }

        private void MaterializeFilter(LlvmCallInstruction call, Geometry.FilterRecipe recipe)
        {
            int snapshot = Location(call);
            var cursor = OpenFilter(call, call.GetArgument(0), recipe);
            InitializeWordSnapshot(call, snapshot);
            allocatedTransforms.Add(call);
            ++filterSnapshots;
            if (recipe.Empty) return;
            bool reference = geometry.IsGuestPointer(recipe.Item);
            int item = Reserve(8, 8);
            CollectWords(call, snapshot, reference, available =>
            {
                PullFilter(call, cursor, item, available, snapshot);
                return (item, reference ? item : -1);
            }, static (_, _, value) => value);
        }

        private void PullFilter(LlvmCallInstruction site, FilterCursor filter, int item, int available, int snapshot = -1)
        {
            if (filter.Recipe.Empty)
            {
                Add(new(HpirOp.ConstU8, available), site.Source);
                return;
            }
            var cursor = filter.Snapshot;
            int cap = filter.Limit >= 0 ? BranchWhenZero(site, filter.Limit) : -1;
            int scan = code.Count, finished = BranchWhenZero(site, cursor.Remaining), selected = Reserve(8, 8);
            Copy(HpirWidth.U64, selected, cursor.Data, site.Source);
            int stride = geometry.ElementBytes(filter.Recipe.Item);
            if (stride != 0) Add(new(HpirOp.OffsetPointer, cursor.Data, cursor.Data, Immediate: stride), site.Source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), site.Source);
            var leaves = geometry.Leaves(filter.Recipe.Item);
            foreach (var leaf in leaves)
                Copy(Width(leaf.Type), item + leaf.Offset, Indirect(selected, leaf.Offset, site), site.Source);
            PredicateCall(site, filter, item, leaves, available, snapshot);
            int accepted = CursorBranch(available, site.Source);
            LinkCursorBranch(finished, code.Count, finished + 1);
            if (cap >= 0) LinkCursorBranch(cap, code.Count, cap + 1);
            Add(new(HpirOp.ConstU8, available), site.Source);
            if (filter.Limit >= 0)
            {
                int done = code.Count;
                Add(new(HpirOp.Jump), site.Source);
                LinkCursorBranch(accepted, code.Count, scan);
                Add(new(HpirOp.SubU64, filter.Limit, filter.Limit, cursor.One), site.Source);
                code[done] = code[done] with { A = code.Count };
            }
            else LinkCursorBranch(accepted, code.Count, scan);
            if (Geometry.IsPathRecord(filter.Recipe.Item)) ++pathFilterPulls;
            ++filterPulls;
        }

        private void PredicateCall(LlvmCallInstruction site, FilterCursor cursor, int item, Geometry.Leaf[] leaves,
            int result, int snapshot)
        {
            var target = cursor.Recipe.Predicate;
            var arguments = new List<int>(leaves.Length + cursor.Captures.Length + 1);
            bool deferred = geometry.FutureResult(target);
            int pending = -1;
            if (deferred)
            {
                int ready = Reserve(8, 8);
                pending = Reserve(8, 8);
                Add(new(HpirOp.AddressOf, ready, result), site.Source);
                arguments.Add(ready);
            }
            arguments.AddRange(cursor.Captures);
            foreach (var leaf in leaves) arguments.Add(item + leaf.Offset);
            if (snapshot >= 0) transformBufferRoots.Add(code.Count, (snapshot, -1));
            EmitCall(site, ids[target], deferred ? pending : result, arguments.ToArray());
            if (!deferred) return;
            int readyBranch = BranchWhenZero(site, pending);
            (filterDemands ??= []).Add(code.Count, pending);
            if (snapshot >= 0) transformBufferRoots.Add(code.Count, (snapshot, -1));
            EmitCall(site, ~geometry.DemandImport(HpirWidth.U8, HpirFutureInput.Deferred), result, [pending]);
            LinkCursorBranch(readyBranch, code.Count, readyBranch + 1);
            ++filterWaits;
        }

        private void ReportVectorFilters(CompilationTrace? trace)
        {
            trace?.Metric("range.filter.path_plans", pathFilterPlans);
            trace?.Metric("range.filter.path_opens", pathFilterOpens);
            trace?.Metric("range.filter.path_pull_sites", pathFilterPulls);
            trace?.Metric("range.filter.snapshot_plans", filterPlans);
            trace?.Metric("range.filter.snapshot_opens", filterOpens);
            trace?.Metric("range.filter.snapshot_pull_sites", filterPulls);
            trace?.Metric("range.filter.windows", filterWindows);
            trace?.Metric("range.filter.fronts", filterFronts);
            trace?.Metric("range.filter.predicate_demands", filterWaits);
            if (filterSnapshots != 0) trace?.Metric("range.filter.snapshot_materializations", filterSnapshots);
        }
    }
}
