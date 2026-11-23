using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal sealed record CountedShape(LlvmType Payload, TypeKind ItemKind, int RowLayout = -1, int IndexOffset = 0,
            int ItemOffset = 0, bool HasSnapshot = false)
        {
            internal bool Indexed => RowLayout >= 0;
            internal int PayloadOffset => Indexed ? 16 : 8;
        }

        private readonly Dictionary<LlvmValue, CountedShape?> countedRecipes = [];
        private readonly Dictionary<CountedShape, int> countedAllocators = [];
        private readonly Dictionary<int, int> ordinalAllocators = [];
        private readonly Dictionary<(HpirRangeBound, HpirFutureInput), int> ordinalImports = [];
        private int emptyRangeAllocator = -1;

        internal bool IsEmptyRange(LlvmCallInstruction call) =>
            call.ArgumentCount == 1 && NativeRangeCall(call, "ranges.empty_of");

        internal HpirRangeBound CountedProducer(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !call.GetArgument(1).Type.IsDoubleTy()) return HpirRangeBound.None;
            if (NativeRangeCall(call, "ranges.repeat")) return HpirRangeBound.Repeat;
            return NativeRangeCall(call, "ranges.enumerate_from") ? HpirRangeBound.EnumerateStart : HpirRangeBound.None;
        }

        internal CountedShape? CountedRecipe(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (countedRecipes.TryGetValue(value, out var known)) return known;
            countedRecipes.Add(value, null);
            CountedShape? shape = value is LlvmCallInstruction call ? CountedConstruction(call) : null;
            if (shape is null && RangeInputs(value) is { Count: > 0 } inputs)
            {
                shape = CountedRecipe(inputs[0]);
                if (shape is not null && inputs.Skip(1).Any(input => CountedRecipe(input) != shape)) shape = null;
            }
            return countedRecipes[value] = shape;
        }

        private CountedShape? CountedConstruction(LlvmCallInstruction call)
        {
            var op = CountedProducer(call);
            if (op == HpirRangeBound.Repeat)
                return new(call.GetArgument(0).Type, RepeatItemKind(call));
            if (op != HpirRangeBound.EnumerateStart || IndexInput(call) is not { } input
                || !graph.EnumeratedLayouts.TryGetValue(input.ItemKind, out int row)
                || !structs.TryGetValue(row, out var storage) || storage.Elements[1] != input.Payload) return null;
            return input with { RowLayout = row, IndexOffset = FieldOffset(storage, 0), ItemOffset = FieldOffset(storage, 1) };
        }

        private CountedShape? IndexInput(LlvmCallInstruction origin)
        {
            var input = origin.GetArgument(0);
            if (CountedRecipe(input) is { Indexed: false } repeated) return repeated;
            if ((SelectedSnapshot(origin) ?? VectorShape(input)) is not { } snapshot) return null;
            TypeKind kind = SnapshotKind(origin, 0, snapshot.Element);
            return kind is TypeKind.Number or TypeKind.String or TypeKind.Scope
                && (snapshot.Element.IsDoubleTy() || IsGuestPointer(snapshot.Element))
                ? new(snapshot.Element, kind, HasSnapshot: true) : null;
        }

        private TypeKind RepeatItemKind(LlvmCallInstruction call)
        {
            int logical = call.LogicalType;
            int item = logical >= 0 ? graph.Types[logical].Element : -1;
            return item >= 0 ? graph.Types[item].Kind : TypeKind.Never;
        }

        internal int EmptyRangeAllocator()
        {
            if (emptyRangeAllocator >= 0) return emptyRangeAllocator;
            // The witness sits beyond the usual [length, data] vector header.
            return emptyRangeAllocator = PhysicalAllocator(AddLayout(new(24, 8, [])));
        }

        internal int CountedAllocator(CountedShape shape)
        {
            if (countedAllocators.TryGetValue(shape, out int index)) return index;
            if (shape.HasSnapshot)
                return countedAllocators[shape] = PhysicalAllocator(AddLayout(new(24, 8, [shape.PayloadOffset])));
            int size = checked(shape.PayloadOffset + ElementBytes(shape.Payload));
            int[] borrow = Leaves(shape.Payload).Where(leaf => IsGuestPointer(leaf.Type))
                .Select(leaf => checked(shape.PayloadOffset + leaf.Offset)).ToArray();
            return countedAllocators[shape] = PhysicalAllocator(AddLayout(new(size, 8, []) { BorrowOffsets = borrow }));
        }

        internal int OrdinalItemAllocator(int logical, SourceSpan? source) => PhysicalAllocator(LayoutId(logical, source));

        internal int BoundImport(HpirRangeBound operation, HpirFutureInput mode)
        {
            var key = (operation, mode);
            if (ordinalImports.TryGetValue(key, out int index)) return index;
            index = Imports.Count;
            Imports.Add(HpirTransformImports.Bound(operation, mode));
            return ordinalImports[key] = index;
        }

        private int PhysicalAllocator(int layout)
        {
            if (ordinalAllocators.TryGetValue(layout, out int index)) return index;
            index = Imports.Count;
            Imports.Add(new(HpirService.AllocateObject, layout, HpirWidth.U64, []));
            return ordinalAllocators[layout] = index;
        }
    }

    private sealed partial class Body
    {
        private readonly record struct CountedCursor(Geometry.CountedShape Shape, LlvmValue Owner, int Plan,
            int Remaining, int One, int Index, int IndexStep, int Stalled, int SnapshotData = -1);
        private readonly Dictionary<LlvmCallInstruction, CountedCursor> countedCursors = [];
        private int emptyRangePlans, repeatPlans, indexedPlans, indexedSnapshots, countedPulls, constantRangeBounds;

        private bool TryCountedRange(LlvmCallInstruction call)
        {
            if (geometry.IsEmptyRange(call))
            {
                int plan = Location(call);
                EmitCall(call, ~geometry.EmptyRangeAllocator(), plan, []);
                Copy(HpirWidth.U64, Indirect(plan, 16, call), Location(call.GetArgument(0)), call.Source);
                allocatedTransforms.Add(call);
                ++emptyRangePlans;
                return true;
            }
            HpirRangeBound producer = geometry.CountedProducer(call);
            if (producer != HpirRangeBound.None && geometry.CountedRecipe(call) is { } shape)
            {
                EmitCountedPlan(call, producer, shape);
                return true;
            }
            if (call.CalledFunction is not { } target) return false;
            if (TryCountedObservation(call)) return true;
            if (!geometry.IteratorOperation(target, out var operation)) return false;
            var input = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.CountedRecipe(input) is { } known)
            {
                countedCursors.Add(call, OpenCounted(call, input, Location(input), known));
                return true;
            }
            if (input is not LlvmCallInstruction open || !countedCursors.TryGetValue(open, out var cursor)) return false;
            if (operation == LlvmIteratorOperation.Close) return true;
            if (operation != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction item) return false;
            EmitCountedNext(call, cursor, item);
            return true;
        }

        private bool TryCountedObservation(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 1) return false;
            VectorAccess access = geometry.VectorAccessFor(call);
            if (access is not (VectorAccess.Size or VectorAccess.Empty or VectorAccess.Front)) return false;
            LlvmValue input = call.GetArgument(0);
            if (geometry.CountedRecipe(input) is not { } shape) return false;
            int plan = Location(input);
            if (access == VectorAccess.Empty)
            {
                EmitEmptyLength(call, ~plan);
                return true;
            }
            if (access == VectorAccess.Size)
            {
                if (shape.Indexed) return false;
                Add(new(HpirOp.ConvertI64ToF64, Location(call), ~plan), call.Source);
                return true;
            }
            if (shape.Indexed ? !geometry.IsGuestPointer(call.Type) : call.Type != shape.Payload) return false;
            int available = Reserve(1, 1);
            int output = Aggregate(call.Type)
                ? geometry.Leaves(call.Type).Length == 0 ? -1 : RecordLocation(call)
                : Location(call);
            PullCounted(call, OpenCounted(call, input, plan, shape), available, output);
            int hasItem = CursorBranch(available, call.Source);
            RangeSourcePoint(call.Source);
            EmitFailure(call, HpirFailure.RangeEmpty);
            LinkCursorBranch(hasItem, code.Count, hasItem + 1);
            return true;
        }

        private void EmitCountedPlan(LlvmCallInstruction call, HpirRangeBound producer, Geometry.CountedShape shape)
        {
            int bound = EmitRangeBound(call, call.GetArgument(1), producer);
            int plan = Location(call);
            LlvmValue input = call.GetArgument(0);
            if (producer == HpirRangeBound.Repeat && FutureRoot(input))
                throw Error("a repeated pending item requires its own physical item carrier", call);
            EmitCall(call, ~geometry.CountedAllocator(shape), plan, []);
            if (producer == HpirRangeBound.Repeat)
            {
                Copy(HpirWidth.U64, ~plan, bound, call.Source);
                foreach (var leaf in geometry.Leaves(shape.Payload))
                    Copy(Width(leaf.Type), Indirect(plan, checked(shape.PayloadOffset + leaf.Offset), call),
                        LeafLocation(input, leaf.Offset), call.Source);
                ++repeatPlans;
            }
            else
            {
                int source = Location(input);
                Copy(HpirWidth.U64, ~plan, ~source, call.Source);
                Copy(HpirWidth.U64, Indirect(plan, sizeof(ulong), call), bound, call.Source);
                if (shape.HasSnapshot)
                {
                    Copy(HpirWidth.U64, Indirect(plan, shape.PayloadOffset, call), source, call.Source);
                    ++indexedSnapshots;
                }
                else CopyRangePayload(call, shape.Payload, source, sizeof(ulong), plan, shape.PayloadOffset, indirect: true);
                ++indexedPlans;
            }
            allocatedTransforms.Add(call);
        }

        private int EmitRangeBound(LlvmInstruction site, LlvmValue value, HpirRangeBound operation)
        {
            int result = Reserve(8, 8);
            if (value is LlvmConstantFP literal)
            {
                ulong bits = literal.Value.BitcastToAPInt().GetZExtValue();
                double number = BitConverter.UInt64BitsToDouble(bits);
                ulong constant = operation == HpirRangeBound.EnumerateStart
                    ? double.IsFinite(number) ? bits : HpirOrdinal.Invalid
                    : HpirOrdinal.FromNumber(number);
                if (constant != HpirOrdinal.Invalid)
                {
                    Add(new(HpirOp.ConstU64, result, Immediate: (long)constant), site.Source);
                    ++constantRangeBounds;
                    return result;
                }
            }
            HpirFutureInput mode = FutureInput(value);
            var args = new List<int>(2);
            AddFutureInput(args, value);
            RangeSourcePoint(site.Source);
            EmitCall(site, ~geometry.BoundImport(operation, mode), result, args.ToArray(),
                safepoint: mode != HpirFutureInput.Ready);
            return result;
        }

        private void CopyRangePayload(LlvmInstruction site, LlvmType type, int from, int inputOffset,
            int to, int outputOffset, bool indirect)
        {
            int snapshot = -1;
            foreach (var leaf in geometry.Leaves(type))
            {
                HpirWidth width = Width(leaf.Type);
                if (snapshot < 0) snapshot = Reserve(8, 8);
                Copy(width, snapshot, Indirect(from, checked(inputOffset + leaf.Offset), site), site.Source);
                int offset = checked(outputOffset + leaf.Offset);
                Copy(width, indirect ? Indirect(to, offset, site) : checked(to + offset), snapshot, site.Source);
            }
        }

        private CountedCursor OpenCounted(LlvmInstruction site, LlvmValue owner, int plan, Geometry.CountedShape shape)
        {
            int remaining = Reserve(8, 8), one = Reserve(8, 8);
            Copy(HpirWidth.U64, remaining, ~plan, site.Source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), site.Source);
            if (!shape.Indexed) return new(shape, owner, plan, remaining, one, -1, -1, -1);
            int index = Reserve(8, 8), step = Reserve(8, 8), stalled = Reserve(1, 1);
            Copy(HpirWidth.U64, index, Indirect(plan, sizeof(ulong), site), site.Source);
            Add(new(HpirOp.ConstU64, step, Immediate: BitConverter.DoubleToInt64Bits(1)), site.Source);
            Add(new(HpirOp.ConstU8, stalled), site.Source);
            int data = -1;
            if (shape.HasSnapshot)
            {
                int snapshot = Reserve(8, 8);
                Copy(HpirWidth.U64, snapshot, Indirect(plan, shape.PayloadOffset, site), site.Source);
                data = OpenSnapshotWords(site, snapshot, remaining).Data;
            }
            return new(shape, owner, plan, remaining, one, index, step, stalled, data);
        }

        private void EmitCountedNext(LlvmCallInstruction call, CountedCursor cursor, LlvmAllocaInstruction output)
        {
            int available = Location(call);
            if (cursor.Shape.Indexed)
            {
                if (!geometry.IsGuestPointer(output.AllocatedType)) throw Error("an indexed Range requires its producer's row item", call);
                PullCounted(call, cursor, available, Memory(output));
                return;
            }
            if (output.AllocatedType != cursor.Shape.Payload) throw Error("a repeated Range requires its typed item", call);
            if (!frameAddresses.TryGetValue(output, out int item))
                throw Error("a repeated Range requires ordinary frame item storage", call);
            PullCounted(call, cursor, available, item);
        }

        private void PullCounted(LlvmInstruction site, CountedCursor cursor, int available, int output, int liveVector = -1)
        {
            if (cursor.Shape.Indexed)
            {
                int stalled = CursorBranch(cursor.Stalled, site.Source);
                RangeSourcePoint(site.Source);
                EmitFailure(site, HpirFailure.RangeIndexStalled);
                LinkCursorBranch(stalled, stalled + 1, code.Count);
            }
            int finish = BranchWhenZero(site, cursor.Remaining);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), site.Source);
            if (cursor.Shape.Indexed) PublishIndexRow(site, cursor, output, liveVector);
            else CopyRangePayload(site, cursor.Shape.Payload, cursor.Plan, cursor.Shape.PayloadOffset, output, 0, indirect: false);
            RangeSourcePoint(site.Source);
            Add(new(HpirOp.ConstU8, available, Immediate: 1), site.Source);
            int yielded = code.Count;
            Add(new(HpirOp.Jump), site.Source);
            LinkCursorBranch(finish, code.Count, finish + 1);
            RangeSourcePoint(site.Source);
            Add(new(HpirOp.ConstU8, available), site.Source);
            code[yielded] = code[yielded] with { A = code.Count };
            ++countedPulls;
        }

        private void PublishIndexRow(LlvmInstruction site, CountedCursor cursor, int output, int liveVector)
        {
            int row = Reserve(8, 8);
            if (liveVector >= 0) transformBufferRoots.Add(code.Count, (liveVector, -1));
            RangeSourcePoint(site.Source);
            EmitCall(site, ~geometry.OrdinalItemAllocator(cursor.Shape.RowLayout, site.Source), row, []);
            Copy(HpirWidth.U64, Indirect(row, cursor.Shape.IndexOffset, site), cursor.Index, site.Source);
            if (cursor.SnapshotData >= 0)
            {
                Copy(HpirWidth.U64, Indirect(row, cursor.Shape.ItemOffset, site), ~cursor.SnapshotData, site.Source);
                Add(new(HpirOp.OffsetPointer, cursor.SnapshotData, cursor.SnapshotData, Immediate: sizeof(ulong)), site.Source);
            }
            else CopyRangePayload(site, cursor.Shape.Payload, cursor.Plan, cursor.Shape.PayloadOffset, row,
                cursor.Shape.ItemOffset, indirect: true);
            int next = Reserve(8, 8);
            Add(new(HpirOp.AddF64, next, cursor.Index, cursor.IndexStep), site.Source);
            Add(new(HpirOp.EqF64, cursor.Stalled, next, cursor.Index), site.Source);
            Copy(HpirWidth.U64, cursor.Index, next, site.Source);
            Copy(HpirWidth.U64, output, row, site.Source);
        }

        private void ReportCountedRanges(CompilationTrace? trace)
        {
            trace?.Metric("range.empty_snapshots", emptyRangePlans);
            trace?.Metric("range.repeat_plans", repeatPlans);
            trace?.Metric("range.enumeration_plans", indexedPlans);
            trace?.Metric("range.enumeration_snapshot_plans", indexedSnapshots);
            trace?.Metric("range.counted_pull_sites", countedPulls);
            trace?.Metric("range.bound_constants", constantRangeBounds);
        }
    }
}
