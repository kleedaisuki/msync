using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal sealed record SnapshotSort(LlvmType Element, LlvmFunction Compare);

        private readonly Dictionary<LlvmValue, SnapshotSort?> snapshotSorts = [];
        private int snapshotSortAllocator = -1;

        internal SnapshotSort? SortShape(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (snapshotSorts.TryGetValue(value, out var found)) return found;
            snapshotSorts.Add(value, null);
            var shape = value is LlvmCallInstruction call ? SortProducer(call) : null;
            if (shape is null && RangeInputs(value) is { Count: > 0 } inputs)
            {
                shape = SortShape(inputs[0]);
                if (shape is not null && inputs.Skip(1).Any(input => SortShape(input) != shape)) shape = null;
            }
            return snapshotSorts[value] = shape;
        }

        private SnapshotSort? SortProducer(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !NativeRangeCall(call, "ranges.sort_with")
                || (SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0))) is not { } source
                || call.GetArgument(1) is not LlvmCallInstruction { CalledFunction: { } adapt }
                || Callable(adapt) is not { Operation: LlvmCallableRuntimeOperation.Adapt, SessionSlot: < 0, Targets.Count: 1 } binding)
                return null;
            LlvmFunction target = binding.Targets.Values.First();
            int captures = Captures(target);
            if (!entries.ContainsKey(target) || target.Arguments.Count != captures + 2 || !target.ReturnType.IsIntegerTy(1)
                || FutureArguments(target) || FutureResult(target) || ReductionWork(target) != LlvmRangeCallbackWork.Ready)
                return null;
            var element = target.Arguments[captures].Type;
            return (element.IsDoubleTy() || IsGuestPointer(element)) && element == target.Arguments[captures + 1].Type
                && (source.Empty || source.Element == element) ? new(element, target) : null;
        }

        internal int SnapshotSortAllocator() => snapshotSortAllocator >= 0 ? snapshotSortAllocator
            : snapshotSortAllocator = PhysicalAllocator(AddLayout(new(16, 8, [0]) { BorrowOffsets = [8] }));
    }

    private sealed partial class Body
    {
        private readonly record struct SortBuffers(int Snapshot, int Scratch, int Read, int Write, int Length, int One, int Word);
        private int snapshotSortPlans, snapshotSortMaterializations;

        private bool TrySnapshotSort(LlvmCallInstruction call)
        {
            if (geometry.NativeRangeCall(call, "ranges.sort_with") && geometry.SortShape(call) is not null)
            {
                int plan = Location(call);
                EmitCall(call, ~geometry.SnapshotSortAllocator(), plan, []);
                Copy(HpirWidth.U64, ~plan, Location(call.GetArgument(0)), call.Source);
                Copy(HpirWidth.U64, Indirect(plan, 8, call), LeafLocation(call.GetArgument(1), 8), call.Source);
                allocatedTransforms.Add(call);
                ++snapshotSortPlans;
                return true;
            }
            if (call.ArgumentCount != 1 || !geometry.NativeRangeCall(call, "ranges.materialize")
                || geometry.SortShape(call.GetArgument(0)) is not { } shape) return false;
            MaterializeSort(call, shape);
            return true;
        }

        private void MaterializeSort(LlvmCallInstruction site, Geometry.SnapshotSort shape)
        {
            var source = site.Source;
            int plan = Location(site.GetArgument(0)), output = Location(site);
            int owner = Reserve(8, 8), length = Reserve(8, 8), shortRange = Reserve(1, 1);
            Copy(HpirWidth.U64, owner, ~plan, source);
            Copy(HpirWidth.U64, length, ~owner, source);
            Copy(HpirWidth.U64, output, owner, source);
            int one = FormatImmediate(site, 1);
            Add(new(HpirOp.LeU64, shortRange, length, one), source);
            int trivial = CursorBranch(shortRange, source);

            // Constructing the plan is inert. At observation, keep one
            // retained copy and one scratch buffer for stable merge passes.
            InitializeWordSnapshot(site, output);
            Copy(HpirWidth.U64, ~output, length, source);
            int origin = Indirect(owner, HpirTransformImports.VectorData, site);
            int first = Reserve(8, 8), scratch = Reserve(8, 8);
            int zero = FormatImmediate(site, 0);
            int grow = geometry.TransformImport(HpirService.RangeBufferGrow, reference: geometry.IsGuestPointer(shape.Element));
            transformBufferRoots.Add(code.Count, (output, -1));
            EmitCall(site, ~grow, first, [origin, length, length]);
            Copy(HpirWidth.U64, Indirect(output, HpirTransformImports.VectorData, site), first, source);
            transformBufferRoots.Add(code.Count, (output, -1));
            EmitCall(site, ~grow, scratch, [zero, zero, length]);
            int read = Reserve(8, 8), write = Reserve(8, 8);
            Copy(HpirWidth.U64, read, first, source);
            Copy(HpirWidth.U64, write, scratch, source);
            var buffers = new SortBuffers(output, scratch, read, write, length, one, FormatImmediate(site, sizeof(ulong)));
            EmitSortPasses(site, buffers, shape.Compare, ReadPlanCaptures(site, plan, 8, shape.Compare));
            Copy(HpirWidth.U64, Indirect(output, HpirTransformImports.VectorData, site), read, source);
            LinkCursorBranch(trivial, code.Count, trivial + 1);
            allocatedTransforms.Add(site);
            ++snapshotSortMaterializations;
        }

        private void EmitSortPasses(LlvmInstruction site, SortBuffers buffers, LlvmFunction target, int[] captures)
        {
            var source = site.Source;
            int width = Reserve(8, 8), start = Reserve(8, 8), middle = Reserve(8, 8), end = Reserve(8, 8);
            int test = Reserve(1, 1), swap = Reserve(8, 8);
            Copy(HpirWidth.U64, width, buffers.One, source);
            int pass = code.Count;
            Add(new(HpirOp.ConstU64, start), source);
            int run = code.Count;
            Add(new(HpirOp.AddU64, middle, start, width), source);
            ClampRangeLength(site, middle, buffers.Length);
            Add(new(HpirOp.AddU64, end, middle, width), source);
            ClampRangeLength(site, end, buffers.Length);
            EmitMergeRun(site, buffers, target, captures, start, middle, end);
            Copy(HpirWidth.U64, start, end, source);
            Add(new(HpirOp.LtU64, test, start, buffers.Length), source);
            int anotherRun = CursorBranch(test, source);
            LinkCursorBranch(anotherRun, run, anotherRun + 1);
            Copy(HpirWidth.U64, swap, buffers.Read, source);
            Copy(HpirWidth.U64, buffers.Read, buffers.Write, source);
            Copy(HpirWidth.U64, buffers.Write, swap, source);
            Add(new(HpirOp.AddU64, width, width, width), source);
            Add(new(HpirOp.LtU64, test, width, buffers.Length), source);
            int anotherPass = CursorBranch(test, source);
            LinkCursorBranch(anotherPass, pass, anotherPass + 1);
        }

        private void EmitMergeRun(LlvmInstruction site, SortBuffers buffers, LlvmFunction target,
            int[] captures, int start, int middle, int end)
        {
            var source = site.Source;
            int left = Reserve(8, 8), right = Reserve(8, 8), output = Reserve(8, 8), selected = Reserve(8, 8);
            int a = Reserve(8, 8), b = Reserve(8, 8), test = Reserve(1, 1);
            Copy(HpirWidth.U64, left, start, source);
            Copy(HpirWidth.U64, right, middle, source);
            Copy(HpirWidth.U64, output, start, source);
            int next = code.Count;
            Add(new(HpirOp.EqU64, test, right, end), source);
            int noRight = CursorBranch(test, source);
            Add(new(HpirOp.EqU64, test, left, middle), source);
            int noLeft = CursorBranch(test, source);
            Copy(HpirWidth.U64, a, SortWord(site, buffers.Read, left, buffers.Word), source);
            Copy(HpirWidth.U64, b, SortWord(site, buffers.Read, right, buffers.Word), source);
            transformBufferRoots.Add(code.Count, (buffers.Snapshot, buffers.Scratch));
            EmitCall(site, ids[target], test, [.. captures, b, a]);
            int choose = CursorBranch(test, source);
            int takeLeft = code.Count;
            Copy(HpirWidth.U64, selected, left, source);
            Add(new(HpirOp.AddU64, left, left, buffers.One), source);
            int picked = code.Count;
            Add(new(HpirOp.Jump), source);
            int takeRight = code.Count;
            Copy(HpirWidth.U64, selected, right, source);
            Add(new(HpirOp.AddU64, right, right, buffers.One), source);
            int store = code.Count;
            Copy(HpirWidth.U64, a, SortWord(site, buffers.Read, selected, buffers.Word), source);
            Copy(HpirWidth.U64, SortWord(site, buffers.Write, output, buffers.Word), a, source);
            Add(new(HpirOp.AddU64, output, output, buffers.One), source);
            Add(new(HpirOp.LtU64, test, output, end), source);
            int more = CursorBranch(test, source);
            LinkCursorBranch(more, next, more + 1);
            LinkCursorBranch(noRight, takeLeft, noRight + 1);
            LinkCursorBranch(noLeft, takeRight, noLeft + 1);
            LinkCursorBranch(choose, takeRight, takeLeft);
            code[picked] = code[picked] with { A = store };
        }

        private int SortWord(LlvmInstruction site, int data, int index, int stride)
        {
            int address = Reserve(8, 8);
            Add(new(HpirOp.MulU64, address, index, stride), site.Source);
            Add(new(HpirOp.AddPointer, address, data, address), site.Source);
            return ~address;
        }

        private void ReportSnapshotSorts(CompilationTrace? trace)
        {
            if (snapshotSortPlans != 0) trace?.Metric("range.sort.snapshot_plans", snapshotSortPlans);
            if (snapshotSortMaterializations != 0) trace?.Metric("range.sort.snapshot_materializations", snapshotSortMaterializations);
        }
    }
}
