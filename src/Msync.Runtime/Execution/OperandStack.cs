using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

internal struct OperandStack
{
    public OperandStack() { }

    // A null slot denotes an unboxed Number; language null remains Value.Null.
    private Value?[] items = new Value?[4];
    // Literal ranges grow the reference stack without paying for numeric scratch.
    private double[] numbers = [];
    public int Count { get; private set; }
    public Value this[int index] => items[index] ?? new NumberValue(numbers[index]);
    public Value Peek(int depth) => this[Count - depth - 1];

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
        return item ?? new NumberValue(numbers[Count]);
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
            locals[destination + index] = value ?? new NumberValue(numbers[source]);
        }
        return new(items, start, count);
    }

    public void CompleteCall(int arguments, Value result)
    {
        Count -= arguments;
        items[Count - 1] = result;
        if (arguments != 0) Array.Clear(items, Count, arguments);
    }

    public ScopeValue PopScope(ScopeAllocator allocator, ScopeLayout layout, int count, int allocationSite)
    {
        int start = Count - count;
        ReadOnlySpan<double> numeric = start < numbers.Length
            ? numbers.AsSpan(start, Math.Min(count, numbers.Length - start)) : [];
        ScopeValue value = allocator.Create(layout, items.AsSpan(start, count), numeric, allocationSite);
        Array.Clear(items, start, count);
        Count = start;
        return value;
    }

    public void Discard() => items[--Count] = null;

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
}
