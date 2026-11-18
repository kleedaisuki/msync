using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Buffer = System.Runtime.InteropServices.NativeMemory;

namespace Msync.Core.Native;

internal enum NativeRangeOp : byte { Sequence, Repeat, Take, Drop, Stride, Concat, Characters, Lines, Words, Snapshot, Transform, Filter, Sort, Merge, Window }

// A selection progression and a separate upstream demand fence.
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRangeWindow(ulong Start, ulong Interval, ulong Fence)
{
    internal static NativeRangeWindow All => new(0, 1, ulong.MaxValue);

    internal bool TryAppend(NativeRangeOp operation, ulong count, out NativeRangeWindow window)
    {
        window = this;
        switch (operation)
        {
            case NativeRangeOp.Take:
                if (count == 0) { window = new(0, 1, 0); return true; }
                UInt128 stop = (UInt128)Start + (UInt128)Interval * (count - 1) + 1;
                if (Fence == ulong.MaxValue && stop >= ulong.MaxValue) return false;
                if (stop < Fence) window = this with { Fence = (ulong)stop };
                return true;
            case NativeRangeOp.Drop:
                UInt128 start = (UInt128)Start + (UInt128)Interval * count;
                if (Fence != ulong.MaxValue && start >= Fence) start = Fence;
                if (start >= ulong.MaxValue) return false;
                window = this with { Start = (ulong)start };
                return true;
            case NativeRangeOp.Stride:
                UInt128 interval = (UInt128)Interval * count;
                if (interval >= ulong.MaxValue) return false;
                window = this with { Interval = (ulong)interval };
                return true;
            default:
                return false;
        }
    }
}

[StructLayout(LayoutKind.Explicit)]
internal struct NativeRangeOperands
{
    [FieldOffset(0)] internal NativeRangeWindow Window;
    [FieldOffset(0)] internal double First;
    [FieldOffset(8)] internal double Limit;
    [FieldOffset(16)] internal double Step;
}

// Child edges are tagged arena addresses, not managed iterators.
internal struct NativeRangeNode
{
    internal NativeRangeOp Op;
    internal bool Inclusive, HasCancellation, StringItems, UsesValues, HasCallbacks;
    internal int Depth, Shape;
    internal NativeOffset Offset, Stride;
    internal nint Left, Right;
    internal long Count;
    internal NativeRangeOperands Operands;

    internal static unsafe ref NativeRangeNode At(nint edge) =>
        ref *(NativeRangeNode*)(edge & ~7);
}

internal unsafe struct NativeRangeCursor
{
    private struct Entry
    {
        internal nint Edge;
        internal long Remaining;
        internal ulong Skip;
        internal bool Second, CheckOrigin, CheckSequenceOrigin;
        internal NativeSequenceCursor Sequence;
        internal Workspace* Work;
    }

    private struct Workspace
    {
        internal NativeRangeCursor Left, Right;
        internal Row* Rows;
        internal int Length, Position, Pull;
        internal Row First, Second;
        internal bool HasFirst, HasSecond;
    }

    internal static readonly NativeType WorkspaceType = NativeType.FromTypeRef(TypeRef.Any);

    private Entry* entries;
    private int count, resume;
    private Row saved;
    private bool accepted;
    internal nint Scratch;
    internal bool Requested { get; private set; }
    internal nint CallbackCell { get; private set; }
    internal nint Root { get; private set; }
    internal int Bytes => NativeRangeNode.At(Root).Depth * sizeof(Entry);
    internal bool HasRootWindowKernel => NativeRangeNode.At(Root).Op == NativeRangeOp.Window
        && (entries[0].Sequence.State & NativeSequenceCursor.Open) != 0;

    // Each cursor is opened and driven under the same consumer cancellation token.
    internal static NativeRangeCursor Open(nint root, NativeGuestObjectTable arena, CancellationToken token)
    {
        NativeRangeCursor cursor = new() { Root = root };
        cursor.entries = (Entry*)Buffer.Alloc((nuint)cursor.Bytes);
        cursor.Push(root, arena, token);
        return cursor;
    }

    private void Push(nint edge, NativeGuestObjectTable arena, CancellationToken token)
    {
        ref NativeRangeNode node = ref NativeRangeNode.At(edge);
        ref NativeRangeNode source = ref node;
        if (node.Op == NativeRangeOp.Window) source = ref NativeRangeNode.At(node.Left);
        entries[count++] = new()
        {
            Edge = edge,
            CheckOrigin = node.HasCancellation && !arena.UsesRangeToken(edge, token),
            CheckSequenceOrigin = node.Op == NativeRangeOp.Window && source.Op == NativeRangeOp.Sequence
                && source.HasCancellation && !arena.UsesRangeToken(node.Left, token),
            Remaining = node.Op == NativeRangeOp.Window ? unchecked((long)node.Operands.Window.Fence) : node.Count,
            Skip = node.Op == NativeRangeOp.Window ? node.Operands.Window.Start : 0,
            Sequence = source.Op == NativeRangeOp.Sequence
                ? new(new NativeNumberSequence(source.Operands.First, source.Operands.Limit, source.Operands.Step, default, source.Inclusive)) : default
        };
    }

    internal bool Next(NativeGuestObjectTable arena, CancellationToken token, out double value)
    {
        bool has = Advance(arena, token, out var row);
        value = row.Number;
        return has;
    }

    internal bool NextValue(NativeGuestObjectTable arena, CancellationToken token, out NativeValue value)
    {
        while (Advance(arena, token, out var row))
        {
            if (!Requested) { value = Value(arena, in row); return true; }
            arena.RunRangeCallback!(ref this, Value(arena, in row), token);
        }
        value = default;
        return false;
    }

    private static NativeValue Value(NativeGuestObjectTable arena, in Row row)
    {
        if (row.Cell != 0)
        {
            NativeType type = row.Shape == 0 ? WorkspaceType : arena.RangeShape(row.Shape).Physical;
            return NativeStorage.LoadInitialized(type, arena.Resolve(row.Cell).AddressAt(row.Offset, type));
        }
        return row.Text == 0 ? NativeValue.FromNumber(row.Number)
            : NativeValue.FromTextEdge(arena, row.Text).BuildString(row.Length < 0 ? "\uFFFD".AsSpan()
                : NativeGuestObjectTable.TextSpan(row.Text).Slice(row.Start, row.Length));
    }

    internal bool Filtering => NativeRangeNode.At(entries[resume - 1].Edge).Op == NativeRangeOp.Filter;

    internal void CompleteCallback(bool keep, bool replace)
    {
        if (replace) saved = new(0, Cell: Scratch);
        accepted = keep;
        Requested = false;
    }

    private readonly record struct Row(double Number, nint Text = 0, int Start = 0, int Length = 0,
        nint Cell = 0, NativeOffset Offset = default, int Shape = 0);

    private bool Advance(NativeGuestObjectTable arena, CancellationToken token, out Row row)
    {
        row = default;
        while (count != 0)
        {
            token.ThrowIfCancellationRequested();
            int parentIndex;
            bool keep;
            if (resume != 0)
            {
                row = saved;
                parentIndex = resume - 2;
                keep = accepted;
                resume = 0;
            }
            else
            {
                int exhaustedWindow = -1;
                for (int i = 0; i < count; i++)
                {
                    ref Entry entry = ref entries[i];
                    ref NativeRangeNode node = ref NativeRangeNode.At(entry.Edge);
                    if (entry.CheckOrigin) arena.CheckRangeCancellation(entry.Edge);
                    if (node.Op == NativeRangeOp.Window && entry.Remaining == 0)
                    {
                        exhaustedWindow = i;
                        break;
                    }
                }
                if (exhaustedWindow >= 0)
                {
                    while (count > exhaustedWindow + 1) Release(ref entries[--count]);
                    Finish();
                    continue;
                }

                ref Entry leaf = ref entries[count - 1];
                ref NativeRangeNode plan = ref NativeRangeNode.At(leaf.Edge);
                if (plan.Op == NativeRangeOp.Sequence)
                {
                    if (!leaf.Sequence.Next(out double value)) { Finish(); continue; }
                    row = new(value);
                }
                else if (plan.Op == NativeRangeOp.Window && (leaf.Sequence.State & NativeSequenceCursor.Open) != 0)
                {
                    if (!PullWindowSequence(ref leaf, in plan, arena, token, out double item)) { Finish(); continue; }
                    row = new(item);
                }
                else if (plan.Op == NativeRangeOp.Repeat)
                {
                    if (leaf.Remaining == 0) { Finish(); continue; }
                    leaf.Remaining--;
                    row = new(plan.Operands.First);
                }
                else if (plan.Op is NativeRangeOp.Sort or NativeRangeOp.Merge)
                {
                    if (!NextOrdered(ref leaf, in plan, arena, token, out row)) { Finish(); continue; }
                }
                else if (plan.Op == NativeRangeOp.Snapshot)
                {
                    if (leaf.Remaining == 0) { Finish(); continue; }
                    int index = checked((int)(plan.Count - leaf.Remaining--));
                    row = new(0, Cell: plan.Left, Offset: plan.Offset + plan.Stride * index, Shape: plan.Shape);
                }
                else if (plan.Op is NativeRangeOp.Characters or NativeRangeOp.Lines or NativeRangeOp.Words)
                {
                    if (!NextSlice(ref leaf.Remaining, in plan, out row)) { Finish(); continue; }
                }
                else
                {
                    Push(leaf.Second ? plan.Right : plan.Left, arena, token);
                    continue;
                }

                parentIndex = count - 2;
                keep = true;
            }

            for (int i = parentIndex; i >= 0 && keep; i--)
            {
                ref Entry parent = ref entries[i];
                ref NativeRangeNode op = ref NativeRangeNode.At(parent.Edge);
                switch (op.Op)
                {
                    case NativeRangeOp.Transform:
                    case NativeRangeOp.Filter:
                        resume = i + 1;
                        saved = row;
                        CallbackCell = op.Right;
                        Requested = true;
                        return true;
                    case NativeRangeOp.Window: keep = Select(ref parent, op.Operands.Window.Interval); break;
                }
            }
            if (keep) return true;
        }
        return false;
    }

    private static bool Select(ref Entry cursor, ulong interval)
    {
        if (cursor.Remaining != -1) cursor.Remaining = unchecked(cursor.Remaining - 1);
        if (cursor.Skip != 0) { cursor.Skip--; return false; }
        cursor.Skip = interval - 1;
        return true;
    }

    private static bool PullWindowSequence(ref Entry cursor, in NativeRangeNode plan, NativeGuestObjectTable arena,
        CancellationToken token, out double value)
    {
        NativeRangeWindow window = plan.Operands.Window;
        while (cursor.Remaining != 0)
        {
            token.ThrowIfCancellationRequested();
            if (cursor.CheckOrigin) arena.CheckRangeCancellation(cursor.Edge);
            if (cursor.CheckSequenceOrigin) arena.CheckRangeCancellation(plan.Left);
            if (!cursor.Sequence.Next(out value)) return false;
            if (Select(ref cursor, window.Interval)) return true;
        }
        value = default;
        return false;
    }

    private static bool NextSlice(ref long position, in NativeRangeNode plan, out Row row)
    {
        ReadOnlySpan<char> text = NativeGuestObjectTable.TextSpan(plan.Left);
        int start = (int)position;
        row = default;
        if (start == text.Length) return false;
        if (plan.Op == NativeRangeOp.Characters)
        {
            OperationStatus status = Rune.DecodeFromUtf16(text[start..], out _, out int used);
            position = start + used;
            row = new(0, plan.Left, start, status == OperationStatus.Done ? used : -1);
            return true;
        }
        if (plan.Op == NativeRangeOp.Lines)
        {
            int width = text[start..].IndexOfAny('\r', '\n');
            int end = width < 0 ? text.Length : start + width;
            position = end == text.Length ? end : end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            row = new(0, plan.Left, start, end - start);
            return true;
        }
        int offset = start, first = -1;
        foreach (Rune rune in text[start..].EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)) { if (first < 0) first = offset; }
            else if (first >= 0) break;
            offset += rune.Utf16SequenceLength;
        }
        position = offset;
        if (first < 0) return false;
        row = new(0, plan.Left, first, offset - first);
        return true;
    }

    private static bool NextOrdered(ref Entry entry, in NativeRangeNode node, NativeGuestObjectTable arena,
        CancellationToken token, out Row row)
    {
        Workspace* work = entry.Work;
        if (work == null)
        {
            work = entry.Work = (Workspace*)Buffer.AllocZeroed((nuint)sizeof(Workspace));
            work->Left = Open(node.Left, arena, token);
            if (node.Op == NativeRangeOp.Sort) FillSorted(work, arena, token);
            else
            {
                work->Right = Open(node.Right, arena, token);
                work->Pull = 3;
            }
        }

        row = default;
        if (node.Op == NativeRangeOp.Sort)
        {
            if (work->Position == work->Length) return false;
            row = work->Rows[work->Position++];
            return true;
        }

        if ((work->Pull & 1) != 0) work->HasFirst = work->Left.Advance(arena, token, out work->First);
        if ((work->Pull & 2) != 0) work->HasSecond = work->Right.Advance(arena, token, out work->Second);
        if (!work->HasFirst && !work->HasSecond) return false;
        bool right = !work->HasFirst || work->HasSecond && Less(arena, in work->Second, in work->First);
        row = right ? work->Second : work->First;
        work->Pull = right ? 2 : 1;
        return true;
    }

    private static bool Less(NativeGuestObjectTable arena, in Row first, in Row second)
    {
        if (first.Text != 0 && second.Text != 0)
            return TextSemantics.CompareOrdinal(Slice(in first), Slice(in second)) < 0;
        return arena.CompareRangeValues!(Value(arena, in first), Value(arena, in second)) < 0;
    }

    private static ReadOnlySpan<char> Slice(in Row row) => row.Length < 0 ? "\uFFFD".AsSpan()
        : NativeGuestObjectTable.TextSpan(row.Text).Slice(row.Start, row.Length);

    private static void FillSorted(Workspace* work, NativeGuestObjectTable arena, CancellationToken token)
    {
        int capacity = 0;
        while (work->Left.Advance(arena, token, out Row item))
        {
            if (work->Length == capacity)
            {
                capacity = capacity == 0 ? 32 : checked(capacity * 2);
                work->Rows = (Row*)Buffer.Realloc(work->Rows, (nuint)capacity * (nuint)sizeof(Row));
            }
            work->Rows[work->Length++] = item;
        }
        work->Left.Close();
        int length = work->Length;
        if (length < 2) return;
        Row* other = (Row*)Buffer.Alloc((nuint)length * (nuint)sizeof(Row));
        try
        {
            for (int width = 1; width < length; width = width > length / 2 ? length : width * 2)
            {
                for (int start = 0; start < length;)
                {
                    int middle = start + Math.Min(width, length - start), end = middle + Math.Min(width, length - middle);
                    int a = start, b = middle;
                    for (int i = start; i < end; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        bool right = b < end && (a == middle || Less(arena, in work->Rows[b], in work->Rows[a]));
                        other[i] = work->Rows[right ? b++ : a++];
                    }
                    start = end;
                }
                Row* swap = work->Rows;
                work->Rows = other;
                other = swap;
            }
        }
        finally { Buffer.Free(other); }
    }

    private static void Release(ref Entry entry)
    {
        Workspace* work = entry.Work;
        if (work == null) return;
        work->Left.Close();
        work->Right.Close();
        Buffer.Free(work->Rows);
        Buffer.Free(work);
        entry.Work = null;
    }

    private void Finish()
    {
        while (count != 0)
        {
            Release(ref entries[--count]);
            if (count == 0) break;
            ref Entry parent = ref entries[count - 1];
            if (NativeRangeNode.At(parent.Edge).Op != NativeRangeOp.Concat || parent.Second) continue;
            parent.Second = true;
            return;
        }
    }

    internal void Close()
    {
        while (count != 0) Release(ref entries[--count]);
        Buffer.Free(entries);
        this = default;
    }

}

internal static class NativeRangePlanAdapter
{
    // Only materialized host callers and remaining non-native consumers use this adapter.
    internal static async IAsyncEnumerable<NativeValue> Export(NativeGuestObjectTable arena, nint edge,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        NativeRangeCursor cursor = NativeRangeCursor.Open(edge, arena, token);
        try
        {
            while (cursor.NextValue(arena, token, out NativeValue value))
                yield return value;
        }
        finally { cursor.Close(); }
        await Task.CompletedTask;
    }
}
