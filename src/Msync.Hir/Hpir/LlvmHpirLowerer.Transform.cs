using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal sealed record TransformRecipe(LlvmFunction Callback, CountedShape? Indexed = null,
            VectorTransform? Vector = null, ZipRecipe? Zip = null, bool Empty = false, SortedRecipe? Sorted = null);

        private readonly Dictionary<LlvmValue, TransformRecipe?> transforms = [];
        private readonly Dictionary<(HpirService, int), int> transformImports = [];
        private readonly Dictionary<(LlvmType Element, int Segments), int> packImports = [];
        private int transformLayout = -1, materializedLayout = -1, recordTransformAllocator = -1;

        internal bool NativeRangeCall(LlvmCallInstruction call, string name) =>
            call.CalledFunction is { } target && graph.NativeCalls.TryGetValue(target, out string? native) && native == name;

        internal LlvmFunction? TransformCallback(LlvmValue value) => TransformShape(value)?.Callback;

        internal bool EmptySnapshotTransform(LlvmCallInstruction call) =>
            call.ArgumentCount == 2 && NativeRangeCall(call, "ranges.transform")
            && (SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0))) is { Empty: true };

        internal TransformRecipe? TransformShape(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (transforms.TryGetValue(value, out var known)) return known;
            transforms.Add(value, null);
            var shape = value is LlvmCallInstruction call ? TransformProducer(call) : null;
            if (shape is null && RangeInputs(value) is { Count: > 0 } incoming)
            {
                shape = ForwardedTransform(incoming);
            }
            return transforms[value] = shape;
        }

        private TransformRecipe? ForwardedTransform(IReadOnlyList<LlvmValue> inputs)
        {
            if (TransformShape(inputs[0]) is not { } selected) return null;
            bool empty = selected.Empty;
            for (int index = 1; index < inputs.Count; ++index)
            {
                var other = TransformShape(inputs[index]);
                if (other is null || selected.Callback != other.Callback || selected.Indexed != other.Indexed
                    || selected.Vector != other.Vector || selected.Zip != other.Zip || selected.Sorted != other.Sorted) return null;
                empty &= other.Empty;
            }
            return selected.Empty == empty ? selected : selected with { Empty = empty };
        }

        private TransformRecipe? TransformProducer(LlvmCallInstruction call)
        {
            if (SessionSnapshotRead(call) is { Callback: { } retained } known)
                return new(retained, Vector: new(false), Empty: known.Carrier.Empty);
            if (call.ArgumentCount != 2) return null;
            LlvmValue input = call.GetArgument(0);
            if (IsTransformTake(call))
                return TransformShape(input) is { Indexed: null, Zip: null, Sorted: null } selected
                    ? selected with { Empty = selected.Empty || ZeroVectorLimit(call) } : null;
            if (!NativeRangeCall(call, "ranges.transform") || FixedUnaryCallback(call.GetArgument(1)) is not { } callback)
                return null;
            var item = callback.Arguments[Captures(callback)];
            LlvmType parameter = item.Type;
            if (!TextResult(callback) && FutureParameter(item) && parameter.IsDoubleTy()
                && (callback.ReturnType.IsDoubleTy() || IsStringResult(callback))
                && (SelectedCarrier(call) ?? SnapshotShape(input)) is { Completion: true } deferred && deferred.Element == parameter)
                return new(callback, Vector: new(false, Completion: true));
            if (FutureArguments(callback) || FutureResult(callback) || TextResult(callback)) return null;
            bool normal = callback.ReturnType.IsDoubleTy() || IsRecordResult(callback);
            bool text = IsStringResult(callback);
            if (!normal && !text) return null;
            if (IsGuestPointer(parameter))
            {
                if (ZipShape(input) is { } zip) return new(callback, Zip: zip, Empty: zip.Empty);
                if (CountedRecipe(input) is { Indexed: true } indexed) return new(callback, indexed);
            }
            if (SortedShape(input) is { } sorted && parameter == sorted.Element)
                return new(callback, Empty: sorted.Empty, Sorted: sorted);
            if (parameter.IsDoubleTy() && IsNumericPlan(input)) return new(callback);
            if ((SelectedSnapshot(call) ?? VectorShape(input)) is not { } vector) return null;
            bool compatible = parameter.IsDoubleTy() && (vector.Empty || vector.Element.IsDoubleTy())
                || StringValue(item) && IsTextSnapshot(call, 0, vector)
                || IsGuestPointer(parameter) && item.LogicalType >= 0 && graph.Types[item.LogicalType].Kind == TypeKind.Type
                    && SnapshotKind(call, 0, vector.Element) == TypeKind.Type && (vector.Empty || vector.Element == parameter);
            bool scope = text && ScopeTextSnapshot(call, vector, item);
            return compatible || scope ? new(callback, Vector: new(scope), Empty: vector.Empty) : null;
        }

        internal bool OwnsTransformPlan(LlvmCallInstruction call) => IsTransformTake(call) || NativeRangeCall(call, "ranges.transform");

        internal int RecordTransformAllocator()
        {
            if (recordTransformAllocator >= 0) return recordTransformAllocator;
            int layout = AddLayout(new(16, 8, [0]) { BorrowOffsets = [8] });
            return recordTransformAllocator = PhysicalAllocator(layout);
        }

        private bool IsRecordResult(LlvmFunction callback) => IsPointerResult(callback, TypeKind.Scope);
        internal bool IsStringResult(LlvmFunction callback) => IsPointerResult(callback, TypeKind.String);

        private bool IsPointerResult(LlvmFunction callback, TypeKind kind) => IsGuestPointer(callback.ReturnType)
            && graph.SourceFunctions.TryGetValue(callback, out var signature)
            && signature.ResultType >= 0 && graph.Types[signature.ResultType].Kind == kind;

        internal bool IsTransformTake(LlvmCallInstruction call) =>
            call.ArgumentCount == 2 && call.GetArgument(1).Type.IsDoubleTy() && NativeRangeCall(call, "ranges.take");

        internal LlvmType? MaterializedElement(LlvmValue value) =>
            value is LlvmCallInstruction { ArgumentCount: 1 } call && NativeRangeCall(call, "ranges.materialize")
                ? TransformCallback(call.GetArgument(0))?.ReturnType : null;

        internal int TransformImport(HpirService service, HpirFutureInput count = HpirFutureInput.Ready, bool reference = false)
        {
            var key = (service, (int)count | (reference ? 4 : 0));
            if (transformImports.TryGetValue(key, out int index)) return index;
            HpirImport import = service switch
            {
                HpirService.RangeTransform => HpirTransformImports.Capture(TransformLayout()),
                HpirService.RangeTransformTake => HpirTransformImports.Take(TransformLayout(), count),
                HpirService.RangeTransformOpen => HpirTransformImports.Open(),
                HpirService.RangeBufferGrow => HpirTransformImports.Grow(AddLayout(HpirTransformImports.ElementLayout(reference))),
                HpirService.AllocateObject => new(service, materializedLayout >= 0 ? materializedLayout
                    : materializedLayout = AddLayout(HpirTransformImports.VectorLayout()), HpirWidth.U64, []),
                _ => throw new ArgumentOutOfRangeException(nameof(service))
            };
            index = Imports.Count;
            Imports.Add(import);
            transformImports.Add(key, index);
            return index;

            int TransformLayout() => transformLayout >= 0 ? transformLayout : transformLayout = AddLayout(HpirTransformImports.TransformLayout());
        }

        internal int PackSnapshotImport(LlvmType element, int segments)
        {
            if (packImports.TryGetValue((element, segments), out int index)) return index;
            int[] borrows = Leaves(element).Where(leaf => IsGuestPointer(leaf.Type)).Select(leaf => leaf.Offset).ToArray();
            int layout = AddLayout(new(ElementBytes(element), checked((int)Data.GetABITypeAlign(element).Value), [])
            { BorrowOffsets = borrows });
            index = Imports.Count;
            Imports.Add(HpirTransformImports.PackWords(layout, segments));
            packImports.Add((element, segments), index);
            return index;
        }

        private int AddLayout(HpirLayout layout)
        {
            int index = Layouts.Count;
            Layouts.Add(layout);
            return index;
        }
    }

    private sealed partial class Body
    {
        private readonly record struct TransformCursor(int Offset, LlvmFunction Callback, LlvmValue Owner, int[] Captures,
            CountedCursor? Indexed = null, VectorTransformCursor? Vector = null, ZipCursor? Zip = null, bool Empty = false,
            SortedCursor? Sorted = null, int InputCompletion = -1);
        private readonly Dictionary<LlvmCallInstruction, TransformCursor> transformCursors = [];
        private readonly Dictionary<int, (int Vector, int Item)> transformBufferRoots = [];
        private readonly HashSet<LlvmValue> allocatedTransforms = [];
        private int transformPlans, transformWindows, transformOpens, transformCallbacks, transformSnapshots,
            transformCloseElisions, emptySnapshotTransforms;

        private bool TryNumericTransform(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (geometry.EmptySnapshotTransform(call))
            {
                InitializeWordSnapshot(call, Location(call));
                allocatedTransforms.Add(call);
                ++emptySnapshotTransforms;
                return true;
            }
            if (geometry.OwnsTransformPlan(call) && geometry.TransformShape(call) is { } producer)
            {
                EmitTransformProducer(call, producer);
                return true;
            }
            if (geometry.IteratorOperation(target, out var operation)) return EmitTransformIterator(call, operation);
            if (call.ArgumentCount != 1) return false;
            bool front = geometry.VectorAccessFor(call) is VectorAccess.Front or VectorAccess.First;
            bool size = geometry.NativeRangeCall(call, "ranges.size");
            if (!front && !size && !geometry.NativeRangeCall(call, "ranges.materialize")) return false;
            LlvmValue input = call.GetArgument(0);
            if (geometry.TransformShape(input) is not { } shape) return false;
            if (geometry.FutureResult(shape.Callback)) return false;
            if (size)
            {
                if (!shape.Empty) return false;
                Add(new(HpirOp.ConstU64, Location(call)), call.Source);
                ++emptyTransformSizes;
                return true;
            }
            if (front)
            {
                if (call.Type != shape.Callback.ReturnType) return false;
                if (shape.Empty || geometry.ReductionWork(shape.Callback) == LlvmRangeCallbackWork.Ready)
                    FrontTransform(call, OpenTransform(call, input, shape));
                else if (geometry.IsEntry(function) && geometry.CooperativeNumberFront(call))
                    FrontGuestNumber(call, OpenTransform(call, input, shape));
                else
                    throw Error("ranges.front of a suspending callback requires typed stream delivery", call);
                return true;
            }
            SnapshotTransform(call, OpenTransform(call, input, shape));
            return true;
        }

        private void EmitTransformProducer(LlvmCallInstruction call, Geometry.TransformRecipe producer)
        {
            int input = Location(call.GetArgument(0));
            if (producer.Vector is { } vector)
            {
                if (vector.ScopeText) ++vectorScopeTextPlans;
                EmitVectorTransform(call, input, producer);
                return;
            }
            if (producer.Indexed is not null || producer.Zip is not null || producer.Sorted is not null)
            {
                int plan = Location(call);
                EmitCall(call, ~geometry.RecordTransformAllocator(), plan, []);
                Copy(HpirWidth.U64, ~plan, input, call.Source);
                Copy(HpirWidth.U64, Indirect(plan, 8, call), LeafLocation(call.GetArgument(1), 8), call.Source);
                allocatedTransforms.Add(call);
                ++transformPlans;
                return;
            }
            int import;
            int[] arguments;
            if (geometry.IsTransformTake(call))
            {
                var count = FutureInput(call.GetArgument(1));
                import = geometry.TransformImport(HpirService.RangeTransformTake, count);
                var inputs = new List<int>(3) { input };
                AddFutureInput(inputs, call.GetArgument(1));
                arguments = inputs.ToArray();
                ++transformWindows;
            }
            else
            {
                import = geometry.TransformImport(HpirService.RangeTransform);
                int readyLayout = Reserve(8, 8);
                Add(new(HpirOp.ConstU64, readyLayout, Immediate: geometry.NumericPlanLayout()), call.Source);
                arguments = [input, LeafLocation(call.GetArgument(1), 8), readyLayout];
                ++transformPlans;
            }
            RangeSourcePoint(call.Source);
            EmitImportCall(call, import, arguments);
            allocatedTransforms.Add(call);
        }

        private bool EmitTransformIterator(LlvmCallInstruction call, LlvmIteratorOperation operation)
        {
            var input = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.TransformShape(input) is { } shape)
            {
                transformCursors.Add(call, OpenTransform(call, input, shape));
                return true;
            }
            if (input is not LlvmCallInstruction original || !transformCursors.TryGetValue(original, out var cursor)) return false;
            if (operation == LlvmIteratorOperation.Close)
            {
                ++transformCloseElisions;
                return true;
            }
            if (operation != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction item
                || item.AllocatedType != cursor.Callback.ReturnType) return false;
            int available = Location(call);
            if (cursor.Empty)
            {
                Add(new(HpirOp.ConstU8, available), call.Source);
                return true;
            }
            int number = PullTransformInput(call, cursor, available);
            int takeItem = CursorBranch(available, call.Source);
            LinkCursorBranch(takeItem, code.Count, -1);
            int resultCompletion = -1;
            if (geometry.FutureResult(cursor.Callback) && !frameCompletions.TryGetValue(item, out resultCompletion))
                throw Error("mapped iterator lacks its selected scalar completion lane", call);
            CallTransformCallback(call, cursor, number, Memory(item), completion: resultCompletion);
            code[takeItem] = code[takeItem] with { D = code.Count };
            return true;
        }

        private TransformCursor OpenTransform(LlvmInstruction site, LlvmValue plan, Geometry.TransformRecipe shape, int pointer = -1)
        {
            ++transformOpens;
            if (shape.Empty) return new(-1, shape.Callback, plan, [], Empty: true);
            int reference = pointer >= 0 ? pointer : Location(plan);
            if (shape.Zip is { } zip)
            {
                int source = Reserve(8, 8);
                Copy(HpirWidth.U64, source, ~reference, site.Source);
                return new(-1, shape.Callback, plan, ReadPlanCaptures(site, reference, 8, shape.Callback),
                    Zip: OpenZip(site, plan, source, zip));
            }
            if (shape.Sorted is { } sorted)
            {
                int source = Reserve(8, 8);
                Copy(HpirWidth.U64, source, ~reference, site.Source);
                return new(-1, shape.Callback, plan, ReadPlanCaptures(site, reference, 8, shape.Callback),
                    Sorted: OpenSorted(site, plan, source, sorted));
            }
            if (shape.Vector is { } vector)
                return new(-1, shape.Callback, plan, ReadPlanCaptures(site, reference, VectorTransformPlan.Environment, shape.Callback),
                    Vector: OpenVectorTransform(site, reference), InputCompletion: vector.Completion ? Reserve(8, 8) : -1);
            if (shape.Indexed is { } counted)
            {
                int source = Reserve(8, 8);
                Copy(HpirWidth.U64, source, ~reference, site.Source);
                return new(-1, shape.Callback, plan, ReadPlanCaptures(site, reference, 8, shape.Callback),
                    OpenCounted(site, plan, source, counted));
            }
            int cursor = Reserve(HpirRangeImports.CursorBytes + sizeof(ulong), HpirRangeImports.Alignment);
            int cursorPointer = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, cursorPointer, cursor), site.Source);
            Add(new(HpirOp.ConstU64, cursor + HpirRangeImports.CursorBytes, Immediate: 1), site.Source);
            RangeSourcePoint(site.Source);
            EmitCall(site, ~geometry.TransformImport(HpirService.RangeTransformOpen), -1, [reference, cursorPointer], safepoint: false);
            // Cache capture addresses, not the mutable values they refer to.
            return new(cursor, shape.Callback, plan, ReadPlanCaptures(site, reference, HpirTransformImports.Environment, shape.Callback));
        }

        private int PullTransformInput(LlvmInstruction site, TransformCursor cursor, int available, int snapshot = -1)
        {
            int input = Reserve(8, 8);
            if (cursor.Zip is { } zip) PullZip(site, zip, available, input, snapshot);
            else if (cursor.Sorted is { } sorted) PullSorted(site, sorted, available, input, snapshot);
            else if (cursor.Indexed is { } indexed) PullCounted(site, indexed, available, input, snapshot);
            else if (cursor.Vector is { } vector) PullVectorTransform(site, vector, available, input, cursor.InputCompletion);
            else AdvanceNumericCursor(site, cursor.Offset, input, available);
            return input;
        }

        private void CallTransformCallback(LlvmInstruction site, TransformCursor cursor, int number, int output,
            int snapshot = -1, int completion = -1)
        {
            if (cursor.InputCompletion < 0)
                EmitRangeCallback(site, cursor.Callback, cursor.Captures, number, output, snapshot);
            else
                CallCompletionTransform(site, cursor, number, output, snapshot, completion);
            ++transformCallbacks;
        }

        private void CallCompletionTransform(LlvmInstruction site, TransformCursor cursor, int value, int output,
            int snapshot, int completion)
        {
            int[] arguments = [.. cursor.Captures, cursor.InputCompletion, value];
            if (completion >= 0)
            {
                int address = Reserve(8, 8);
                Add(new(HpirOp.AddressOf, address, output), site.Source);
                arguments = [address, .. arguments];
            }
            if (snapshot >= 0) transformBufferRoots.Add(code.Count, (snapshot, -1));
            RangeSourcePoint(site.Source);
            EmitCall(site, ids[cursor.Callback], completion >= 0 ? completion : output, arguments);
            ++vectorCompletionCalls;
        }

        private void EmitRangeCallback(LlvmInstruction site, LlvmFunction target, int[] captures, int input, int output,
            int snapshot = -1)
        {
            var arguments = new int[captures.Length + 1];
            captures.CopyTo(arguments, 0);
            arguments[^1] = input;
            if (snapshot >= 0) transformBufferRoots.Add(code.Count, (snapshot, -1));
            RangeSourcePoint(site.Source);
            EmitCall(site, ids[target], output, arguments);
        }

        private void FrontTransform(LlvmCallInstruction call, TransformCursor cursor)
        {
            if (cursor.Empty)
            {
                EmitFailure(call, HpirFailure.RangeEmpty);
                return;
            }
            int available = Reserve(1, 1);
            int number = PullTransformInput(call, cursor, available);
            int nonempty = CursorBranch(available, call.Source);
            RangeSourcePoint(call.Source);
            EmitFailure(call, HpirFailure.RangeEmpty);
            LinkCursorBranch(nonempty, code.Count, nonempty + 1);
            CallTransformCallback(call, cursor, number, Location(call));
        }

        private void SnapshotTransform(LlvmCallInstruction call, TransformCursor cursor, int scratch = -1)
        {
            int snapshot = scratch >= 0 ? scratch : Location(call);
            InitializeWordSnapshot(call, snapshot);
            if (scratch < 0) allocatedTransforms.Add(call);
            ++transformSnapshots;
            if (cursor.Empty) return;
            CollectWords(call, snapshot, geometry.IsGuestPointer(cursor.Callback.ReturnType), available =>
            {
                int input = PullTransformInput(call, cursor, available, snapshot);
                return (input, cursor.Indexed.HasValue || cursor.Zip is not null ? input : -1);
            }, (site, owner, input) =>
            {
                int item = Reserve(8, 8);
                CallTransformCallback(site, cursor, input, item, owner);
                return item;
            });
        }

        private void InitializeWordSnapshot(LlvmInstruction site, int snapshot)
        {
            RangeSourcePoint(site.Source);
            EmitCall(site, ~geometry.TransformImport(HpirService.AllocateObject), snapshot, []);
        }

        private void CollectWords(LlvmInstruction call, int snapshot, bool reference,
            Func<int, (int Input, int Root)> pullItem, Func<LlvmInstruction, int, int, int> project)
        {
            var source = call.Source;
            int data = Reserve(8, 8), length = Reserve(8, 8), capacity = Reserve(8, 8);
            int available = Reserve(1, 1), room = Reserve(1, 1);
            int one = Reserve(8, 8), initial = Reserve(8, 8), stride = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, data), source);
            Add(new(HpirOp.ConstU64, length), source);
            Add(new(HpirOp.ConstU64, capacity), source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), source);
            Add(new(HpirOp.ConstU64, initial, Immediate: 16), source);
            Add(new(HpirOp.ConstU64, stride, Immediate: 8), source);

            int pull = code.Count;
            var (input, root) = pullItem(available);
            int hasItem = CursorBranch(available, source);
            Add(new(HpirOp.LtU64, room, length, capacity), source);
            int hasRoom = CursorBranch(room, source);
            Add(new(HpirOp.AddU64, capacity, capacity, capacity), source);
            Add(new(HpirOp.LtU64, room, capacity, initial), source);
            int useInitial = CursorBranch(room, source);
            Add(new(HpirOp.CopyU64, capacity, initial), source);
            int grow = code.Count;
            LinkCursorBranch(useInitial, grow - 1, grow);
            transformBufferRoots.Add(grow, (snapshot, root));
            RangeSourcePoint(source);
            EmitCall(call, ~geometry.TransformImport(HpirService.RangeBufferGrow, reference: reference), data,
                [data, length, capacity]);
            Copy(HpirWidth.U64, Indirect(snapshot, HpirTransformImports.VectorData, call), data, source);

            LinkCursorBranch(hasRoom, code.Count, hasRoom + 1);
            int item = project(call, snapshot, input);
            int address = Reserve(8, 8);
            Add(new(HpirOp.MulU64, address, length, stride), source);
            Add(new(HpirOp.AddPointer, address, data, address), source);
            Add(new(HpirOp.CopyU64, ~address, item), source);
            Add(new(HpirOp.AddU64, length, length, one), source);
            Add(new(HpirOp.Jump, A: pull), source);
            LinkCursorBranch(hasItem, hasItem + 1, code.Count);
            Copy(HpirWidth.U64, ~snapshot, length, source);
        }

        private void ReportTransforms(CompilationTrace? trace)
        {
            trace?.Metric("range.transform.plans", transformPlans);
            trace?.Metric("range.transform.windows", transformWindows);
            trace?.Metric("range.transform.frame_cursors", transformOpens);
            trace?.Metric("range.transform.compiled_callback_sites", transformCallbacks);
            trace?.Metric("range.transform.materializations", transformSnapshots);
            trace?.Metric("range.transform.resource_free_closes", transformCloseElisions);
            ReportVectorTransforms(trace);
            ReportSortedRanges(trace);
            ReportSnapshotSorts(trace);
            if (emptySnapshotTransforms != 0) trace?.Metric("range.transform.empty_snapshots", emptySnapshotTransforms);
        }
    }
}
