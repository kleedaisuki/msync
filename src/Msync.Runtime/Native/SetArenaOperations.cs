using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

internal static class SetArenaOperations
{
    internal static Value Invoke(ArenaOperation operation, IReadOnlyList<Value> args, RuntimeContext context)
    {
        if (operation == ArenaOperation.New) return SetArena.Create(((TypeValue)args[0]).Data);
        var owner = (SetArena)args[0];
        int Index(int position) => ExactIndex(((NumberValue)args[position]).Data);
        bool Flag(int position) => ((BoolValue)args[position]).Data;

        switch (operation)
        {
            case ArenaOperation.Root: return new NumberValue(owner.Root);
            case ArenaOperation.Size: return new NumberValue(owner.Count);
            case ArenaOperation.Key: return owner.Key(Index(1));
            case ArenaOperation.Child: return new NumberValue(owner.Child(Index(1), Flag(2)));
            case ArenaOperation.Parent: return new NumberValue(owner.Parent(Index(1)));
            case ArenaOperation.Red: return owner.Red(Index(1)) ? Value.True : Value.False;
            case ArenaOperation.Allocate:
                return new NumberValue(owner.Allocate(KeyFor(owner, args[1], context), Index(2)));
            case ArenaOperation.Release: owner.Release(Index(1)); break;
            case ArenaOperation.Clear: owner.Clear(); break;
            case ArenaOperation.SetRoot: owner.SetRoot(Index(1)); break;
            case ArenaOperation.SetChild: owner.SetChild(Index(1), Flag(2), Index(3)); break;
            case ArenaOperation.SetParent: owner.SetParent(Index(1), Index(2)); break;
            case ArenaOperation.SetRed: owner.SetRed(Index(1), Flag(2)); break;
            default: throw new MsyncException($"Unknown arena operation '{operation}'.");
        }
        return Value.Null;
    }

    internal static Value KeyFor(SetArena owner, Value key, RuntimeContext context) => key switch
    {
        StringValue when (owner.ElementType.Kind & ~TypeKind.Null) == TypeKind.Path => context.AsPath(key),
        PathValue path when (owner.ElementType.Kind & ~TypeKind.Null) == TypeKind.String => new StringValue(path.NativeText),
        _ => key
    };

    internal static int ExactIndex(double value)
    {
        int index = (int)value;
        if (index >= 0 && index == value) return index;
        throw new MsyncException($"Arena index {value} must be an exact nonnegative Int32.");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SetArenaLinks
{
    internal int Left;
    internal int Right;
    internal int Parent;
    internal int Color;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SetArenaRow<TKey>
{
    internal SetArenaLinks Links;
    internal TKey Key;
}

internal abstract record SetArena(TypeRef Carrier) : StorageValue
{
    private int next = 1;
    private int free;
    internal int Root { get; private set; }
    internal int Count { get; private set; }
    protected int End => next;
    public sealed override TypeRef ElementType => Carrier;

    internal static SetArena Create(TypeRef type) => type.Kind switch
    {
        TypeKind.Number => new SetArena<double>(type, static value => ((NumberValue)value).Data, static number => new NumberValue(number)),
        TypeKind.Bool => new SetArena<bool>(type, static value => ((BoolValue)value).Data, static bit => bit ? Value.True : Value.False),
        TypeKind.Number | TypeKind.Null => new SetArena<double?>(type,
            static value => value is NullValue ? null : ((NumberValue)value).Data,
            static number => number.HasValue ? new NumberValue(number.Value) : Value.Null),
        TypeKind.Bool | TypeKind.Null => new SetArena<bool?>(type,
            static value => value is NullValue ? null : ((BoolValue)value).Data,
            static bit => bit.HasValue ? bit.Value ? Value.True : Value.False : Value.Null),
        TypeKind.String or (TypeKind.String | TypeKind.Null) => References<StringValue>(type, false),
        TypeKind.Path or (TypeKind.Path | TypeKind.Null) => References<PathValue>(type, false),
        TypeKind.Scope or (TypeKind.Scope | TypeKind.Null) => References<ScopeValue>(type, true),
        TypeKind.Range or (TypeKind.Range | TypeKind.Null) => References<RangeValue>(type, true),
        TypeKind.Process or (TypeKind.Process | TypeKind.Null) => References<ProcessValue>(type, true),
        TypeKind.Function or (TypeKind.Function | TypeKind.Null) => References<FunctionValue>(type, true),
        TypeKind.Connection or (TypeKind.Connection | TypeKind.Null) => References<ConnectionValue>(type, false),
        TypeKind.Type or (TypeKind.Type | TypeKind.Null) => References<TypeValue>(type, false),
        _ => new SetArena<Value?>(type, static value => value, static value => value ?? Value.Null, hasRoots: true)
    };

    private static SetArena References<T>(TypeRef type, bool hasRoots) where T : Value =>
        new SetArena<T?>(type, static value => value is NullValue ? null : (T)value,
            static value => value is null ? Value.Null : value, hasRoots);

    internal int Child(int index, bool left)
    {
        ref SetArenaLinks node = ref Links(index);
        return left ? node.Left : node.Right;
    }

    internal int Parent(int index) => Links(index).Parent;
    internal bool Red(int index) => Links(index).Color != 0;
    internal void SetRoot(int value) => Root = Link(value);

    internal void SetChild(int index, bool left, int value)
    {
        ref SetArenaLinks node = ref Links(index);
        int link = Link(value);
        if (left) node.Left = link;
        else node.Right = link;
    }
    internal void SetParent(int index, int value) => Links(index).Parent = Link(value);
    internal void SetRed(int index, bool value) => Links(index).Color = value ? 1 : 0;
    internal abstract Value Key(int index);
    internal abstract int Allocate(Value value, int parent);
    protected abstract int Capacity { get; }
    protected abstract ref SetArenaLinks Links(int index);
    protected abstract void Grow(int capacity);
    protected abstract void DropKey(int index);
    protected abstract void DropKeys(int end);

    protected int Reserve(int parent)
    {
        parent = Link(parent);
        int index = free;
        if (index != 0) free = Links(index).Left;
        else
        {
            index = next;
            if (index == Capacity) Grow(checked(index * 2));
            next++;
        }
        Links(index) = new SetArenaLinks { Parent = parent, Color = 1 };
        Count++;
        return index;
    }

    internal void Release(int index)
    {
        if (Link(index) == 0) throw new MsyncException("Cannot release the arena's NIL row.");
        DropKey(index);
        Links(index).Left = free;
        free = index;
        Count--;
    }

    internal void Clear()
    {
        DropKeys(next);
        Links(0) = default;
        Root = Count = free = 0;
        next = 1;
    }

    // Every written link belongs to this extent. Readers can use a proven
    // owner and trust the link instead of checking it after each load.
    private int Link(int index) => (uint)index < (uint)next ? index
        : throw new MsyncException($"Arena link {index} is outside its {next} initialized rows.");
}

internal sealed record SetArena<TKey>(TypeRef type, Func<Value, TKey> lower, Func<TKey, Value> lift, bool hasRoots = false)
    : SetArena(type)
{
    private SetArenaRow<TKey>[] rows = new SetArenaRow<TKey>[4];
    internal Span<SetArenaRow<TKey>> Rows => rows.AsSpan(0, End);

    public bool Equals(SetArena<TKey>? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal TKey RawKey(int index) => rows[index].Key;

    internal int AllocateRaw(TKey key, int parent)
    {
        int index = Reserve(parent);
        rows[index].Key = key;
        return index;
    }

    internal override Value Key(int index) => lift(RawKey(index));
    internal override int Allocate(Value value, int parent) => AllocateRaw(lower(value), parent);

    protected override int Capacity => rows.Length;
    protected override ref SetArenaLinks Links(int index) => ref rows[index].Links;
    protected override void Grow(int capacity) => Array.Resize(ref rows, capacity);
    protected override void DropKey(int index)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) rows[index].Key = default!;
    }

    protected override void DropKeys(int end)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) Array.Clear(rows, 1, end - 1);
    }

    public override IEnumerable<Value> RetainedReferences()
    {
        if (!hasRoots) yield break;
        for (int i = 1; i < End; i++)
            if (rows[i].Key is Value key) yield return key;
    }
}
