using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmValue, bool> numericPlans = [];
        private readonly Dictionary<(HpirService Service, int Modes), int> rangeImports = [];
        private int numericPlanLayout = -1, deferredPlanLayout = -1;

        internal int NumericRangeImport(HpirService service, HpirFutureInput count = HpirFutureInput.Ready)
        {
            var key = (service, (int)count);
            if (rangeImports.TryGetValue(key, out int id)) return id;

            id = Imports.Count;
            Imports.Add(HpirRangeImports.Create(service, NumericPlanLayout(), count));
            rangeImports.Add(key, id);
            return id;
        }

        internal int NumericPlanLayout()
        {
            if (numericPlanLayout >= 0) return numericPlanLayout;
            numericPlanLayout = Layouts.Count;
            Layouts.Add(HpirRangeImports.ReadyLayout());
            return numericPlanLayout;
        }

        internal int NumericDeferredSequence(HpirFutureInput first, HpirFutureInput limit, HpirFutureInput step)
        {
            var key = (HpirService.RangeDeferredSequence, (int)first | ((int)limit << 2) | ((int)step << 4));
            if (rangeImports.TryGetValue(key, out int id)) return id;
            if (deferredPlanLayout < 0)
            {
                deferredPlanLayout = Layouts.Count;
                Layouts.Add(HpirRangeImports.DeferredLayout());
            }
            id = Imports.Count;
            Imports.Add(HpirRangeImports.DeferredSequence(deferredPlanLayout, first, limit, step));
            rangeImports.Add(key, id);
            return id;
        }

        internal bool IteratorOperation(LlvmFunction target, out LlvmIteratorOperation operation) =>
            graph.IteratorCalls.TryGetValue(target, out operation);

        internal HpirService NumericRangeProducer(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !graph.NativeCalls.TryGetValue(target, out string? name))
                return default;
            return name switch
            {
                "ranges.sequence" when call.ArgumentCount == 3 && call.Arguments.All(argument => argument.Type.IsDoubleTy())
                    => HpirService.RangeSequence,
                "ranges.drop" when WindowInput(call) => HpirService.RangeDrop,
                "ranges.take" when WindowInput(call) => HpirService.RangeTake,
                _ => default
            };
        }

        private bool WindowInput(LlvmCallInstruction call) =>
            call.ArgumentCount == 2 && call.GetArgument(1).Type.IsDoubleTy() && IsNumericPlan(call.GetArgument(0));

        internal bool IsNumericPlan(LlvmValue value)
        {
            if (value is not LlvmCallInstruction { CalledFunction: { } target } call
                || !value.Type.IsPointerTy()
                || value.LogicalType >= 0 && (graph.Types[value.LogicalType].Kind & TypeKind.Range) == 0)
                return false;
            if (numericPlans.TryGetValue(value, out bool known)) return known;
            // Real cyclic/indirect range flows require representation selection ahead of this adapter.
            numericPlans.Add(value, false);
            bool admitted = NumericRangeProducer(call) != default || !target.IsDeclaration && ReturnsNumericPlan(target);
            if (admitted) numericPlans[value] = true;
            return admitted;
        }

        private bool ReturnsNumericPlan(LlvmFunction target)
        {
            bool found = false;
            foreach (var block in target.BasicBlocks)
            {
                if (block.LastInstruction is not LlvmReturnInstruction result) continue;
                if (result.ReturnValue is not { } value || !IsNumericPlan(value)) return false;
                found = true;
            }
            return found;
        }
    }

    private sealed partial class Body
    {
        private readonly Dictionary<LlvmCallInstruction, int> numericCursors = [];
        private int numericRangeProducers, numericCursorOpens, numericCursorNexts, numericClosesElided;
        private int deferredBoundSites, deferredCountSites, numericSnapshots;
        private int cursorPredicate = -1, cursorItem = -1;

        private bool TryNumericRange(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            HpirService producer = geometry.NumericRangeProducer(call);
            if (producer != default)
            {
                EmitNumericProducer(call, producer);
                numericRangeProducers++;
                return true;
            }
            if (!geometry.IteratorOperation(target, out var operation))
            {
                if (call.ArgumentCount != 1 || !geometry.NativeRangeCall(call, "ranges.materialize")
                    || !geometry.IsNumericPlan(call.GetArgument(0))) return false;
                MaterializeNumbers(call);
                return true;
            }
            switch (operation)
            {
                case LlvmIteratorOperation.Open when geometry.IsNumericPlan(call.GetArgument(0)):
                    OpenNumericCursor(call);
                    return true;
                case LlvmIteratorOperation.Next when NumericCursor(call.GetArgument(0)) is { } open && DoubleItem(call.GetArgument(1)):
                    AdvanceNumericCursor(call, Cursor(open), Memory(call.GetArgument(1)), Location(call));
                    numericCursorNexts++;
                    return true;
                case LlvmIteratorOperation.Close when NumericCursor(call.GetArgument(0)) is not null:
                    // A pointer-free cursor has the lifetime of its physical frame region.
                    numericClosesElided++;
                    return true;
                default:
                    return false;
            }
        }

        private void EmitNumericProducer(LlvmCallInstruction call, HpirService service)
        {
            if (service == HpirService.RangeSequence)
            {
                HpirFutureInput first = FutureInput(call.GetArgument(0)), limit = FutureInput(call.GetArgument(1)),
                    step = FutureInput(call.GetArgument(2));
                if ((first | limit | step) != HpirFutureInput.Ready)
                {
                    var arguments = new List<int>(6);
                    foreach (var argument in call.Arguments) AddFutureInput(arguments, argument);
                    RangeSourcePoint(call.Source);
                    EmitImportCall(call, geometry.NumericDeferredSequence(first, limit, step), arguments.ToArray());
                    deferredBoundSites++;
                    return;
                }
            }
            else
            {
                HpirFutureInput count = FutureInput(call.GetArgument(1));
                if (count != HpirFutureInput.Ready)
                {
                    var arguments = new List<int>(3) { Location(call.GetArgument(0)) };
                    AddFutureInput(arguments, call.GetArgument(1));
                    RangeSourcePoint(call.Source);
                    EmitImportCall(call, geometry.NumericRangeImport(service, count), arguments.ToArray());
                    deferredCountSites++;
                    return;
                }
            }
            EmitImportCall(call, geometry.NumericRangeImport(service), call.Arguments.Select(Location).ToArray());
        }

        private void OpenNumericCursor(LlvmCallInstruction call)
        {
            OpenNumericCursor(call, Cursor(call), Location(call));
        }

        private void OpenNumericCursor(LlvmCallInstruction call, int cursor, int pointer)
        {
            Add(new(HpirOp.AddressOf, pointer, cursor), call.Source);
            Add(new(HpirOp.ConstU64, cursor + HpirRangeImports.CursorBytes, Immediate: 1), call.Source);
            RangeSourcePoint(call.Source);
            EmitCall(call, ~geometry.NumericRangeImport(HpirService.RangeOpen), -1,
                [Location(call.GetArgument(0)), pointer]);
            numericCursorOpens++;
        }

        private void MaterializeNumbers(LlvmCallInstruction call)
        {
            int cursor = Cursor(call), pointer = Reserve(sizeof(ulong), sizeof(ulong));
            OpenNumericCursor(call, cursor, pointer);
            int value = Reserve(sizeof(double), sizeof(double));
            int snapshot = Location(call);
            InitializeWordSnapshot(call, snapshot);
            allocatedTransforms.Add(call);
            CollectWords(call, snapshot, reference: false, available =>
            {
                AdvanceNumericCursor(call, cursor, value, available);
                return (value, -1);
            }, static (_, _, item) => item);
            ++numericSnapshots;
        }

        private LlvmCallInstruction? NumericCursor(LlvmValue value) =>
            value is LlvmCallInstruction { CalledFunction: { } target } call
            && geometry.IteratorOperation(target, out var operation) && operation == LlvmIteratorOperation.Open
            && geometry.IsNumericPlan(call.GetArgument(0)) ? call : null;

        private int Cursor(LlvmCallInstruction open)
        {
            if (numericCursors.TryGetValue(open, out int cursor)) return cursor;
            cursor = Reserve(HpirRangeImports.CursorBytes + sizeof(ulong), HpirRangeImports.Alignment);
            numericCursors.Add(open, cursor);
            return cursor;
        }

        private void AdvanceNumericCursor(LlvmInstruction call, int cursor, int destination, int result)
        {
            if (cursorPredicate < 0) cursorPredicate = Reserve(sizeof(byte), sizeof(byte));
            if (cursorItem < 0) cursorItem = Reserve(sizeof(double), sizeof(double));
            SourceSpan? source = call.Source;
            int position = cursor + HpirRangeImports.Position, fence = cursor + HpirRangeImports.Fence;
            int current = cursor + HpirRangeImports.Current, limit = cursor + HpirRangeImports.Limit;

            int pull = code.Count;
            Add(new(HpirOp.EqU64, cursorPredicate, position, fence), source);
            int atFence = CursorBranch(cursorPredicate, source);
            int stalled = CursorBranch(cursor + HpirRangeImports.Stalled, source);
            int direction = CursorBranch(cursor + HpirRangeImports.Descending, source);

            int descending = code.Count;
            Add(new(HpirOp.GtF64, cursorPredicate, current, limit), source);
            int descendingBound = CursorBranch(cursorPredicate, source);
            int ascending = code.Count;
            Add(new(HpirOp.LtF64, cursorPredicate, current, limit), source);
            int ascendingBound = CursorBranch(cursorPredicate, source);

            int advance = code.Count;
            Add(new(HpirOp.CopyU64, cursorItem, current), source);
            Add(new(HpirOp.AddF64, current, cursorItem, cursor + HpirRangeImports.Step), source);
            Add(new(HpirOp.EqF64, cursor + HpirRangeImports.Stalled, current, cursorItem), source);
            Add(new(HpirOp.LtU64, cursorPredicate, position, cursor + HpirRangeImports.Skip), source);
            Add(new(HpirOp.AddU64, position, position, cursor + HpirRangeImports.CursorBytes), source);
            Add(new(HpirOp.Branch, D: code.Count + 1, A: cursorPredicate, B: pull), source);

            Add(new(HpirOp.CopyU64, destination, cursorItem), source);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, result, Immediate: 1), source);
            int yielded = code.Count;
            Add(new(HpirOp.Jump), source);

            int fail = code.Count;
            // The failure import has its own site even when adjacent cursor
            // operations share one source span.
            RangeSourcePoint(source);
            EmitFailure(call, HpirFailure.RangeStalled);
            int finish = code.Count;
            Add(new(HpirOp.CopyU64, position, fence), source);
            int exhausted = code.Count;
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, result), source);

            LinkCursorBranch(atFence, exhausted, stalled);
            LinkCursorBranch(stalled, fail, direction);
            LinkCursorBranch(direction, descending, ascending);
            LinkCursorBranch(descendingBound, advance, finish);
            LinkCursorBranch(ascendingBound, advance, finish);
            code[yielded] = code[yielded] with { A = code.Count };
        }

        private void CopyPlanWord(LlvmInstruction site, int destination, int source, int offset, int lane)
        {
            Copy(HpirWidth.U64, lane, Indirect(source, offset, site), site.Source);
            Copy(HpirWidth.U64, Indirect(destination, offset, site), lane, site.Source);
        }

        private int BranchWhenZero(LlvmInstruction site, int tested)
        {
            int zero = Reserve(8, 8), condition = Reserve(1, 1);
            Add(new(HpirOp.ConstU64, zero), site.Source);
            Add(new(HpirOp.EqU64, condition, tested, zero), site.Source);
            return CursorBranch(condition, site.Source);
        }

        private int CursorBranch(int condition, SourceSpan? source)
        {
            int pc = code.Count;
            Add(new(HpirOp.Branch, A: condition), source);
            return pc;
        }

        private void LinkCursorBranch(int pc, int yes, int no) => code[pc] = code[pc] with { B = yes, D = no };

        private void RangeSourcePoint(SourceSpan? source)
        {
            int pc = code.Count;
            if (source is { } span && (sources.Count == 0 || sources[^1].Instruction != pc))
                sources.Add(new(pc, span));
        }

        private static bool DoubleItem(LlvmValue address) =>
            address is LlvmAllocaInstruction allocation && allocation.AllocatedType.IsDoubleTy();

        private void RecordNumericRange(CompilationTrace? trace)
        {
            trace?.Metric("range.numeric_producers", numericRangeProducers);
            trace?.Metric("range.deferred_bound_sites", deferredBoundSites);
            trace?.Metric("range.deferred_count_sites", deferredCountSites);
            trace?.Metric("range.frame_cursors", numericCursorOpens);
            trace?.Metric("range.cursor_next_sites", numericCursorNexts);
            if (numericSnapshots != 0) trace?.Metric("range.numeric_snapshot_sites", numericSnapshots);
            trace?.Metric("range.resource_free_closes", numericClosesElided);
        }
    }
}
