using System.Runtime.CompilerServices;

namespace Msync.Core;

public sealed record ScopeValue : Value, IReadOnlyDictionary<string, Value>
{
    private readonly Value[] fields;
    public bool Equals(ScopeValue? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public ScopeValue(IReadOnlyDictionary<string, Value> members)
    {
        var names = new string[members.Count];
        fields = new Value[members.Count];
        int index = 0;
        foreach (var member in members) { names[index] = member.Key; fields[index++] = member.Value; }
        Layout = ScopeLayout.Own(names);
    }
    public ScopeValue(ScopeLayout layout, Value[] fields)
    {
        if (fields.Length != layout.Count) throw new ArgumentException("Scope field storage disagrees with its layout.", nameof(fields));
        Layout = layout;
        this.fields = fields;
    }
    public ScopeValue(IReadOnlyDictionary<string, Value> members, int allocationSite) : this(members) => AllocationSite = allocationSite;
    public ScopeLayout Layout { get; }
    public IReadOnlyDictionary<string, Value> Members => this;
    public int AllocationSite { get; init; }
    public int Count => Layout.Count;
    public IEnumerable<string> Keys => Layout.Names;
    public IEnumerable<Value> Values => fields.Select(Read);
    public Value this[string name] => GetField(Layout.GetOffset(name));
    public bool ContainsKey(string name) => Layout.TryGetOffset(name, out _);
    public bool TryGetValue(string name, out Value value)
    {
        if (Layout.TryGetOffset(name, out int offset)) { value = GetField(offset); return true; }
        value = Value.Null; return false;
    }
    public Value GetField(int offset) => Read(fields[offset]);
    public bool IsField(int offset, string name) => (uint)offset < (uint)Layout.Count
        && string.Equals(Layout.Names[offset], name, StringComparison.Ordinal);
    public bool TryGetFieldOffset(string name, out int offset) => Layout.TryGetOffset(name, out offset);
    public void SetField(int offset, Value value)
    {
        if (fields[offset] is CellValue cell) cell.Data = value;
        else fields[offset] = value;
    }
    public void SetMember(string name, Value value)
    {
        if (!Layout.TryGetOffset(name, out int offset)) throw new MsyncException($"Scope has no member '{name}'");
        SetField(offset, value);
    }
    public ScopeValue MapFields(Func<Value, Value> transform)
    {
        var values = new Value[fields.Length];
        for (int index = 0; index < values.Length; index++) values[index] = transform(GetField(index));
        return new(Layout, values) { AllocationSite = AllocationSite };
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
