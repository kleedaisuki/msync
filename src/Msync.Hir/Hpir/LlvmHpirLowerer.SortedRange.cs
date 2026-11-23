using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private static class SortedPlan
    {
        internal const int Left = 0, Right = 8, Limit = 16, Environment = 24, Bytes = 32;
    }

    private enum SortedOperation : byte { Merge, Union, Intersection, Difference, SymmetricDifference }

    private sealed partial class Geometry
    {
        internal sealed record SortedRecipe(SortedOperation Operation, ZipSource Left, ZipSource Right,
            LlvmType Element, LlvmFunction? Compare = null, bool Empty = false)
        {
            internal bool TakeLeft => Operation != SortedOperation.Intersection;
            internal bool TakeRight => Operation is SortedOperation.Merge or SortedOperation.Union or SortedOperation.SymmetricDifference;
            internal bool TakeEqual => Operation is SortedOperation.Union or SortedOperation.Intersection;
        }

        private readonly Dictionary<LlvmValue, SortedRecipe?> sortedRecipes = [];
        private int sortedAllocator = -1;

        internal bool OwnsSortedPlan(LlvmCallInstruction call) => IsTransformTake(call) || SortedOperationFor(call) is not null;

        private SortedOperation? SortedOperationFor(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !graph.NativeCalls.TryGetValue(target, out var name)) return null;
            return name switch
            {
                "ranges.merge" or "ranges.merge_with" => SortedOperation.Merge,
                "ranges.set_union" => SortedOperation.Union,
                "ranges.set_intersection" => SortedOperation.Intersection,
                "ranges.set_difference" => SortedOperation.Difference,
                "ranges.set_symmetric_difference" => SortedOperation.SymmetricDifference,
                _ => null
            };
        }

        internal SortedRecipe? SortedShape(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (sortedRecipes.TryGetValue(value, out var known)) return known;
            sortedRecipes.Add(value, null);
            var shape = value is LlvmCallInstruction call ? SortedProducer(call) : null;
            if (shape is null && RangeInputs(value) is { Count: > 0 } inputs)
            {
                shape = SortedShape(inputs[0]);
                if (shape is not null && inputs.Skip(1).Any(input => SortedShape(input) != shape)) shape = null;
            }
            return sortedRecipes[value] = shape;
        }

        private SortedRecipe? SortedProducer(LlvmCallInstruction call)
        {
            if (IsTransformTake(call))
                return SortedShape(call.GetArgument(0)) is { } shape ? shape with { Empty = shape.Empty || ZeroVectorLimit(call) } : null;
            if (SortedOperationFor(call) is not { } operation || call.ArgumentCount is not (2 or 3)
                || ZipInput(call, 0) is not { } left || ZipInput(call, 1) is not { } right || left.Kind != right.Kind) return null;
            if (left.Map is { Vector: null } || right.Map is { Vector: null }) return null;
            LlvmType element = left.Empty ? right.Element : left.Element;
            if (!left.Empty && !right.Empty && right.Element != element) return null;
            if (call.ArgumentCount == 2)
                return left.Kind == TypeKind.Number && element.IsDoubleTy() ? new(operation, left, right, element) : null;
            var arg = call.GetArgument(2);
            if (arg is not LlvmCallInstruction { CalledFunction: { } adapt }
                || Callable(adapt) is not { Operation: LlvmCallableRuntimeOperation.Adapt, SessionSlot: < 0, Targets.Count: 1 } binding)
                return null;
            var compare = binding.Targets.Values.First();
            int captures = Captures(compare);
            return entries.ContainsKey(compare) && compare.Arguments.Count == captures + 2 && compare.ReturnType.IsIntegerTy(1)
                && compare.Arguments[captures].Type == element && compare.Arguments[captures + 1].Type == element
                && !FutureArguments(compare) && !FutureResult(compare) && ReductionWork(compare) == LlvmRangeCallbackWork.Ready
                ? new(operation, left, right, element, compare) : null;
        }

        internal int SortedAllocator()
        {
            if (sortedAllocator >= 0) return sortedAllocator;
            return sortedAllocator = PhysicalAllocator(AddLayout(new(SortedPlan.Bytes, 8, [SortedPlan.Left, SortedPlan.Right])
            { BorrowOffsets = [SortedPlan.Environment] }));
        }
    }

    private sealed partial class Body
    {
        private readonly record struct SortedSide(ZipSideCursor Input, int Refill, int HasValue, int Value);
        private sealed record SortedCursor(Geometry.SortedRecipe Shape, LlvmValue Owner, int Remaining, int One,
            SortedSide Left, SortedSide Right, int[] Captures);
        private readonly Dictionary<LlvmCallInstruction, SortedCursor> sortedCursors = [];
        private int sortedPlans, sortedOpens, sortedPulls, sortedGuestComparisons, naturalNumberLess;

        private bool TrySortedRange(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (geometry.NativeRangeCall(call, "ranges.less") && call.ArgumentCount == 2 && call.Type.IsIntegerTy(1)
                && call.GetArgument(0) is { } left && call.GetArgument(1) is { } right
                && left.Type.IsDoubleTy() && right.Type.IsDoubleTy() && !FutureRoot(left) && !FutureRoot(right))
            {
                EmitNaturalNumberLess(call, Location(call), Location(left), Location(right));
                ++naturalNumberLess;
                return true;
            }
            if (call.ArgumentCount is (2 or 3) && geometry.IsGuestPointer(call.Type)
                && geometry.OwnsSortedPlan(call) && geometry.SortedShape(call) is not null)
            {
                EmitSortedPlan(call);
                return true;
            }
            if (geometry.IteratorOperation(target, out var operation)) return EmitSortedIterator(call, operation);
            if (call.ArgumentCount != 1) return false;
            VectorAccess access = geometry.VectorAccessFor(call);
            if (access is not (VectorAccess.Front or VectorAccess.First)
                || geometry.SortedShape(call.GetArgument(0)) is not { } shape || shape.Element != call.Type) return false;
            int available = Reserve(1, 1);
            PullSorted(call, OpenSorted(call, call.GetArgument(0), Location(call.GetArgument(0)), shape), available, Location(call));
            int hadItem = CursorBranch(available, call.Source);
            RangeSourcePoint(call.Source);
            VectorAccessFailure(call, access, -1);
            LinkCursorBranch(hadItem, code.Count, hadItem + 1);
            return true;
        }

        private void EmitSortedPlan(LlvmCallInstruction call)
        {
            bool take = geometry.IsTransformTake(call);
            int limit = take ? EmitRangeBound(call, call.GetArgument(1), HpirRangeBound.Take) : Reserve(8, 8);
            int output = Location(call), input = Location(call.GetArgument(0));
            EmitCall(call, ~geometry.SortedAllocator(), output, []);
            if (take)
            {
                int lane = Reserve(8, 8);
                CopyPlanWord(call, output, input, SortedPlan.Left, lane);
                CopyPlanWord(call, output, input, SortedPlan.Right, lane);
                CopyPlanWord(call, output, input, SortedPlan.Environment, lane);
                ClampRangeLength(call, limit, Indirect(input, SortedPlan.Limit, call));
            }
            else
            {
                Copy(HpirWidth.U64, ~output, input, call.Source);
                Copy(HpirWidth.U64, Indirect(output, SortedPlan.Right, call), Location(call.GetArgument(1)), call.Source);
                if (call.ArgumentCount == 3)
                    Copy(HpirWidth.U64, Indirect(output, SortedPlan.Environment, call), LeafLocation(call.GetArgument(2), 8), call.Source);
                Add(new(HpirOp.ConstU64, limit, Immediate: -1), call.Source);
            }
            Copy(HpirWidth.U64, Indirect(output, SortedPlan.Limit, call), limit, call.Source);
            allocatedTransforms.Add(call);
            ++sortedPlans;
        }

        private bool EmitSortedIterator(LlvmCallInstruction call, LlvmIteratorOperation operation)
        {
            var input = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.SortedShape(input) is { } shape)
            {
                sortedCursors.Add(call, OpenSorted(call, input, Location(input), shape));
                return true;
            }
            if (input is not LlvmCallInstruction open || !sortedCursors.TryGetValue(open, out var cursor)) return false;
            if (operation == LlvmIteratorOperation.Close) return true;
            if (operation != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction item
                || cursor.Shape.Element != item.AllocatedType) return false;
            PullSorted(call, cursor, Location(call), Memory(item));
            return true;
        }

        private SortedCursor OpenSorted(LlvmInstruction site, LlvmValue owner, int plan, Geometry.SortedRecipe shape)
        {
            ++sortedOpens;
            if (shape.Empty) return new(shape, owner, -1, -1, default, default, []);
            int remaining = Reserve(8, 8), one = Reserve(8, 8), left = Reserve(8, 8), right = Reserve(8, 8);
            Copy(HpirWidth.U64, remaining, Indirect(plan, SortedPlan.Limit, site), site.Source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), site.Source);
            Copy(HpirWidth.U64, left, ~plan, site.Source);
            Copy(HpirWidth.U64, right, Indirect(plan, SortedPlan.Right, site), site.Source);
            return new(shape, owner, remaining, one, OpenSortedSide(site, owner, left, shape.Left),
                OpenSortedSide(site, owner, right, shape.Right),
                shape.Compare is { } compare ? ReadPlanCaptures(site, plan, SortedPlan.Environment, compare) : []);
        }

        private SortedSide OpenSortedSide(LlvmInstruction site, LlvmValue owner, int source, Geometry.ZipSource shape)
        {
            int refill = Reserve(1, 1), has = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, refill, Immediate: 1), site.Source);
            return new(OpenZipSide(site, owner, source, shape), refill, has, Reserve(8, 8));
        }

        private void RefillSorted(LlvmInstruction site, SortedSide side, int snapshot)
        {
            int refill = CursorBranch(side.Refill, site.Source);
            PullZipSide(site, side.Input, side.HasValue, side.Value, snapshot);
            Add(new(HpirOp.ConstU8, side.Refill), site.Source);
            LinkCursorBranch(refill, refill + 1, code.Count);
        }

        private void PullSorted(LlvmInstruction site, SortedCursor cursor, int available, int output, int snapshot = -1)
        {
            ++sortedPulls;
            var source = site.Source;
            if (cursor.Shape.Empty)
            {
                Add(new(HpirOp.ConstU8, available), source);
                return;
            }

            int atLimit = BranchWhenZero(site, cursor.Remaining);
            var left = cursor.Left;
            var right = cursor.Right;
            var shape = cursor.Shape;
            int loop = code.Count;
            RefillSorted(site, left, snapshot);
            int noLeftStop = shape.TakeRight ? -1 : CursorBranch(left.HasValue, source);
            RefillSorted(site, right, snapshot);
            int hasLeft = shape.TakeRight ? CursorBranch(left.HasValue, source) : -1;
            int hasRight = CursorBranch(right.HasValue, source);
            int rightLess = CursorBranch(SortedLess(site, cursor, right.Value, left.Value, snapshot), source);
            int leftLess = shape.Operation == SortedOperation.Merge ? -1
                : CursorBranch(SortedLess(site, cursor, left.Value, right.Value, snapshot), source);

            int equal = code.Count;
            int equalYield = -1;
            if (leftLess >= 0)
            {
                Add(new(HpirOp.ConstU8, left.Refill, Immediate: 1), source);
                Add(new(HpirOp.ConstU8, right.Refill, Immediate: 1), source);
                equalYield = YieldSorted(site, shape.TakeEqual, output, left.Value, loop);
            }
            int takeLeft = code.Count;
            Add(new(HpirOp.ConstU8, left.Refill, Immediate: 1), source);
            int leftYield = YieldSorted(site, shape.TakeLeft, output, left.Value, loop);
            int takeRight = code.Count;
            Add(new(HpirOp.ConstU8, right.Refill, Immediate: 1), source);
            int rightYield = YieldSorted(site, shape.TakeRight, output, right.Value, loop);

            int rightOnly = shape.TakeRight ? CursorBranch(right.HasValue, source) : -1;
            int exhausted = code.Count;
            Add(new(HpirOp.ConstU64, cursor.Remaining), source);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available), source);
            int done = code.Count;
            Add(new(HpirOp.Jump), source);
            int yielded = code.Count;
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available, Immediate: 1), source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), source);
            code[done] = code[done] with { A = code.Count };

            LinkCursorBranch(atLimit, exhausted, atLimit + 1);
            if (noLeftStop >= 0) LinkCursorBranch(noLeftStop, noLeftStop + 1, exhausted);
            if (hasLeft >= 0) LinkCursorBranch(hasLeft, hasLeft + 1, rightOnly);
            LinkCursorBranch(hasRight, hasRight + 1, shape.TakeLeft ? takeLeft : exhausted);
            LinkCursorBranch(rightLess, takeRight, leftLess < 0 ? takeLeft : rightLess + 1);
            if (leftLess >= 0) LinkCursorBranch(leftLess, takeLeft, equal);
            if (rightOnly >= 0) LinkCursorBranch(rightOnly, takeRight, exhausted);
            if (equalYield >= 0) code[equalYield] = code[equalYield] with { A = yielded };
            if (leftYield >= 0) code[leftYield] = code[leftYield] with { A = yielded };
            if (rightYield >= 0) code[rightYield] = code[rightYield] with { A = yielded };
        }

        private int YieldSorted(LlvmInstruction site, bool take, int output, int item, int again)
        {
            if (take) Copy(HpirWidth.U64, output, item, site.Source);
            int jump = code.Count;
            Add(new(HpirOp.Jump, A: take ? 0 : again), site.Source);
            return take ? jump : -1;
        }

        private int SortedLess(LlvmInstruction site, SortedCursor cursor, int a, int b, int snapshot)
        {
            int result = Reserve(1, 1);
            if (cursor.Shape.Compare is { } compare)
            {
                int[] args = [.. cursor.Captures, a, b];
                if (snapshot >= 0) transformBufferRoots.Add(code.Count, (snapshot, -1));
                EmitCall(site, ids[compare], result, args);
                ++sortedGuestComparisons;
            }
            else EmitNaturalNumberLess(site, result, a, b);
            return result;
        }

        private void EmitNaturalNumberLess(LlvmInstruction site, int result, int left, int right)
        {
            // Natural Number ordering also puts NaN before every ordered value.
            int nan = Reserve(1, 1), ordered = Reserve(1, 1);
            Add(new(HpirOp.LtF64, result, left, right), site.Source);
            Add(new(HpirOp.NeF64, nan, left, left), site.Source);
            Add(new(HpirOp.EqF64, ordered, right, right), site.Source);
            Add(new(HpirOp.AndU8, nan, nan, ordered), site.Source);
            Add(new(HpirOp.OrU8, result, result, nan), site.Source);
        }

        private void ReportSortedRanges(CompilationTrace? trace)
        {
            trace?.Metric("range.sorted.plans", sortedPlans);
            trace?.Metric("range.sorted.frame_cursors", sortedOpens);
            trace?.Metric("range.sorted.compiled_pulls", sortedPulls);
            trace?.Metric("range.sorted.guest_compare_sites", sortedGuestComparisons);
            trace?.Metric("range.sorted.natural_number_less_sites", naturalNumberLess);
        }
    }
}
