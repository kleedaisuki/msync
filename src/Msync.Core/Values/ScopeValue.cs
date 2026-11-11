using System.Runtime.CompilerServices;

namespace Msync.Core;

public sealed record ScopeValue : Value, IReadOnlyDictionary<string, Value>
{
    private object fields;
    private readonly int rowOffset;
    public bool Equals(ScopeValue? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public ScopeValue(IReadOnlyDictionary<string, Value> members)
    {
        var names = new string[members.Count];
        Value[] values = new Value[members.Count];
        fields = values;
        int index = 0;
        foreach (var member in members) { names[index] = member.Key; values[index++] = member.Value; }
        Layout = ScopeLayout.Own(names);
    }
    public ScopeValue(ScopeLayout layout, Value[] fields) : this(layout, fields, 0)
    {
        if (fields.Length != layout.Count) throw new ArgumentException("Scope field storage disagrees with its layout.", nameof(fields));
    }

    private ScopeValue(ScopeLayout layout, Value[] fields, int allocationSite)
    {
        Layout = layout;
        this.fields = fields;
        AllocationSite = allocationSite;
    }

    internal static ScopeValue FromOwnedFields(ScopeLayout layout, Value[] fields, int allocationSite) =>
        new(layout, fields, allocationSite);
    internal ScopeValue(ScopeLayout layout, PackedScopeStorage fields, int rowOffset)
    {
        Layout = layout;
        this.fields = fields;
        this.rowOffset = rowOffset;
    }
    internal bool IsPacked => fields is PackedScopeStorage;
    public ScopeValue(IReadOnlyDictionary<string, Value> members, int allocationSite) : this(members) => AllocationSite = allocationSite;
    public ScopeLayout Layout { get; }
    public IReadOnlyDictionary<string, Value> Members => this;
    public int AllocationSite { get; init; }
    public int Count => Layout.Count;
    public IEnumerable<string> Keys => Layout.Names;
    public IEnumerable<Value> Values => Enumerable.Range(0, Count).Select(GetField);
    public Value this[string name] => GetField(Layout.GetOffset(name));
    public bool ContainsKey(string name) => Layout.TryGetOffset(name, out _);
    public bool TryGetValue(string name, out Value value)
    {
        if (Layout.TryGetOffset(name, out int offset)) { value = GetField(offset); return true; }
        value = Value.Null; return false;
    }
    public Value GetField(int offset) => Read(fields is Value[] direct ? direct[offset]
        : ((PackedScopeStorage)fields).GetField(rowOffset, offset));
    // Null denotes a raw numeric operand, not the language's Null value.
    public Value? GetOperandField(int offset, out double number)
    {
        if (fields is PackedScopeStorage packed)
        {
            var value = packed.GetOperandField(rowOffset, offset, out number);
            return value is null ? null : Read(value);
        }
        number = 0;
        return Read(((Value[])fields)[offset]);
    }
    public bool TryGetNumber(int offset, out double number)
    {
        if (fields is PackedScopeStorage packed) return packed.TryGetNumber(rowOffset, offset, out number);
        Value value = Read(((Value[])fields)[offset]);
        if (value is NumberValue numeric) { number = numeric.Data; return true; }
        number = 0;
        return false;
    }
    public void SetNumber(int offset, double number)
    {
        if (fields is PackedScopeStorage packed) { packed.SetNumber(rowOffset, offset, number); return; }
        SetField(offset, new NumberValue(number));
    }
    public bool IsField(int offset, string name) => Layout.IsField(offset, name);
    public bool TryGetFieldOffset(string name, out int offset) => Layout.TryGetOffset(name, out offset);
    public bool TryGetFieldCell(int offset, out CellValue cell)
    {
        CellValue? captured = fields is Value[] direct ? direct[offset] as CellValue
            : ((PackedScopeStorage)fields).GetCell(offset);
        cell = captured!;
        return captured is not null;
    }
    public void SetField(int offset, Value value)
    {
        if (fields is PackedScopeStorage packed)
        {
            if (!packed.IsNumeric(offset)) { packed.SetField(offset, value); return; }
            if (value is NumberValue number) { packed.SetNumber(rowOffset, offset, number.Data); return; }
            fields = packed.Materialize(rowOffset);
        }
        Value[] direct = (Value[])fields;
        if (direct[offset] is CellValue cell) cell.Data = value;
        else direct[offset] = value;
    }
    public void SetMember(string name, Value value)
    {
        if (!Layout.TryGetOffset(name, out int offset)) throw new MsyncException($"Scope has no member '{name}'");
        SetField(offset, value);
    }
    public ScopeValue MapFields(Func<Value, Value> transform)
    {
        var values = new Value[Count];
        for (int index = 0; index < values.Length; index++) values[index] = transform(GetField(index));
        return FromOwnedFields(Layout, values, AllocationSite);
    }
    private static Value Read(Value value) => value is CellValue cell ? cell.Data : value;
    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<string, Value>> IEnumerable<KeyValuePair<string, Value>>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public struct Enumerator(ScopeValue scope) : IEnumerator<KeyValuePair<string, Value>>
    {
        private int position = -1;
        public readonly KeyValuePair<string, Value> Current => KeyValuePair.Create(scope.Layout.Names[position], scope.GetField(position));
        readonly object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++position < scope.Count;
        public void Reset() => position = -1;
        public readonly void Dispose() { }
    }
    public override TypeRef Type => TypeRef.Scope;
    public override string ToString() => "scope { " + string.Join(", ", Members.Keys) + " }";
}
