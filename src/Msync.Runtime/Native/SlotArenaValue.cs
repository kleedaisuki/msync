using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

internal sealed record SlotArenaValue(TypeRef Carrier) : StorageValue
{
    private struct Slot
    {
        internal Value? Value;
        internal int Next;
    }

    private Slot[] slots = [];
    private int end;
    private int free = -1;
    internal int Count { get; private set; }
    public override TypeRef ElementType => Carrier;

    public bool Equals(SlotArenaValue? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal int Allocate(Value value)
    {
        int index = free;
        if (index >= 0)
        {
            free = slots[index].Next;
        }
        else
        {
            index = end;
            if (index == slots.Length)
                Array.Resize(ref slots, index == 0 ? 4 : checked(index * 2));
            end++;
        }
        slots[index].Value = value;
        Count++;
        return index;
    }

    internal Value Get(double index) => slots[LiveSlot(index)].Value!;

    internal void Release(double index)
    {
        int slot = LiveSlot(index);
        slots[slot] = new Slot { Next = free };
        free = slot;
        Count--;
    }

    internal int Reset(Value first)
    {
        Array.Clear(slots, 0, end);
        end = Count = 0;
        free = -1;
        return Allocate(first);
    }

    private int LiveSlot(double index)
    {
        if (index >= 0 && index < end && index == Math.Truncate(index)
            && slots[(int)index].Value is not null)
            return (int)index;
        throw new MsyncException($"Index {index} does not identify a live allocation in this slot owner.");
    }

    public override IEnumerable<Value> RetainedReferences()
    {
        for (int index = 0; index < end; index++)
            if (slots[index].Value is { } value) yield return value;
    }
}

internal static class SlotArenaOperations
{
    internal static Value Invoke(string operation, IReadOnlyList<Value> arguments, RuntimeContext context)
    {
        if (operation == "new") return new SlotArenaValue(((TypeValue)arguments[0]).Data);
        if (arguments[0] is not SlotArenaValue owner)
            throw new MsyncException($"slots.{operation} requires a slot owner.");
        switch (operation)
        {
            case "allocate": return new NumberValue(owner.Allocate(Store(owner, arguments[1], context)));
            case "get": return owner.Get(((NumberValue)arguments[1]).Data);
            case "release":
                owner.Release(((NumberValue)arguments[1]).Data);
                return Value.Null;
            case "count": return new NumberValue(owner.Count);
            case "reset": return new NumberValue(owner.Reset(Store(owner, arguments[1], context)));
            default: throw new MsyncException($"Unknown slot operation '{operation}'.");
        }
    }

    private static Value Store(SlotArenaValue owner, Value value, RuntimeContext context) => value switch
    {
        StringValue when owner.ElementType.WithoutNull == TypeRef.Path => context.AsPath(value),
        PathValue path when owner.ElementType.WithoutNull == TypeRef.String => new StringValue(path.NativeText),
        _ => value
    };
}
