using Msync.Core;
using Msync.Core.Native;
using Buffer = System.Runtime.InteropServices.NativeMemory;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private bool TryPlanRange(string name, IReadOnlyList<NativeDatum> args, CancellationToken token, out NativeDatum result)
    {
        result = default;
        if (guestHeap.References is not { } arena) return false;
        if (TryTextPlan(arena, name, args, token, out result)) return true;
        if (!name.StartsWith("ranges.", StringComparison.Ordinal)) return false;
        NativeRangeOp opcode = name switch
        {
            "ranges.sequence" or "ranges.closed" => NativeRangeOp.Sequence,
            "ranges.repeat" => NativeRangeOp.Repeat,
            "ranges.take" => NativeRangeOp.Take,
            "ranges.drop" => NativeRangeOp.Drop,
            "ranges.stride" or "ranges.step" => NativeRangeOp.Stride,
            "ranges.concat" => NativeRangeOp.Concat,
            "ranges.transform" => NativeRangeOp.Transform,
            "ranges.filter" => NativeRangeOp.Filter,
            "ranges.sort" => NativeRangeOp.Sort,
            "ranges.merge" => NativeRangeOp.Merge,
            _ => (NativeRangeOp)byte.MaxValue
        };
        if (opcode == (NativeRangeOp)byte.MaxValue || args.Count != (name == "ranges.sequence" ? 3 : opcode == NativeRangeOp.Sort ? 1 : 2)) return false;
        for (int i = 0; i < args.Count; i++)
            if (!args[i].IsReady) return false;
        NativeValue first = args[0].Value, second = args.Count > 1 ? args[1].Value : default;
        string operation = name[7..];
        bool callback = opcode is NativeRangeOp.Transform or NativeRangeOp.Filter;
        NativeFunction function = default;
        if (callback)
        {
            if (second.Kind != NativeKind.Function) return false;
            function = second.AsFunction();
            if (function.NativeName is not null || !ExecutionFunction(image, function.Function).IsContextFree
                || DatumExecutionMayDefer(new(second))) return false;
        }
        NativeRangeNode node = new() { Op = opcode, Depth = 1 };
        bool fused = false;
        switch (opcode)
        {
            case NativeRangeOp.Sequence:
                node.Inclusive = name == "ranges.closed";
                node.Operands.First = node.Inclusive ? NativeBuiltins.ClosedEndpoint(first) : NativeBuiltins.FiniteNumber(first, operation);
                node.Operands.Limit = node.Inclusive ? NativeBuiltins.ClosedEndpoint(second) : NativeBuiltins.FiniteNumber(second, operation);
                node.Operands.Step = node.Inclusive ? 1 : NativeBuiltins.FiniteNumber(args[2].Value, operation);
                if (node.Operands.Step == 0) throw new MsyncException("ranges.sequence requires a nonzero step");
                break;
            case NativeRangeOp.Repeat:
                if (first.Kind != NativeKind.Number) return false;
                node.Operands.First = first.AsNumber();
                node.Count = NativeBuiltins.Ordinal(second, operation, false);
                break;
            default:
                node.Left = LinkRangeInput(first, arena, token);
                if (node.Left == 0) return false;
                ref NativeRangeNode left = ref NativeRangeNode.At(node.Left);
                node.StringItems = left.StringItems;
                node.Shape = left.Shape;
                node.UsesValues = left.UsesValues;
                node.HasCallbacks = left.HasCallbacks;
                node.Depth = checked(1 + left.Depth);
                if (opcode is NativeRangeOp.Concat or NativeRangeOp.Merge)
                {
                    node.Right = LinkRangeInput(second, arena, token);
                    if (node.Right == 0) return false;
                    ref NativeRangeNode right = ref NativeRangeNode.At(node.Right);
                    if (node.Shape != right.Shape || node.StringItems != right.StringItems) return false;
                    node.UsesValues |= right.UsesValues;
                    node.HasCallbacks |= right.HasCallbacks;
                    if (opcode == NativeRangeOp.Merge && node.HasCallbacks) return false;
                    node.Depth = checked(1 + Math.Max(left.Depth, right.Depth));
                }
                else if (callback)
                {
                    var callbackCounters = (currentWork.Value ?? executionWork).Counters;
                    NativeAllocation callable = guestHeap.NewObject(rangeCallableLayout, -1, 1, 0, callbackCounters);
                    NativeStorage.StoreTrusted(rangeCallableLayout[0].Type, callable.Field(0), second);
                    node.Right = callable.Storage.RawAddress;
                    if (opcode == NativeRangeOp.Transform)
                    {
                        node.Shape = arena.LinkRangeShape(DescribeFunction(function)?.Result ?? TypeRef.Any, CallbackPhysical(second));
                        node.StringItems = false;
                    }
                    node.UsesValues = node.HasCallbacks = true;
                }
                else if (opcode == NativeRangeOp.Sort) { if (node.HasCallbacks) return false; }
                else
                {
                    ulong count = (ulong)NativeBuiltins.Ordinal(second, operation, opcode == NativeRangeOp.Stride);
                    fused = LinkRangeWindow(ref node, in left, arena, opcode, count, token);
                }
                break;
        }

        nint edge = guestHeap.NewRangePlan(node, token);
        var counters = (currentWork.Value ?? executionWork).Counters;
        counters.Native(name);
        counters.RangePlanNode(node.Left == 0 ? 0 : node.Right == 0 ? 1 : 2, opcode is NativeRangeOp.Sort or NativeRangeOp.Merge);
        if (fused) counters.RangeWindowFusion();
        result = new(NativeValue.FromRangePlan(arena, edge));
        return true;
    }

    private static bool LinkRangeWindow(ref NativeRangeNode node, in NativeRangeNode input,
        NativeGuestObjectTable arena, NativeRangeOp operation, ulong count, CancellationToken token)
    {
        node.Op = NativeRangeOp.Window;
        if (input.Op == NativeRangeOp.Window && arena.MatchesRangeCancellation(node.Left, token)
            && input.Operands.Window.TryAppend(operation, count, out var merged))
        {
            node.Left = input.Left;
            node.Depth = input.Depth;
            node.Operands.Window = merged;
            return true;
        }

        NativeRangeWindow.All.TryAppend(operation, count, out node.Operands.Window);
        if (input.Op == NativeRangeOp.Sequence) node.Depth = 1;
        return false;
    }

    private static readonly NativeType rangeWorkspaceType = NativeRangeCursor.WorkspaceType;
    private static readonly NativeLayout rangeWorkspaceLayout = new(rangeWorkspaceType);
    private static readonly NativeLayout rangeCallableLayout = new(NativeType.Of(NativeKind.Function));

    private nint LinkRangeInput(NativeValue value, NativeGuestObjectTable arena, CancellationToken token)
    {
        if (value.Kind != NativeKind.Range) return 0;
        if (value.RangePlan != 0) return ReferenceEquals(value.RangePlanArena, arena) ? value.RangePlan : 0;
        NativeRange range = value.AsRange();
        NativeAddress source = range.SnapshotStorage;
        if (source.IsNull || !ReferenceEquals(source.Allocation.GuestReferences, arena)) return 0;
        int shape = arena.LinkRangeShape(range.ItemType, source.Type);
        (currentWork.Value ?? executionWork).Counters.RangePlanNode(1);
        return guestHeap.NewRangePlan(new()
        {
            Op = NativeRangeOp.Snapshot, UsesValues = true, Depth = 1, Shape = shape,
            Left = source.Allocation.Storage.RawAddress, Offset = source.Offset, Stride = range.SnapshotStride,
            Count = range.SnapshotValues!.Count
        }, token);
    }

    private void DispatchRangeCallback(ref NativeRangeCursor cursor, NativeValue value, CancellationToken token)
    {
        NativeGuestObjectTable arena = guestHeap.References!;
        NativeFunction function = NativeStorage.LoadFunctionDirect(arena.Resolve(cursor.CallbackCell).Field(0));
        bool filtering = cursor.Filtering;
        var counters = (currentWork.Value ?? executionWork).Counters;
        if (cursor.Scratch == 0)
            cursor.Scratch = guestHeap.NewObject(rangeWorkspaceLayout, -1, 1, 0, counters).Storage.RawAddress;
        NativeAllocation scratch = arena.Resolve(cursor.Scratch);
        memory.Write(scratch.Field(0), new(value));
        counters.RangeCallback(resumed: false);
        NativeDatum result = RunFunction(function.Function, function.Environment, NativeArguments.BorrowedPacket(scratch), token);
        NativeValue ready = result.IsReady ? result.Value : Demand(result.ResolveAsync(token));
        if (!filtering) memory.Write(scratch.Field(0), new(ready));
        cursor.CompleteCallback(!filtering || ready.AsBool(), replace: !filtering);
        counters.RangeCallback(resumed: true);
    }

    private bool TryTextPlan(NativeGuestObjectTable arena, string name, IReadOnlyList<NativeDatum> args,
        CancellationToken token, out NativeDatum value)
    {
        value = default;
        NativeRangeOp? op = name switch
        {
            "string.chars" => NativeRangeOp.Characters,
            "string.lines" => NativeRangeOp.Lines,
            "string.split_whitespace" => NativeRangeOp.Words,
            _ => null
        };
        if (op is not { } opcode || args.Count != 1 || !args[0].IsReady) return false;
        NativeValue input = args[0].Value;
        if (input.NativeStringEdge == 0 || !ReferenceEquals(input.NativeStringArena, arena)) return false;
        nint edge = guestHeap.NewRangePlan(new()
        {
            Op = opcode, Depth = 1, StringItems = true, Left = input.NativeStringEdge
        }, token);
        var counters = (currentWork.Value ?? executionWork).Counters;
        counters.Native(name);
        counters.RangePlanNode(1);
        value = new(NativeValue.FromRangePlan(arena, edge));
        return true;
    }

    private bool TryConsumeRangePlan(string name, IReadOnlyList<NativeDatum> args, CancellationToken token, out NativeDatum result)
    {
        result = default;
        if (args.Count is < 1 or > 2 || !args[0].IsReady || args[0].Value.RangePlan == 0) return false;
        ref NativeRangeNode node = ref NativeRangeNode.At(args[0].Value.RangePlan);
        if (node.StringItems || node.UsesValues) return false;
        string operation = name.StartsWith("ranges.", StringComparison.Ordinal) ? name[7..] : "";
        switch (operation)
        {
            case "identity" or "all" or "size" or "empty" or "first" or "last" or "front" or "back" or "materialize":
                if (args.Count != 1) return false;
                break;
            case "at" or "contains" or "find":
                if (args.Count != 2 || !args[1].IsReady) return false;
                break;
            default:
                return false;
        }

        var counters = (currentWork.Value ?? executionWork).Counters;
        counters.Native(name);
        if (operation is "identity" or "all") { result = args[0]; return true; }
        NativeValue source = args[0].Value;
        NativeRangeCursor cursor = NativeRangeCursor.Open(source.RangePlan, source.RangePlanArena, token);
        counters.RangePlanOpen(cursor.Bytes, cursor.HasRootWindowKernel);
        long yields = 0;
        try
        {
            result = operation == "materialize"
                ? MaterializeRangePlan(ref cursor, source.RangePlanArena, token, ref yields)
                : new(ReduceRangePlan(ref cursor, source.RangePlanArena, operation,
                    args.Count == 2 ? args[1].Value : default, token, ref yields));
            return true;
        }
        finally
        {
            counters.RangePlanStep(yields);
            cursor.Close();
        }
    }

    private static NativeValue ReduceRangePlan(ref NativeRangeCursor cursor, NativeGuestObjectTable arena,
        string operation, NativeValue operand, CancellationToken token, ref long yields)
    {
        long index = operation == "at" ? NativeBuiltins.Ordinal(operand, operation, false) : 0;
        double last = 0;
        while (cursor.Next(arena, token, out double value))
        {
            long position = yields++;
            if (operation == "empty") return NativeValue.False;
            if (operation is "first" or "front" || operation == "at" && position == index)
                return NativeValue.FromNumber(value);
            if (operation is "contains" or "find"
                && operand.Kind == NativeKind.Number && value.Equals(operand.AsNumber()))
                return operation == "contains" ? NativeValue.True : NativeValue.FromNumber(value);
            last = value;
        }
        return operation switch
        {
            "size" => NativeValue.FromNumber(yields),
            "empty" => NativeValue.True,
            "contains" => NativeValue.False,
            "at" => throw new MsyncException($"ranges.at has no item at index {index}"),
            "back" or "last" => yields != 0 ? NativeValue.FromNumber(last)
                : throw new MsyncException($"ranges.{operation} requires a nonempty Range"),
            _ => throw new MsyncException($"ranges.{operation} found no matching item")
        };
    }

    private unsafe NativeDatum MaterializeRangePlan(ref NativeRangeCursor cursor, NativeGuestObjectTable arena,
        CancellationToken token, ref long yields)
    {
        double* elements = null;
        int count = 0, capacity = 0;
        try
        {
            while (cursor.Next(arena, token, out double value))
            {
                if (count == capacity)
                {
                    capacity = capacity == 0 ? 64 : checked(capacity * 2);
                    elements = (double*)Buffer.Realloc(elements, (nuint)capacity * sizeof(double));
                }
                elements[count++] = value;
                yields++;
            }
            NativeType number = NativeType.Of(NativeKind.Number);
            NativeLayout element = new(number);
            var counters = (currentWork.Value ?? executionWork).Counters;
            NativeAllocation backing = guestHeap.NewObject(element, -1, count, 0, counters);
            new ReadOnlySpan<double>(elements, count).CopyTo(new Span<double>((void*)backing.Storage.RawAddress, count));
            backing.Storage.InitializationSpan.Fill(true);
            return new(NativeValue.FromRange(NativeRange.FromGuestSnapshot(backing.Address, count, element, TypeRef.Number)));
        }
        finally { Buffer.Free(elements); }
    }

    private void StartRangePlan(Activation activation, int index, NativeGuestObjectTable arena, nint edge, CancellationToken token)
    {
        ref NativeRangeNode node = ref NativeRangeNode.At(edge);
        if (node.Op == NativeRangeOp.Sequence && arena.UsesRangeToken(edge, token))
        {
            activation.Frame.SequenceCursor(index) = new(new NativeNumberSequence(node.Operands.First, node.Operands.Limit, node.Operands.Step, token, node.Inclusive));
            activation.Work.Counters.SequenceOpen();
            return;
        }
        ref NativeRangeCursor cursor = ref activation.Frame.RangeCursor(index);
        cursor = NativeRangeCursor.Open(edge, arena, token);
        activation.Work.Counters.RangePlanOpen(cursor.Bytes, cursor.HasRootWindowKernel);
    }
}
