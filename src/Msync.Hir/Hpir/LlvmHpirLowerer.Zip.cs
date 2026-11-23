using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private static class ZipPlan
    {
        public const int Left = 0, Right = 8, Limit = 16, Bytes = 24;
    }

    private sealed partial class Geometry
    {
        internal sealed record ZipSource(LlvmType Element, TypeKind Kind, bool Empty, TransformRecipe? Map);
        internal sealed record ZipRecipe(ZipSource Left, ZipSource Right, int Layout, int First, int Second, bool Empty);

        private readonly Dictionary<LlvmValue, ZipRecipe?> zipRecipes = [];
        private int zipAllocator = -1;

        internal bool OwnsZipPlan(LlvmCallInstruction call) => IsTransformTake(call)
            || call.ArgumentCount == 2 && NativeRangeCall(call, "ranges.zip");

        internal ZipRecipe? ZipShape(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (zipRecipes.TryGetValue(value, out var known)) return known;
            zipRecipes.Add(value, null);
            var answer = value is LlvmCallInstruction call ? ZipProducer(call) : null;
            if (answer is null && RangeInputs(value) is { Count: > 0 } incoming)
            {
                answer = ZipShape(incoming[0]);
                if (answer is not null && incoming.Skip(1).Any(input => ZipShape(input) != answer)) answer = null;
            }
            return zipRecipes[value] = answer;
        }

        private ZipRecipe? ZipProducer(LlvmCallInstruction call)
        {
            if (!OwnsZipPlan(call)) return null;
            if (IsTransformTake(call))
                return ZipShape(call.GetArgument(0)) is { } selected
                    ? selected with { Empty = selected.Empty || ZeroVectorLimit(call) } : null;
            var left = ZipInput(call, 0);
            var right = ZipInput(call, 1);
            if (left is null || right is null) return null;
            if (!graph.ZippedLayouts.TryGetValue((left.Kind, right.Kind), out int layout)
                || !structs.TryGetValue(layout, out var record)
                || !Compatible(left, record.Elements[0]) || !Compatible(right, record.Elements[1])) return null;
            // Right exhaustion still observes one left pull.
            return new(left, right, layout, FieldOffset(record, 0), FieldOffset(record, 1), left.Empty);
        }

        private ZipSource? ZipInput(LlvmCallInstruction origin, int index)
        {
            LlvmValue value = origin.GetArgument(index);
            if ((SelectedSnapshot(origin, index) ?? VectorShape(value)) is { } vector)
            {
                TypeKind kind = SnapshotKind(origin, index, vector.Element);
                return kind is TypeKind.Number or TypeKind.String or TypeKind.Scope
                    ? new(vector.Element, kind, vector.Empty, null) : null;
            }
            if (TransformShape(value) is { } map && map.Callback.ReturnType.IsDoubleTy() && !FutureResult(map.Callback)
                && ReductionWork(map.Callback) == LlvmRangeCallbackWork.Ready)
                return new(map.Callback.ReturnType, TypeKind.Number, map.Empty, map);
            return null;
        }

        private static bool Compatible(ZipSource source, LlvmType field) => source.Empty || source.Element == field;

        internal int ZipAllocator()
        {
            if (zipAllocator >= 0) return zipAllocator;
            return zipAllocator = PhysicalAllocator(AddLayout(new(ZipPlan.Bytes, 8, [ZipPlan.Left, ZipPlan.Right])));
        }
    }

    private sealed partial class Body
    {
        private readonly record struct ZipSideCursor(VectorTransformCursor Words, TransformCursor? Map);
        private sealed record ZipCursor(Geometry.ZipRecipe Shape, LlvmValue Owner, int Remaining, int One,
            ZipSideCursor Left, ZipSideCursor Right);
        private readonly Dictionary<LlvmCallInstruction, ZipCursor> zipCursors = [];
        private int zipPlans, zipOpens, zipPulls, zipEmptyObservations;

        private bool TryZipRange(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (geometry.OwnsZipPlan(call) && geometry.ZipShape(call) is not null)
            {
                EmitZipPlan(call);
                return true;
            }
            if (geometry.IteratorOperation(target, out var operation)) return EmitZipIterator(call, operation);
            if (call.ArgumentCount != 1) return false;
            VectorAccess access = geometry.VectorAccessFor(call);
            if (access is not (VectorAccess.Front or VectorAccess.Empty or VectorAccess.Size)
                || geometry.ZipShape(call.GetArgument(0)) is not { } shape) return false;
            if (access == VectorAccess.Front && geometry.IsGuestPointer(call.Type))
            {
                FrontZip(call, OpenZip(call, call.GetArgument(0), Location(call.GetArgument(0)), shape));
                return true;
            }
            if (!shape.Empty) return false;
            ++zipEmptyObservations;
            return EmitKnownEmpty(call, access);
        }

        private void EmitZipPlan(LlvmCallInstruction call)
        {
            bool take = geometry.IsTransformTake(call);
            int limit = take ? EmitRangeBound(call, call.GetArgument(1), HpirRangeBound.Take) : Reserve(8, 8);
            int output = Location(call), input = Location(call.GetArgument(0));
            EmitCall(call, ~geometry.ZipAllocator(), output, []);
            if (take)
            {
                int lane = Reserve(8, 8);
                CopyPlanWord(call, output, input, ZipPlan.Left, lane);
                CopyPlanWord(call, output, input, ZipPlan.Right, lane);
                ClampRangeLength(call, limit, Indirect(input, ZipPlan.Limit, call));
            }
            else
            {
                Copy(HpirWidth.U64, ~output, input, call.Source);
                Copy(HpirWidth.U64, Indirect(output, ZipPlan.Right, call), Location(call.GetArgument(1)), call.Source);
                Add(new(HpirOp.ConstU64, limit, Immediate: -1), call.Source);
            }
            Copy(HpirWidth.U64, Indirect(output, ZipPlan.Limit, call), limit, call.Source);
            allocatedTransforms.Add(call);
            ++zipPlans;
        }

        private bool EmitZipIterator(LlvmCallInstruction call, LlvmIteratorOperation operation)
        {
            var input = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.ZipShape(input) is { } shape)
            {
                zipCursors.Add(call, OpenZip(call, input, Location(input), shape));
                return true;
            }
            if (input is not LlvmCallInstruction open || !zipCursors.TryGetValue(open, out var cursor)) return false;
            if (operation == LlvmIteratorOperation.Close) return true;
            if (operation != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction item
                || !geometry.IsGuestPointer(item.AllocatedType)) return false;
            PullZip(call, cursor, Location(call), Memory(item));
            return true;
        }

        private void FrontZip(LlvmCallInstruction call, ZipCursor cursor)
        {
            int available = Reserve(1, 1);
            PullZip(call, cursor, available, Location(call));
            int hadItem = CursorBranch(available, call.Source);
            RangeSourcePoint(call.Source);
            EmitFailure(call, HpirFailure.RangeEmpty);
            LinkCursorBranch(hadItem, code.Count, hadItem + 1);
        }

        private ZipCursor OpenZip(LlvmInstruction site, LlvmValue owner, int plan, Geometry.ZipRecipe shape)
        {
            ++zipOpens;
            if (shape.Empty) return new(shape, owner, -1, -1, default, default);
            int remaining = Reserve(8, 8), one = Reserve(8, 8), left = Reserve(8, 8), right = Reserve(8, 8);
            Copy(HpirWidth.U64, remaining, Indirect(plan, ZipPlan.Limit, site), site.Source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), site.Source);
            Copy(HpirWidth.U64, left, ~plan, site.Source);
            Copy(HpirWidth.U64, right, Indirect(plan, ZipPlan.Right, site), site.Source);
            return new(shape, owner, remaining, one, OpenZipSide(site, owner, left, shape.Left),
                OpenZipSide(site, owner, right, shape.Right));
        }

        private ZipSideCursor OpenZipSide(LlvmInstruction site, LlvmValue owner, int source, Geometry.ZipSource shape)
        {
            if (shape.Map is { } mapped) return new(default, OpenTransform(site, owner, mapped, source));
            int length = Reserve(8, 8);
            Copy(HpirWidth.U64, length, ~source, site.Source);
            return new(OpenSnapshotWords(site, source, length), null);
        }

        private void PullZip(LlvmInstruction site, ZipCursor cursor, int available, int output, int liveVector = -1)
        {
            ++zipPulls;
            var source = site.Source;
            if (cursor.Shape.Empty)
            {
                Add(new(HpirOp.ConstU8, available), source);
                return;
            }
            int left = Reserve(8, 8), right = Reserve(8, 8);
            int atLimit = BranchWhenZero(site, cursor.Remaining);
            PullZipSide(site, cursor.Left, available, left, liveVector);
            int hasLeft = CursorBranch(available, source);
            PullZipSide(site, cursor.Right, available, right, liveVector);
            int hasRight = CursorBranch(available, source);

            if (liveVector >= 0) transformBufferRoots.Add(code.Count, (liveVector, -1));
            RangeSourcePoint(source);
            int row = Reserve(8, 8);
            EmitCall(site, ~geometry.OrdinalItemAllocator(cursor.Shape.Layout, source), row, []);
            Copy(HpirWidth.U64, Indirect(row, cursor.Shape.First, site), left, source);
            Copy(HpirWidth.U64, Indirect(row, cursor.Shape.Second, site), right, source);
            Copy(HpirWidth.U64, output, row, source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), source);
            int done = code.Count;
            Add(new(HpirOp.Jump), source);

            int exhausted = code.Count;
            Add(new(HpirOp.ConstU64, cursor.Remaining), source);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available), source);
            LinkCursorBranch(atLimit, exhausted, atLimit + 1);
            LinkCursorBranch(hasLeft, hasLeft + 1, exhausted);
            LinkCursorBranch(hasRight, hasRight + 1, exhausted);
            code[done] = code[done] with { A = code.Count };
        }

        private void PullZipSide(LlvmInstruction site, ZipSideCursor side, int available, int output, int liveVector)
        {
            if (side.Map is not { } mapped)
            {
                PullVectorTransform(site, side.Words, available, output);
                return;
            }
            if (mapped.Empty)
            {
                Add(new(HpirOp.ConstU8, available), site.Source);
                return;
            }
            int input = PullTransformInput(site, mapped, available, liveVector);
            int hasItem = CursorBranch(available, site.Source);
            CallTransformCallback(site, mapped, input, output, liveVector);
            LinkCursorBranch(hasItem, hasItem + 1, code.Count);
        }

        private void ReportZipRanges(CompilationTrace? trace)
        {
            trace?.Metric("range.zip.plans", zipPlans);
            trace?.Metric("range.zip.frame_cursors", zipOpens);
            trace?.Metric("range.zip.pull_sites", zipPulls);
            trace?.Metric("range.zip.empty_observations", zipEmptyObservations);
        }
    }
}
