namespace Msync.Core;

// Field storage is independent of the optional identity exposed to guest code.
internal struct ScopePayload(object memory, int offset = 0)
{
    private object memory = memory;
    internal int Offset = offset;

    internal readonly bool IsPacked => memory is PackedScopeStorage;
    internal readonly ReadOnlySpan<Value?> RetainedReferences => memory is Value[] values
        ? values : ((PackedScopeStorage)memory).RetainedReferences;

    internal readonly Value GetField(int field) => Read(memory is Value[] values
        ? values[field] : ((PackedScopeStorage)memory).GetField(Offset, field));

    internal readonly Value? GetOperandField(int field, out double number)
    {
        if (memory is PackedScopeStorage packed)
        {
            var value = packed.GetOperandField(Offset, field, out number);
            return value is null ? null : Read(value);
        }
        number = 0;
        return Read(((Value[])memory)[field]);
    }

    internal readonly bool TryGetNumber(int field, out double number)
    {
        if (memory is PackedScopeStorage packed) return packed.TryGetNumber(Offset, field, out number);
        if (Read(((Value[])memory)[field]) is NumberValue value)
        {
            number = value.Data;
            return true;
        }
        number = 0;
        return false;
    }

    internal readonly CellValue? GetCell(int field) => memory is Value[] values
        ? values[field] as CellValue : ((PackedScopeStorage)memory).GetCell(field);

    internal void SetNumber(int field, double value)
    {
        if (memory is PackedScopeStorage packed) packed.SetNumber(Offset, field, value);
        else SetField(field, new NumberValue(value));
    }

    internal void SetField(int field, Value value)
    {
        if (memory is PackedScopeStorage packed)
        {
            if (!packed.IsNumeric(field)) { packed.SetField(field, value); return; }
            if (value is NumberValue number) { packed.SetNumber(Offset, field, number.Data); return; }
            memory = packed.Materialize(Offset);
        }
        var values = (Value[])memory;
        if (values[field] is CellValue cell) cell.Data = value;
        else values[field] = value;
    }

    internal void DetachLiteral() => memory = ((Value[])memory).Clone();

    private static Value Read(Value value) => value is CellValue cell ? cell.Data : value;
}
