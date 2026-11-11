namespace Msync.Core;

public sealed class ScopeAllocator
{
    private Dictionary<(ScopeLayout Layout, int Site), Dictionary<ulong, List<NumericScopePool>>>? shapes;

    public ScopeValue Create(ScopeLayout layout, Value[] values) => Create(layout, values.AsSpan());

    public ScopeValue Create(ScopeLayout layout, ReadOnlySpan<Value?> values,
        ReadOnlySpan<double> numbers = default, int allocationSite = 0)
    {
        if (values.Length != layout.Count)
            throw new ArgumentException("Scope field storage disagrees with its layout.", nameof(values));
        int numericCount = 0;
        ulong hash = 14695981039346656037;
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] is not (null or NumberValue)) continue;
            numericCount++;
            hash = (hash ^ (uint)(index + 1)) * 1099511628211;
        }
        if (numericCount < 2)
            return Plain(layout, values, numbers, allocationSite);

        shapes ??= [];
        var key = (layout, allocationSite);
        if (!shapes.TryGetValue(key, out var layouts)) shapes.Add(key, layouts = []);
        if (!layouts.TryGetValue(hash, out var candidates)) layouts.Add(hash, candidates = []);
        foreach (var candidate in candidates)
        {
            if (!candidate.Matches(values, out bool shared)) continue;
            // Repeated immutable references already share their numeric payload.
            return shared ? Plain(layout, values, numbers, allocationSite)
                : candidate.Allocate(layout, allocationSite, values, numbers);
        }
        var shape = new NumericScopePool(values, numericCount);
        candidates.Add(shape);
        return shape.Allocate(layout, allocationSite, values, numbers);
    }

    private static ScopeValue Plain(ScopeLayout layout, ReadOnlySpan<Value?> values,
        ReadOnlySpan<double> numbers, int allocationSite)
    {
        Value[] fields = values.Length == 0 ? [] : new Value[values.Length];
        for (int index = 0; index < fields.Length; index++)
            fields[index] = values[index] ?? new NumberValue(numbers[index]);
        return new(layout, fields) { AllocationSite = allocationSite };
    }
}

internal sealed class NumericScopeShape
{
    private readonly int[] slots;

    internal NumericScopeShape(ReadOnlySpan<Value?> values)
    {
        slots = new int[values.Length];
        int number = 0, reference = 0;
        for (int index = 0; index < slots.Length; index++)
            slots[index] = values[index] is null or NumberValue ? number++ : -++reference;
    }

    internal int Count => slots.Length;
    internal int Slot(int field) => slots[field];
}

internal sealed class NumericScopePool
{
    private const int PageBytes = 8192;
    private readonly NumericScopeShape shape;
    private readonly int numericCount;
    private readonly int referenceCount;
    private readonly int maximumRows;
    private readonly NumberValue?[] previousNumbers;
    private double[]? page;
    private PackedScopeStorage? numericPage;
    private int used;
    private int nextRows = 1;

    internal NumericScopePool(ReadOnlySpan<Value?> values, int numericCount)
    {
        this.numericCount = numericCount;
        referenceCount = values.Length - numericCount;
        previousNumbers = new NumberValue[numericCount];
        maximumRows = Math.Max(1, PageBytes / sizeof(double) / numericCount);
        shape = new(values);
    }

    internal bool Matches(ReadOnlySpan<Value?> values, out bool shared)
    {
        shared = true;
        for (int index = 0; index < shape.Count; index++)
        {
            int slot = shape.Slot(index);
            if ((slot >= 0) != (values[index] is null or NumberValue)) return false;
            if (slot >= 0 && (values[index] is null
                || !ReferenceEquals(values[index], previousNumbers[slot]))) shared = false;
        }
        return true;
    }

    internal ScopeValue Allocate(ScopeLayout layout, int site, ReadOnlySpan<Value?> values, ReadOnlySpan<double> numbers)
    {
        if (page is null || used == page.Length)
        {
            page = new double[checked(nextRows * numericCount)];
            // Reference-free rows can share their storage descriptor and page.
            if (referenceCount == 0) numericPage = new(shape, page, []);
            used = 0;
            nextRows = Math.Min(maximumRows, nextRows * 2);
        }
        int offset = used;
        used += numericCount;
        if (numericPage is not null)
        {
            for (int index = 0; index < values.Length; index++)
            {
                var value = values[index] as NumberValue;
                page[offset + index] = value is null ? numbers[index] : value.Data;
                previousNumbers[index] = value;
            }
            return new(layout, numericPage, offset) { AllocationSite = site };
        }

        var references = new Value[referenceCount];
        for (int index = 0; index < shape.Count; index++)
        {
            int slot = shape.Slot(index);
            if (slot >= 0)
            {
                page[offset + slot] = values[index] is NumberValue value ? value.Data : numbers[index];
                previousNumbers[slot] = values[index] as NumberValue;
            }
            else references[-slot - 1] = values[index]!;
        }
        return new(layout, new PackedScopeStorage(shape, page, references), offset) { AllocationSite = site };
    }
}

internal sealed class PackedScopeStorage(NumericScopeShape shape, double[] page, Value[] references)
{
    internal Value? GetOperandField(int offset, int field, out double number)
    {
        int slot = shape.Slot(field);
        if (slot >= 0) { number = page[offset + slot]; return null; }
        number = 0;
        return references[-slot - 1];
    }

    internal Value GetField(int offset, int field)
    {
        int slot = shape.Slot(field);
        return slot >= 0 ? new NumberValue(page[offset + slot]) : references[-slot - 1];
    }

    internal bool TryGetNumber(int offset, int field, out double number)
    {
        int slot = shape.Slot(field);
        if (slot >= 0) { number = page[offset + slot]; return true; }
        Value value = references[-slot - 1];
        if (value is CellValue cell) value = cell.Data;
        if (value is NumberValue numeric) { number = numeric.Data; return true; }
        number = 0;
        return false;
    }

    internal bool IsNumeric(int field) => shape.Slot(field) >= 0;
    internal CellValue? GetCell(int field) => shape.Slot(field) is var slot && slot < 0
        ? references[-slot - 1] as CellValue : null;

    internal void SetNumber(int offset, int field, double number)
    {
        int slot = shape.Slot(field);
        if (slot >= 0) page[offset + slot] = number;
        else SetField(field, new NumberValue(number));
    }

    internal void SetField(int field, Value value)
    {
        int slot = -shape.Slot(field) - 1;
        if (references[slot] is CellValue cell) cell.Data = value;
        else references[slot] = value;
    }

    internal Value[] Materialize(int offset)
    {
        Value[] values = new Value[shape.Count];
        for (int index = 0; index < values.Length; index++) values[index] = GetField(offset, index);
        return values;
    }
}
