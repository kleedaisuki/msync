using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

internal sealed record DenseVectorValue : StorageValue
{
    private readonly TypeRef elementType;
    private List<double>? numbers;
    private List<Value>? references;

    internal DenseVectorValue(TypeRef elementType)
    {
        this.elementType = elementType;
        if (elementType == TypeRef.Number) numbers = [];
        else references = [];
    }

    public override TypeRef ElementType => elementType;
    internal int Count => numbers?.Count ?? references!.Count;
    internal List<double>? NumberValues => numbers;
    public override IEnumerable<Value> RetainedReferences() => references ?? [];
    public bool Equals(DenseVectorValue? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal bool TryBorrowNumbers(out Span<double> items)
    {
        items = CollectionsMarshal.AsSpan(numbers);
        return numbers is not null;
    }

    internal static async ValueTask<DenseVectorValue> CreateAsync(RangeValue source, CancellationToken cancellationToken)
    {
        DenseVectorValue owner = new(source.ItemType);
        if (owner.numbers is { } numeric && source.Items is NumberRangeItems items)
        {
            var cursor = items.Open(cancellationToken);
            while (cursor.TryNext(out double value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                numeric.Add(value);
            }
            return owner;
        }
        await foreach (var value in source.Items.WithCancellation(cancellationToken))
            owner.AddInitial(value);
        return owner;
    }

    internal Value? GetOperand(double index, out double number)
    {
        int slot = Offset(index, "get");
        number = numbers is { } numeric ? numeric[slot] : 0;
        return references?[slot];
    }

    internal void Set(double index, Value value, RuntimeContext context)
    {
        int slot = Offset(index, "set");
        if (numbers is { } numeric)
            numeric[slot] = Scalar(value);
        else
            references![slot] = Convert(value, context);
    }

    internal void SetNumber(double index, double value) => numbers![Offset(index, "set")] = value;

    internal int Push(Value value, RuntimeContext context)
    {
        if (numbers is not null) return PushNumber(Scalar(value));
        var items = references!;
        int index = items.Count;
        items.Add(Convert(value, context));
        return index;
    }

    internal int PushNumber(double value)
    {
        var numeric = numbers!;
        int index = numeric.Count;
        numeric.Add(value);
        return index;
    }

    internal Value? PopOperand(out double number)
    {
        int index = Count - 1;
        if (index < 0) throw new MsyncException("storage.vector_pop requires nonempty storage.");
        return RemoveSlot(index, out number);
    }

    internal void Insert(double index, Value value, RuntimeContext context)
    {
        int slot = Offset(index, "insert", allowEnd: true);
        if (numbers is { } numeric)
            numeric.Insert(slot, Scalar(value));
        else
            references!.Insert(slot, Convert(value, context));
    }

    internal void InsertNumber(double index, double value) =>
        numbers!.Insert(Offset(index, "insert", allowEnd: true), value);

    internal Value? RemoveOperand(double index, out double number) => RemoveSlot(Offset(index, "remove"), out number);

    private Value? RemoveSlot(int index, out double number)
    {
        if (numbers is { } numeric)
        {
            number = numeric[index];
            numeric.RemoveAt(index);
            return null;
        }
        number = 0;
        var value = references![index];
        references.RemoveAt(index);
        return value;
    }

    internal void Clear()
    {
        numbers?.Clear();
        references?.Clear();
    }

    internal RangeValue Snapshot() => numbers is { } numeric
        ? RangeValue.OwnNumberSnapshot(new(numeric), elementType)
        : RangeValue.TakeSnapshotOwnership(new(references!), elementType);

    internal RangeValue Take()
    {
        if (numbers is { } numeric)
        {
            numbers = [];
            return RangeValue.OwnNumberSnapshot(numeric, elementType);
        }
        var owned = references!;
        references = [];
        return RangeValue.TakeSnapshotOwnership(owned, elementType);
    }

    private void AddInitial(Value value)
    {
        if (numbers is not { } numeric)
        {
            references!.Add(value);
            return;
        }
        if (value is NumberValue scalar)
        {
            numeric.Add(scalar.Data);
            return;
        }
        // A Range can retain an unresolved Number from a live Process.
        // Construction preserves that deferred value without joining it early.
        if (value.Type != TypeRef.Number) throw NumberRequired(value);
        List<Value> items = new(numeric.Count + 1);
        foreach (double number in numeric) items.Add(new NumberValue(number));
        items.Add(value);
        numbers = null;
        references = items;
    }

    private static double Scalar(Value value) => value is NumberValue number ? number.Data : throw NumberRequired(value);

    private static MsyncException NumberRequired(Value value) =>
        new($"Number storage requires Number elements, not {value.Type}.");

    private Value Convert(Value value, RuntimeContext context) => value switch
    {
        StringValue when elementType.WithoutNull == TypeRef.Path => context.AsPath(value),
        PathValue path when elementType.WithoutNull == TypeRef.String => new StringValue(path.NativeText),
        _ => value
    };

    private int Offset(double index, string operation, bool allowEnd = false)
    {
        if (index >= 0 && index <= (allowEnd ? Count : Count - 1) && index == Math.Truncate(index))
            return (int)index;
        throw new MsyncException($"storage.vector_{operation} index {index} is outside the items of storage with {Count} items.");
    }
}

internal static class VectorStorageOperations
{
    internal static async ValueTask<Value> InvokeAsync(string operation, IReadOnlyList<Value> args,
        RuntimeContext context, CancellationToken cancellationToken)
    {
        if (operation == "vector_new") return await DenseVectorValue.CreateAsync((RangeValue)args[0], cancellationToken);
        if (operation == "vector_empty") return new DenseVectorValue(((TypeValue)args[0]).Data);
        if (args[0] is not DenseVectorValue owner)
            throw new MsyncException($"storage.{operation} requires dense vector storage.");
        switch (operation)
        {
            case "vector_empty_like": return new DenseVectorValue(owner.ElementType);
            case "vector_get": return owner.GetOperand(((NumberValue)args[1]).Data, out double read) ?? new NumberValue(read);
            case "vector_set":
                owner.Set(((NumberValue)args[1]).Data, args[2], context);
                return Value.Null;
            case "vector_push": return new NumberValue(owner.Push(args[1], context));
            case "vector_pop": return owner.PopOperand(out double popped) ?? new NumberValue(popped);
            case "vector_insert":
                owner.Insert(((NumberValue)args[1]).Data, args[2], context);
                return Value.Null;
            case "vector_remove":
                return owner.RemoveOperand(((NumberValue)args[1]).Data, out double removed) ?? new NumberValue(removed);
            case "vector_size": return new NumberValue(owner.Count);
            case "vector_clear":
                owner.Clear();
                return Value.Null;
            case "vector_snapshot": return owner.Snapshot();
            case "vector_take": return owner.Take();
            default: throw new MsyncException($"Unknown vector operation '{operation}'.");
        }
    }
}
