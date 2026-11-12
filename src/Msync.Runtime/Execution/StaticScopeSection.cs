using Msync.Core;

namespace Msync.Runtime;

internal sealed class StaticScopeSection
{
    private Slot[] slots = [];

    private struct Slot
    {
        internal bool Ready;
        internal ScopePayload Payload;
        internal ScopeValue? Identity;
    }

    internal void Reserve(int count)
    {
        if (count > slots.Length)
            Array.Resize(ref slots, count);
    }

    internal bool Ready(int slot) => slots[slot].Ready;

    internal void RequireReady(IReadOnlyList<int> required)
    {
        foreach (int slot in required)
            if (!Ready(slot)) throw new MsyncException("The selected callable requires an uninitialized named Scope.");
    }

    internal bool Publish<TFrame>(int slot, ScopeLayout layout, ScopeAllocator allocator, ref OperandStack<TFrame> stack)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        ref var destination = ref slots[slot];
        if (destination.Ready)
        {
            stack.Discard(layout.Count);
            return false;
        }
        destination.Payload = stack.PopScopePayload(allocator, layout, layout.Count, ~slot);
        destination.Ready = true;
        return true;
    }

    internal ScopeValue Identity(int slot, ScopeLayout layout, out bool materialized)
    {
        ref var owner = ref slots[slot];
        materialized = owner.Identity is null;
        if (owner.Identity is { } identity) return identity;
        owner.Identity = identity = new(layout, owner.Payload, 0);
        owner.Payload = default;
        return identity;
    }

    internal Value? Read(int slot, int field, out double number)
    {
        ref var owner = ref slots[slot];
        return owner.Identity is { } identity
            ? identity.GetOperandField(field, out number)
            : owner.Payload.GetOperandField(field, out number);
    }

    internal void Write(int slot, int field, double number)
    {
        ref var owner = ref slots[slot];
        if (owner.Identity is { } identity) identity.SetNumber(field, number);
        else owner.Payload.SetNumber(field, number);
    }

    internal void Write(int slot, int field, Value value)
    {
        ref var owner = ref slots[slot];
        if (owner.Identity is { } identity) identity.SetField(field, value);
        else owner.Payload.SetField(field, value);
    }

    internal void VisitRoots(Action<Value> visit)
    {
        foreach (ref readonly var owner in slots.AsSpan())
        {
            if (!owner.Ready) continue;
            if (owner.Identity is { } identity) visit(identity);
            else
                foreach (var field in owner.Payload.RetainedReferences)
                    if (field is not null) visit(field);
        }
    }
}
