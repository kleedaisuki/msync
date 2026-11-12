using System.Runtime.CompilerServices;

namespace Msync.Core;

public sealed record ScopeValue : Value, IReadOnlyDictionary<string, Value>
{
    private const int SharedLiteralFields = -1;
    private ScopePayload payload;
    public bool Equals(ScopeValue? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public ScopeValue(IReadOnlyDictionary<string, Value> members)
    {
        var names = new string[members.Count];
        Value[] values = new Value[members.Count];
        payload = new(values);
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
        payload = new(fields);
        AllocationSite = allocationSite;
    }

    private ScopeValue(ScopeValue original) : base(original)
    {
        // A host record copy has always shared the original field storage.
        // Detach that pair from other literal instances before sharing it.
        if (Volatile.Read(ref original.payload.Offset) == SharedLiteralFields) original.DetachLiteralFields();
        payload = original.payload;
        Layout = original.Layout;
        AllocationSite = original.AllocationSite;
    }

    internal static ScopeValue FromOwnedFields(ScopeLayout layout, Value[] fields, int allocationSite) =>
        new(layout, fields, allocationSite);

    internal static ScopeValue FromLiteralFields(ScopeLayout layout, Value[] fields, int allocationSite) =>
        new(layout, new ScopePayload(fields, SharedLiteralFields), allocationSite);

    internal ScopeValue(ScopeLayout layout, ScopePayload payload, int allocationSite)
    {
        Layout = layout;
        this.payload = payload;
        AllocationSite = allocationSite;
    }

    internal ReadOnlySpan<Value?> RetainedReferences => payload.RetainedReferences;
    internal bool IsPacked => payload.IsPacked;
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
    public Value GetField(int offset) => payload.GetField(offset);

    // Null denotes a raw numeric operand, not the language's Null value.
    public Value? GetOperandField(int offset, out double number) => payload.GetOperandField(offset, out number);
    public bool TryGetNumber(int offset, out double number) => payload.TryGetNumber(offset, out number);
    public void SetNumber(int offset, double number)
    {
        if (Volatile.Read(ref payload.Offset) == SharedLiteralFields) DetachLiteralFields();
        payload.SetNumber(offset, number);
    }
    public bool IsField(int offset, string name) => Layout.IsField(offset, name);
    public bool TryGetFieldOffset(string name, out int offset) => Layout.TryGetOffset(name, out offset);
    public bool TryGetFieldCell(int offset, out CellValue cell)
    {
        cell = payload.GetCell(offset)!;
        return cell is not null;
    }
    public void SetField(int offset, Value value)
    {
        if (Volatile.Read(ref payload.Offset) == SharedLiteralFields) DetachLiteralFields();
        payload.SetField(offset, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DetachLiteralFields()
    {
        // Competing first writers must converge on one private array.
        lock (this)
        {
            if (payload.Offset != SharedLiteralFields) return;
            payload.DetachLiteral();
            Volatile.Write(ref payload.Offset, 0);
        }
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
