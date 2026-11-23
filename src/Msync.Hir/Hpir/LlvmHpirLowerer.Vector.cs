using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmValue, LlvmSnapshotCarrier?> vectorShapes = [];
        private readonly Dictionary<(LlvmSnapshotCarrier Shape, int Count), int> vectorAllocators = [];

        internal bool IsVectorConstructor(LlvmFunction target) =>
            graph.Constructors.TryGetValue(target, out var kind) && kind == NativeKind.Range;

        internal bool HasCompletionVectors => graph.CompletionVectors is { Count: > 0 };
        internal bool IsCompletionVector(LlvmFunction target) => graph.CompletionVectors?.Contains(target) == true;

        internal LlvmSnapshotCarrier? VectorShape(LlvmValue value) =>
            SnapshotShape(value) is { Completion: false } ready ? ready : null;

        internal LlvmSnapshotCarrier? SnapshotShape(LlvmValue value)
        {
            if (vectorShapes.TryGetValue(value, out var known)) return known;
            vectorShapes.Add(value, null);
            var shape = VectorProducer(value) ?? ForwardedVector(value);
            vectorShapes[value] = shape;
            return shape;
        }

        private LlvmSnapshotCarrier? VectorProducer(LlvmValue value)
        {
            if (RegexGroupsElement(value) is { } group) return new(group, false);
            if (value is not LlvmCallInstruction { CalledFunction: { } target } call) return null;
            if (SessionSnapshotRead(call) is { Callback: null } retained) return retained.Carrier;
            if (ReversedSnapshot(call) is { } reversed) return reversed;
            if (TextVectorOrder(call) is not null) return VectorShape(call.GetArgument(0));
            if (ConcatAlias(call) is >= 0 and var alias) return SnapshotShape(call.GetArgument(alias));
            if (IsEmptyRange(call)) return new(LlvmStructType.Get(value.Context), true);
            if (EmptySnapshotTransform(call)) return new(LlvmStructType.Get(value.Context), true);
            if (TextChunkAppend(call)) return new(call.GetArgument(1).Type, false);
            if (MaterializedElement(call) is { } mapped) return new(mapped, false);
            if (call.ArgumentCount == 1 && NativeRangeCall(call, "ranges.materialize"))
            {
                if (SelectedCarrier(call) is { } snapshot) return snapshot;
                var sequence = call.GetArgument(0);
                if (IsNumericPlan(sequence)) return new(value.Context.GetPrimitiveType(LlvmTypeId.DoubleTyID)!, false);
                if (SortShape(sequence) is { } sorted) return new(sorted.Element, false);
                if (FilterShape(sequence) is { } filtered && IsWordFilter(filtered)) return new(filtered.Item, filtered.Empty);
                return ConcatShape(sequence) is { } concat ? new(concat.Element, false) : SnapshotShape(sequence);
            }
            HpirRangeBound window = VectorWindow(call);
            if (window != HpirRangeBound.None && (SelectedCarrier(call) ?? SnapshotShape(call.GetArgument(0))) is { } input)
                return input with { Empty = input.Empty || window == HpirRangeBound.Take && ZeroVectorLimit(call) };
            if (!IsVectorConstructor(target)) return null;
            if (call.ArgumentCount == 0) return new(LlvmStructType.Get(value.Context), true);
            LlvmType element = call.GetArgument(0).Type;
            return call.Arguments.All(argument => argument.Type == element) ? new(element, false, IsCompletionVector(target)) : null;
        }

        private LlvmSnapshotCarrier? ForwardedVector(LlvmValue value)
        {
            if (RangeInputs(value) is not { Count: > 0 } incoming) return null;
            LlvmSnapshotCarrier? shape = null;
            foreach (var input in incoming)
            {
                if (SnapshotShape(input) is not { } next) return null;
                if (shape is null || shape.Value.Empty) shape = next;
                else if (!next.Empty && shape.Value != next) return null;
            }
            return shape;
        }

        internal int ElementBytes(LlvmType element) => checked((int)Data.GetTypeAllocSize(element).GetFixedValue());

        internal int VectorStride(LlvmSnapshotCarrier shape) => shape.Completion ? SnapshotCompletionStride : ElementBytes(shape.Element);

        internal int VectorAllocator(LlvmSnapshotCarrier shape, int count)
        {
            if (vectorAllocators.TryGetValue((shape, count), out int id)) return id;
            int stride = VectorStride(shape);
            int extent = checked(HpirTransformImports.VectorHeaderBytes + stride * count);
            var pointers = Leaves(shape.Element).Where(leaf => IsGuestPointer(leaf.Type)).ToArray();
            var borrows = new int[checked(pointers.Length * count)];
            int next = 0;
            for (int item = 0; item < count; ++item)
                foreach (var leaf in pointers) borrows[next++] = checked(HpirTransformImports.VectorHeaderBytes + stride * item + leaf.Offset);
            int layout = Layouts.Count;
            int[] completions = shape.Completion
                ? Enumerable.Range(0, count).Select(item => HpirTransformImports.VectorHeaderBytes + stride * item + SnapshotCompletionOffset).ToArray()
                : [];
            Layouts.Add(new(extent, 8, completions) { BorrowOffsets = borrows });
            id = Imports.Count;
            Imports.Add(new(HpirService.AllocateObject, layout, HpirWidth.U64, []));
            vectorAllocators.Add((shape, count), id);
            return id;
        }
    }

    private sealed partial class Body
    {
        private readonly record struct VectorCursor(int Offset, LlvmSnapshotCarrier Shape, LlvmValue Owner);
        private readonly Dictionary<LlvmCallInstruction, VectorCursor> vectorCursors = [];
        private int vectorAllocations, vectorElements, vectorNexts, vectorCloseElisions, completionVectorElements;

        private bool TryVectorRange(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (geometry.IsVectorConstructor(target) && geometry.SnapshotShape(call) is { } shape)
            {
                ConstructVector(call, shape);
                return true;
            }
            if (!geometry.IteratorOperation(target, out var operation)) return false;
            if (operation == LlvmIteratorOperation.Open && AccessSnapshot(call) is { } item)
            {
                OpenVector(call, item);
                return true;
            }
            if (call.ArgumentCount == 0 || call.GetArgument(0) is not LlvmCallInstruction cursor
                || !vectorCursors.TryGetValue(cursor, out var vector)) return false;
            if (operation == LlvmIteratorOperation.Next)
            {
                NextVector(call, vector);
                return true;
            }
            if (operation != LlvmIteratorOperation.Close) return false;
            ++vectorCloseElisions;
            return true;
        }

        private void ConstructVector(LlvmCallInstruction call, LlvmSnapshotCarrier shape)
        {
            int count = call.ArgumentCount;
            int vector = Location(call);
            int import = geometry.VectorAllocator(shape, count);
            geometry.RememberSessionVector(call, shape, import);
            EmitCall(call, ~import, vector, []);
            Add(new(HpirOp.ConstU64, ~vector, Immediate: count), call.Source);
            if (count != 0)
            {
                // The payload belongs to this same allocation; data is not a GC edge.
                int data = Reserve(8, 8);
                Add(new(HpirOp.OffsetPointer, data, vector, Immediate: HpirTransformImports.VectorHeaderBytes), call.Source);
                Copy(HpirWidth.U64, Indirect(vector, HpirTransformImports.VectorData, call), data, call.Source);
            }
            int stride = geometry.VectorStride(shape);
            var leaves = geometry.Leaves(shape.Element);
            for (int item = 0; item < count; ++item)
            {
                LlvmValue value = call.GetArgument(item);
                int offset = checked(HpirTransformImports.VectorHeaderBytes + stride * item);
                if (shape.Completion)
                {
                    WriteCompletionVectorItem(call, vector, offset, value);
                    continue;
                }
                foreach (var leaf in leaves)
                    Copy(Width(leaf.Type), Indirect(vector, checked(offset + leaf.Offset), call),
                        LeafLocation(value, leaf.Offset), call.Source);
            }
            if (shape.Completion) completionVectorElements += count;
            ++vectorAllocations;
            vectorElements += count;
        }

        private void OpenVector(LlvmCallInstruction call, LlvmSnapshotCarrier shape)
        {
            LlvmValue owner = call.GetArgument(0);
            if (shape.Empty)
            {
                vectorCursors.Add(call, new(-1, shape, owner));
                return;
            }
            int vector = Location(owner);
            int cursor = Reserve(24, 8);
            Add(new(HpirOp.ConstU64, cursor), call.Source);
            Copy(HpirWidth.U64, cursor + 8, ~vector, call.Source);
            Copy(HpirWidth.U64, cursor + 16, Indirect(vector, HpirTransformImports.VectorData, call), call.Source);
            vectorCursors.Add(call, new(cursor, shape, owner));
        }

        private void NextVector(LlvmCallInstruction call, VectorCursor cursor)
        {
            var source = call.Source;
            ++vectorNexts;
            if (cursor.Offset < 0)
            {
                Add(new(HpirOp.ConstU8, Location(call)), source);
                return;
            }
            int index = cursor.Offset, length = index + 8, data = index + 16;
            int available = Location(call);
            Add(new(HpirOp.LtU64, available, index, length), source);
            int branch = code.Count;
            Add(new(HpirOp.Branch, A: available, B: branch + 1), source);

            int address = Reserve(8, 8), increment = Reserve(8, 8);
            int stride = geometry.VectorStride(cursor.Shape);
            if (stride != 0)
            {
                Add(new(HpirOp.ConstU64, increment, Immediate: stride), source);
                Add(new(HpirOp.MulU64, address, index, increment), source);
                Add(new(HpirOp.AddPointer, address, data, address), source);
                CopyVectorItem(call, cursor.Shape.Element, address);
                if (cursor.Shape.Completion)
                {
                    if (call.GetArgument(1) is not LlvmAllocaInstruction item || !frameCompletions.TryGetValue(item, out int completion))
                        throw Error("iterator lacks its selected scalar completion lane", call);
                    Copy(HpirWidth.U64, completion, Indirect(address, SnapshotCompletionOffset, call), source);
                }
            }
            Add(new(HpirOp.ConstU64, increment, Immediate: 1), source);
            Add(new(HpirOp.AddU64, index, index, increment), source);
            code[branch] = code[branch] with { D = code.Count };
        }

        private void CopyVectorItem(LlvmCallInstruction call, LlvmType element, int address)
        {
            LlvmValue output = call.GetArgument(1);
            foreach (var leaf in geometry.Leaves(element))
            {
                var width = Width(leaf.Type);
                int snapshot = Reserve((int)width, (int)width);
                Copy(width, snapshot, Indirect(address, leaf.Offset, call), call.Source);
                Copy(width, Memory(output, leaf.Offset, call), snapshot, call.Source);
            }
        }

        private void ReportVectors(CompilationTrace? trace)
        {
            trace?.Metric("vectors.allocations", vectorAllocations);
            trace?.Metric("vectors.elements", vectorElements);
            trace?.Metric("vectors.completion_elements", completionVectorElements);
            trace?.Metric("future.snapshot_items", completionItemCount);
            trace?.Metric("vectors.cursor_nexts", vectorNexts);
            trace?.Metric("vectors.cursor_closes_elided", vectorCloseElisions);
        }
    }
}
