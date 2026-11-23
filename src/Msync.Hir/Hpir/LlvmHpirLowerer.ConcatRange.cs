using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal sealed record ConcatRecipe(LlvmType Element, ConcatRecipe? Left, ConcatRecipe? Right);

        private readonly Dictionary<LlvmValue, ConcatRecipe?> concatenations = [];
        private int concatAllocator = -1;

        internal int ConcatAllocator() => concatAllocator >= 0 ? concatAllocator
            : concatAllocator = PhysicalAllocator(AddLayout(new(16, 8, [0, 8])));

        internal int ConcatAlias(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !NativeRangeCall(call, "ranges.concat")) return -1;
            var left = SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0));
            var right = SelectedSnapshot(call, 1) ?? VectorShape(call.GetArgument(1));
            if (right is { Empty: true } && (left.HasValue || ConcatShape(call.GetArgument(0)) is not null)) return 0;
            if (left is { Empty: true } && (right.HasValue || ConcatShape(call.GetArgument(1)) is not null)) return 1;
            return -1;
        }

        internal ConcatRecipe? ConcatShape(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (concatenations.TryGetValue(value, out var known)) return known;
            concatenations.Add(value, null);
            var shape = value is LlvmCallInstruction call ? ConcatProducer(call) : null;
            if (shape is null && RangeInputs(value) is { Count: > 0 } incoming)
            {
                shape = ConcatShape(incoming[0]);
                if (shape is not null && incoming.Skip(1).Any(input => ConcatShape(input) != shape)) shape = null;
            }
            return concatenations[value] = shape;
        }

        private ConcatRecipe? ConcatProducer(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !NativeRangeCall(call, "ranges.concat")) return null;
            int alias = ConcatAlias(call);
            if (alias >= 0) return ConcatShape(call.GetArgument(alias));
            var left = ConcatShape(call.GetArgument(0));
            var right = ConcatShape(call.GetArgument(1));
            var first = left?.Element ?? (SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0)))?.Element;
            var second = right?.Element ?? (SelectedSnapshot(call, 1) ?? VectorShape(call.GetArgument(1)))?.Element;
            if (first is null || second is null || first != second) return null;
            if (!IsGuestPointer(first)) return ElementBytes(first) > 0 ? new(first, left, right) : null;
            var kind = SnapshotKind(call, 0, first);
            return kind is TypeKind.String or TypeKind.Scope && SnapshotKind(call, 1, second) == kind
                ? new(first, left, right) : null;
        }
    }

    private sealed partial class Body
    {
        private readonly record struct ConcatSegment(int Owner, int Length);
        private readonly record struct ConcatCursor(LlvmValue Owner, LlvmType Element, int Current, int End, int One);
        private readonly Dictionary<LlvmCallInstruction, ConcatCursor> concatCursors = [];
        private int concatPlans, concatAliases, concatPulls, concatPacks, concatPackSegments, concatObservations;

        private bool TryConcatRange(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (call.ArgumentCount == 2 && geometry.NativeRangeCall(call, "ranges.concat"))
                return EmitConcatPlan(call);
            if (geometry.IteratorOperation(target, out var operation))
                return EmitConcatIterator(call, operation);
            if (call.ArgumentCount is not (1 or 2) || !geometry.IsGuestPointer(call.GetArgument(0).Type)) return false;
            bool materialize = call.ArgumentCount == 1 && geometry.NativeRangeCall(call, "ranges.materialize");
            VectorAccess access = geometry.VectorAccessFor(call);
            if (!materialize && access == VectorAccess.None) return false;
            var input = call.GetArgument(0);
            if (geometry.ConcatShape(input) is not { } shape) return false;
            if (materialize)
            {
                MaterializeConcat(call, input, shape);
                return true;
            }
            return ObserveConcat(call, input, shape, access);
        }

        private bool EmitConcatPlan(LlvmCallInstruction call)
        {
            int alias = geometry.ConcatAlias(call);
            if (alias >= 0)
            {
                Alias(call, Location(call.GetArgument(alias)));
                ++concatAliases;
                return true;
            }
            if (geometry.ConcatShape(call) is null) return false;
            int output = Location(call);
            EmitCall(call, ~geometry.ConcatAllocator(), output, []);
            Copy(HpirWidth.U64, ~output, Location(call.GetArgument(0)), call.Source);
            Copy(HpirWidth.U64, Indirect(output, sizeof(ulong), call), Location(call.GetArgument(1)), call.Source);
            allocatedTransforms.Add(call);
            ++concatPlans;
            return true;
        }

        private bool EmitConcatIterator(LlvmCallInstruction call, LlvmIteratorOperation operation)
        {
            var source = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.ConcatShape(source) is { } shape)
            {
                concatCursors.Add(call, OpenConcat(call, source, shape));
                return true;
            }
            if (source is not LlvmCallInstruction open || !concatCursors.TryGetValue(open, out var cursor)) return false;
            if (operation == LlvmIteratorOperation.Close) return true;
            if (operation != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction item
                || cursor.Element != item.AllocatedType) return false;
            NextConcat(call, cursor);
            ++concatPulls;
            return true;
        }

        private ConcatCursor OpenConcat(LlvmInstruction site, LlvmValue owner, Geometry.ConcatRecipe shape)
        {
            var parts = ConcatSegments(site, owner, shape);
            int extent = checked(parts.Count * 16);
            int segments = Reserve(extent, 8), current = Reserve(8, 8), end = Reserve(8, 8);
            for (int index = 0; index < parts.Count; index++)
            {
                int at = segments + index * 16;
                Copy(HpirWidth.U64, at, ConcatData(site, parts[index]), site.Source);
                Copy(HpirWidth.U64, at + 8, parts[index].Length, site.Source);
            }
            Add(new(HpirOp.AddressOf, current, segments), site.Source);
            Add(new(HpirOp.OffsetPointer, end, current, Immediate: extent), site.Source);
            return new(owner, shape.Element, current, end, FormatImmediate(site, 1));
        }

        private void NextConcat(LlvmCallInstruction site, ConcatCursor cursor)
        {
            var source = site.Source;
            int available = Location(site), outOfSegments = Reserve(1, 1);
            int next = code.Count;
            Add(new(HpirOp.EqU64, outOfSegments, cursor.Current, cursor.End), source);
            int atEnd = CursorBranch(outOfSegments, source);
            int remaining = Indirect(cursor.Current, sizeof(ulong), site);
            int emptySegment = BranchWhenZero(site, remaining);
            int data = Reserve(8, 8);
            Copy(HpirWidth.U64, data, ~cursor.Current, source);
            CopyVectorItem(site, cursor.Element, data);
            Add(new(HpirOp.OffsetPointer, data, data, Immediate: geometry.ElementBytes(cursor.Element)), source);
            Copy(HpirWidth.U64, ~cursor.Current, data, source);
            Add(new(HpirOp.SubU64, remaining, remaining, cursor.One), source);
            Add(new(HpirOp.ConstU8, available, Immediate: 1), source);
            int ready = code.Count;
            Add(new(HpirOp.Jump), source);
            LinkCursorBranch(emptySegment, code.Count, emptySegment + 1);
            Add(new(HpirOp.OffsetPointer, cursor.Current, cursor.Current, Immediate: 16), source);
            Add(new(HpirOp.Jump, A: next), source);
            LinkCursorBranch(atEnd, code.Count, atEnd + 1);
            Add(new(HpirOp.ConstU8, available), source);
            code[ready] = code[ready] with { A = code.Count };
        }

        private List<ConcatSegment> ConcatSegments(LlvmInstruction site, LlvmValue input, Geometry.ConcatRecipe shape)
        {
            List<ConcatSegment> segments = [];
            AddSegment(Location(input), shape);
            return segments;

            void AddSegment(int pointer, Geometry.ConcatRecipe? branch)
            {
                if (branch is null)
                {
                    int length = Reserve(8, 8);
                    Copy(HpirWidth.U64, length, ~pointer, site.Source);
                    segments.Add(new(pointer, length));
                    return;
                }
                int left = Reserve(8, 8), right = Reserve(8, 8);
                Copy(HpirWidth.U64, left, ~pointer, site.Source);
                Copy(HpirWidth.U64, right, Indirect(pointer, sizeof(ulong), site), site.Source);
                AddSegment(left, branch.Left);
                AddSegment(right, branch.Right);
            }
        }

        private void MaterializeConcat(LlvmCallInstruction call, LlvmValue input, Geometry.ConcatRecipe shape)
        {
            var parts = ConcatSegments(call, input, shape);
            int[] arguments = new int[parts.Count * 2];
            for (int i = 0; i < parts.Count; i++)
            {
                arguments[i * 2] = ConcatData(call, parts[i]);
                arguments[i * 2 + 1] = parts[i].Length;
            }
            int snapshot = Location(call), data = Reserve(8, 8);
            EmitCall(call, ~geometry.TransformImport(HpirService.AllocateObject), snapshot, []);
            transformBufferRoots.Add(code.Count, (snapshot, -1));
            int import = geometry.PackSnapshotImport(shape.Element, parts.Count);
            EmitCall(call, ~import, data, arguments);
            Copy(HpirWidth.U64, ~snapshot, ConcatLength(call, parts), call.Source);
            Copy(HpirWidth.U64, Indirect(snapshot, HpirTransformImports.VectorData, call), data, call.Source);
            allocatedTransforms.Add(call);
            ++concatPacks;
            concatPackSegments += parts.Count;
        }

        private int ConcatData(LlvmInstruction site, ConcatSegment part)
        {
            int data = Reserve(8, 8);
            Copy(HpirWidth.U64, data, Indirect(part.Owner, HpirTransformImports.VectorData, site), site.Source);
            return data;
        }

        private bool ObserveConcat(LlvmCallInstruction call, LlvmValue input, Geometry.ConcatRecipe shape, VectorAccess op)
        {
            if (call.ArgumentCount != (op == VectorAccess.At ? 2 : 1)) return false;
            var parts = ConcatSegments(call, input, shape);
            int length = ConcatLength(call, parts);
            if (op is VectorAccess.Size or VectorAccess.Empty)
            {
                if (op == VectorAccess.Size) Add(new(HpirOp.ConvertI64ToF64, Location(call), length), call.Source);
                else EmitEmptyLength(call, length);
            }
            else if (!ReadConcatIndex(call, shape.Element, parts, length, op)) return false;
            ++concatObservations;
            return true;
        }

        private int ConcatLength(LlvmInstruction site, List<ConcatSegment> parts)
        {
            int total = Reserve(8, 8);
            Add(new(HpirOp.AddU64, total, parts[0].Length, parts[1].Length), site.Source);
            for (int index = 2; index < parts.Count; index++)
                Add(new(HpirOp.AddU64, total, total, parts[index].Length), site.Source);
            return total;
        }

        private bool ReadConcatIndex(LlvmCallInstruction call, LlvmType element, List<ConcatSegment> parts,
            int length, VectorAccess op)
        {
            if (call.Type != element) return false;
            int ordinal = Reserve(8, 8), sourceIndex = -1;
            if (op == VectorAccess.At)
            {
                var index = call.GetArgument(1);
                if (!index.Type.IsDoubleTy() || FutureRoot(index)) return false;
                sourceIndex = Location(index);
                Add(new(HpirOp.OrdinalF64ToU64, ordinal, sourceIndex), call.Source);
            }
            else Add(new(HpirOp.ConstU64, ordinal), call.Source);
            int test = Reserve(1, 1);
            Add(new(HpirOp.LtU64, test, ordinal, length), call.Source);
            int valid = CursorBranch(test, call.Source);
            VectorAccessFailure(call, op, sourceIndex);
            LinkCursorBranch(valid, code.Count, valid + 1);
            List<int> exits = [];
            for (int i = 0; i < parts.Count - 1; i++)
            {
                ConcatSegment segment = parts[i];
                Add(new(HpirOp.LtU64, test, ordinal, segment.Length), call.Source);
                int choose = CursorBranch(test, call.Source);
                CopyVectorElement(call, new(element, false), segment.Owner, ordinal);
                exits.Add(code.Count);
                Add(new(HpirOp.Jump), call.Source);
                LinkCursorBranch(choose, choose + 1, code.Count);
                Add(new(HpirOp.SubU64, ordinal, ordinal, segment.Length), call.Source);
            }
            CopyVectorElement(call, new(element, false), parts[^1].Owner, ordinal);
            foreach (int exit in exits) code[exit] = code[exit] with { A = code.Count };
            return true;
        }

        private void ReportConcats(CompilationTrace? trace)
        {
            trace?.Metric("range.concat.segment_plans", concatPlans);
            trace?.Metric("range.concat.empty_aliases", concatAliases);
            trace?.Metric("range.concat.pull_sites", concatPulls);
            trace?.Metric("range.concat.bulk_materializations", concatPacks);
            trace?.Metric("range.concat.pack_segments", concatPackSegments);
            trace?.Metric("range.concat.direct_observations", concatObservations);
        }
    }
}
