using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

internal struct OperandStack<TFrame>(TFrame frame) where TFrame : struct, RuntimeProfile.IFrame
{
    // A null slot denotes an unboxed Number; language null remains Value.Null.
    private Value?[] items = new Value?[4];
    // Literal ranges grow the reference stack without paying for numeric scratch.
    private double[] numbers = [];
    public int Count { get; private set; }
    public Value this[int index] => items[index] ?? BoxNumber(index);
    public Value Peek(int depth) => this[Count - depth - 1];
    public Value? PeekReference(int depth) => items[Count - depth - 1];
    public bool HasRawTop => items[Count - 1] is null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double PopRawNumber() => numbers[--Count];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeekNumber(int depth, out double number)
    {
        int index = Count - depth - 1;
        switch (items[index])
        {
            case null: number = numbers[index]; return true;
            case NumberValue value: number = value.Data; return true;
            default: number = 0; return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Value value)
    {
        if (Count == items.Length) Grow();
        items[Count++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddNumber(double number)
    {
        if (Count == items.Length) Grow();
        if (Count >= numbers.Length) GrowNumbers();
        items[Count] = null;
        numbers[Count++] = number;
    }

    // Only a bound WordTransport edge can read this slot. The carrier
    // reuses the primitive stack storage as bits, without a runtime tag.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddWord32(uint word) => AddWordPayload(BitConverter.Int64BitsToDouble(word));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddWordPayload(double payload)
    {
        AddNumber(payload);
        frame.ManagedWordEdge();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPeekWord32(int depth, out uint word)
    {
        int index = Count - depth - 1;
        switch (items[index])
        {
            case null: word = (uint)BitConverter.DoubleToInt64Bits(numbers[index]); return true;
            // A selected producer can have returned through the canonical
            // invocation path, so a materialized exact result is also valid.
            case NumberValue value: word = (uint)value.Data; return true;
            default: word = 0; return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPeekWordPayload(out double payload)
    {
        int index = Count - 1;
        switch (items[index])
        {
            case null: payload = numbers[index]; return true;
            case NumberValue value: payload = BitConverter.Int64BitsToDouble((uint)value.Data); return true;
            default: payload = 0; return false;
        }
    }

    internal void WidenWordArguments(int count, WordTransport transport)
    {
        int mask = (int)transport & 15;
        int start = Count - count;
        for (int position = 0; mask != 0; position++, mask >>= 1)
        {
            int index = start + position;
            if ((mask & 1) != 0 && items[index] is null)
                numbers[index] = (uint)BitConverter.DoubleToInt64Bits(numbers[index]);
        }
    }

    public void Duplicate()
    {
        if (Count == items.Length) Grow();
        Value? item = items[Count - 1];
        if (item is null)
        {
            if (Count >= numbers.Length) GrowNumbers();
            numbers[Count] = numbers[Count - 1];
        }
        items[Count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value Pop()
    {
        Value? item = items[--Count];
        items[Count] = null;
        return item ?? BoxNumber(Count);
    }

    public Value[] PopMany(int count)
    {
        if (count == 0) return [];
        Value[] values = new Value[count];
        for (int index = count - 1; index >= 0; index--) values[index] = Pop();
        return values;
    }

    public Value[] PopArguments(int count)
    {
        if (count == 0) return [];
        Value[] arguments = new Value[count];
        for (int index = count - 1; index >= 0; index--)
        {
            Value value = Pop();
            arguments[index] = value is CellValue cell ? cell.Data : value;
        }
        return arguments;
    }

    public ArraySegment<Value?> CopyArguments(int count, Value[] locals, int destination)
    {
        int start = Count - count;
        for (int index = 0; index < count; index++)
        {
            int source = start + index;
            Value? value = items[source];
            if (value is CellValue cell) items[source] = value = cell.Data;
            locals[destination + index] = value ?? BoxNumber(source);
        }
        return new(items, start, count);
    }

    public ArraySegment<Value?> BorrowArguments(int count, Value? capture, out double[]? raw)
    {
        int start = Count - count;
        if (capture is not null) items[start - 1] = capture;
        raw = null;
        for (int index = start; index < Count; index++)
        {
            var value = items[index];
            if (value is null) raw = numbers;
            else if (value is CellValue cell) items[index] = cell.Data;
        }
        return new(items, start, count);
    }

    public void CompleteCall(int arguments, Value result)
    {
        Count -= arguments;
        items[Count - 1] = result;
        if (arguments != 0) Array.Clear(items, Count, arguments);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CompleteNumberCall(int arguments, double result)
    {
        Count -= arguments;
        int index = Count - 1;
        if (index >= numbers.Length) GrowNumbers();
        numbers[index] = result;
        items[index] = null;
        if (arguments == 1) items[Count] = null;
        else if (arguments != 0) Array.Clear(items, Count, arguments);
    }

    public ScopeValue PopScope(ScopeAllocator allocator, ScopeLayout layout, int count, int allocationSite) =>
        new(layout, PopScopePayload(allocator, layout, count, allocationSite), allocationSite);

    internal ScopePayload PopScopePayload(ScopeAllocator allocator, ScopeLayout layout, int count, int allocationSite)
    {
        int start = Count - count;
        ReadOnlySpan<double> numeric = start < numbers.Length
            ? numbers.AsSpan(start, Math.Min(count, numbers.Length - start)) : [];
        ScopePayload value = allocator.CreatePayload(layout, items.AsSpan(start, count), numeric, allocationSite);
        Array.Clear(items, start, count);
        Count = start;
        return value;
    }

    public void Discard() => items[--Count] = null;

    public void Discard(int count)
    {
        while (count-- != 0) Discard();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopNumbers(out double left, out double right)
    {
        Value? first = items[Count - 2];
        Value? second = items[Count - 1];
        if ((first is null or NumberValue) && (second is null or NumberValue))
        {
            right = second is NumberValue rightValue ? rightValue.Data : numbers[Count - 1];
            left = first is NumberValue leftValue ? leftValue.Data : numbers[Count - 2];
            items[--Count] = null;
            items[--Count] = null;
            return true;
        }
        left = right = 0;
        return false;
    }

    public bool TryPopNumber(out double number)
    {
        Value? item = items[Count - 1];
        if (item is null or NumberValue)
        {
            number = item is NumberValue value ? value.Data : numbers[Count - 1];
            items[--Count] = null;
            return true;
        }
        number = 0;
        return false;
    }

    private void Grow() => Array.Resize(ref items, items.Length * 2);
    private void GrowNumbers() => Array.Resize(ref numbers, Math.Max(4, Math.Max(Count + 1, numbers.Length * 2)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NumberValue BoxNumber(int index)
    {
        frame.Box();
        return new(numbers[index]);
    }
}
