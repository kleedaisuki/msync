using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private async ValueTask<NativeDatum?> TryRangeDatumAsync(Msync.Core.NativeSignature signature,
        IReadOnlyList<NativeDatum> args, CancellationToken token, NativeType? resultPhysical = null)
    {
        if (!signature.Name.StartsWith("ranges.", StringComparison.Ordinal)) return null;
        string operation = signature.Name[7..];
        if (operation is "empty_of" or "sequence" or "less") return null;
        bool callback = operation is "transform" or "filter" or "take_while" or "drop_while" or "scan"
            or "find_if" or "any" or "any_of" or "all_of" or "none" or "none_of" or "sort_with"
            or "merge_with" or "set_union_with" or "set_intersection_with" or "set_difference_with" or "set_symmetric_difference_with";
        bool pending = args.Any(DatumMayDefer) || args.Any(value => value.IsReady && value.Value.Kind == NativeKind.Range
            && HasLogicalMetadata(value.Value.AsRange().ItemType));
        if (!pending && !callback) return null;
        if (signature.Variadic ? args.Count < signature.Parameters.Count : args.Count != signature.Parameters.Count)
            throw new MsyncException($"Native '{signature.Name}' expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments, received {args.Count}");
        (currentWork.Value ?? executionWork).Counters.Native(signature.Name);
        if (operation is "of" or "single") return new NativeDatum(NativeValue.FromRange(DatumSnapshot(args, args.Aggregate(new TypeRef(TypeKind.Never), static (type, value) => TypeRef.Union(type, value.LogicalType)),
                resultPhysical ?? NativeType.Union(args.Select(static value => value.PhysicalType).ToArray()))));
        if (operation == "repeat")
        {
            long count = RangeOrdinal(await args[1].ResolveAsync(token), operation, false);
            return new NativeDatum(NativeValue.FromRange(DatumRangeValue(RepeatDatum(args[0], count, token), args[0].LogicalType, args, resultPhysical ?? args[0].PhysicalType)));
        }
        NativeRange source = (await args[0].ResolveAsync(token)).AsRange();
        if (operation is "identity" or "all") return args[0];
        if (operation == "materialize")
            return new NativeDatum(NativeValue.FromRange(DatumSnapshot(await CollectDatums(source, token), source.ItemType, source.PhysicalItemType)));
        if (operation is "front" or "first" or "back" or "last" or "at" or "size" or "empty")
            return await SelectDatum(source, operation, args, token);
        if (operation is "contains" or "find" or "find_if" or "any" or "any_of" or "all_of" or "none" or "none_of")
            return await MatchDatum(source, operation, args[1], token);
        if (operation is "take" or "drop" or "stride" or "step")
        {
            long count = RangeOrdinal(await args[1].ResolveAsync(token), operation, operation is "stride" or "step");
            if (operation == "take" && count == 0)
                return new NativeDatum(NativeValue.FromRange(DatumSnapshot([], source.ItemType, source.PhysicalItemType)));
            var selected = AdaptDatumSelection(source, operation, count,
                static (owner, input, op, limit, cancellation) => owner.CountedDatums(input, op, limit, cancellation));
            return new NativeDatum(NativeValue.FromRange(DatumRangeValue(CountedDatums(source, operation, count, token),
                source.ItemType, args, source.PhysicalItemType, selected)));
        }
        IAsyncEnumerable<NativeDatum> items;
        TypeRef itemType = source.ItemType;
        NativeType physicalType = source.PhysicalItemType;
        NativePathSelection? selection = null;
        switch (operation)
        {
            case "transform":
            case "filter":
            case "take_while":
            case "drop_while":
                NativeValue callbackValue = await args[1].ResolveAsync(token);
                items = CallbackDatums(source, operation, callbackValue, token);
                selection = AdaptDatumSelection(source, operation, callbackValue,
                    static (owner, input, op, callable, cancellation) => owner.CallbackDatums(input, op, callable, cancellation));
                if (operation == "transform")
                {
                    itemType = DescribeFunction(callbackValue.AsFunction())?.Result ?? TypeRef.Any;
                    physicalType = resultPhysical ?? CallbackPhysical(callbackValue);
                }
                break;
            case "reverse": items = ReversedDatums(source, token); break;
            case "concat":
                NativeRange right = (await args[1].ResolveAsync(token)).AsRange();
                items = ConcatDatums(source, right, token);
                itemType = TypeRef.Union(itemType, right.ItemType);
                physicalType = NativeType.Union(physicalType, right.PhysicalItemType);
                break;
            case "flatten":
                items = FlattenDatums(source, token); itemType = itemType.Element ?? TypeRef.Any;
                physicalType = resultPhysical ?? NativeType.FromTypeRef(TypeRef.Any);
                break;
            case "zip": items = ZipDatums(source, (await args[1].ResolveAsync(token)).AsRange(), token); itemType = TypeRef.Scope; physicalType = NativeType.Of(NativeKind.Address); break;
            case "enumerate_from": items = IndexedDatums(source, RangeFinite(await args[1].ResolveAsync(token), operation), token); itemType = TypeRef.Scope; physicalType = NativeType.Of(NativeKind.Address); break;
            case "chunk":
            case "slide":
                items = PartitionDatums(source, RangeOrdinal(await args[1].ResolveAsync(token), operation, true), operation == "slide", token);
                itemType = new(TypeKind.Range, itemType);
                physicalType = NativeType.Of(NativeKind.Range);
                break;
            case "scan":
                NativeValue scanCallback = await args[2].ResolveAsync(token);
                items = ScanDatums(source, args[1], scanCallback, token);
                itemType = DescribeFunction(scanCallback.AsFunction())?.Result ?? TypeRef.Any;
                physicalType = resultPhysical ?? CallbackPhysical(scanCallback);
                break;
            case "sort":
            case "sort_with":
                items = SortDatums(source, operation == "sort_with" ? await args[1].ResolveAsync(token) : (NativeValue?)null, token);
                break;
            case "unique": items = UniqueDatums(source, token); break;
            case "merge":
            case "merge_with":
            case "set_union":
            case "set_union_with":
            case "set_intersection":
            case "set_intersection_with":
            case "set_difference":
            case "set_difference_with":
            case "set_symmetric_difference":
            case "set_symmetric_difference_with":
                NativeRange other = (await args[1].ResolveAsync(token)).AsRange();
                items = CombineDatums(source, other, operation, args.Count == 3 ? await args[2].ResolveAsync(token) : (NativeValue?)null, token);
                if (!operation.StartsWith("set_intersection", StringComparison.Ordinal) && !operation.StartsWith("set_difference", StringComparison.Ordinal))
                {
                    itemType = TypeRef.Union(itemType, other.ItemType);
                    physicalType = NativeType.Union(physicalType, other.PhysicalItemType);
                }
                break;
            default: return new(await InvokeNativeAsync(signature.Name, await ResolveArguments(args, token), context, token));
        }
        return new NativeDatum(NativeValue.FromRange(DatumRangeValue(items, itemType, args, physicalType, selection)));
    }

    private static bool HasLogicalMetadata(TypeRef type) => (type.Kind & ~TypeKind.Any) != 0
        || type.Element is { } element && HasLogicalMetadata(element);

    private NativeType CallbackPhysical(NativeValue function)
    {
        NativeFunction callable = function.AsFunction();
        return callable.NativeName is { } name ? NativeType.FromTypeRef(NativeSignatures.Get(name).Result)
            : image.Type(image.Program.Functions[callable.Function].Signature.Result);
    }

    private bool DatumMayDefer(NativeDatum value) => !value.IsReady || value.Value.Kind == NativeKind.Range
        && datumRanges.TryGetValue(value.Value.AsRange(), out DatumRange? range) && range.MayDefer;

    private NativeRange ReadySnapshot(IReadOnlyList<NativeDatum> values, TypeRef itemType, NativeType physicalItemType)
    {
        NativeLayout element = new(physicalItemType);
        NativeAllocation storage = guestHeap.NewObject(element, -1, values.Count, 0, currentWork.Value?.Counters ?? default);
        NativeFieldLayout field = element[0];
        for (int index = 0; index < values.Count; index++)
        {
            NativeValue value = values[index].Value;
            if (!physicalItemType.Accepts(value))
                throw new ArgumentException($"{value.Type} is not admitted by the Range's item ABI {physicalItemType}.", nameof(values));
            NativeStorage.StoreTrusted(physicalItemType, storage.Address.OffsetBy(element.Extent * index + field.Offset, physicalItemType), value);
        }
        return NativeRange.FromGuestSnapshot(storage.Address, values.Count, element, itemType);
    }

    private NativeRange DatumSnapshot(IReadOnlyList<NativeDatum> values, TypeRef itemType, NativeType physicalItemType)
    {
        if (values.All(static value => value.IsReady))
        {
            NativeRange ready = ReadySnapshot(values, itemType, physicalItemType);
            if (values.Any(DatumMayDefer)) datumRanges.Add(ready, new(ReadyDatumItems(ready.Items, CancellationToken.None)));
            return ready;
        }
        NativeAllocation storage = guestHeap.NewObject(NativeLayout.Repeat(physicalItemType, values.Count),
            -1, 1, 0, currentWork.Value?.Counters ?? default);
        for (int index = 0; index < values.Count; index++) memory.Write(storage.Field(index), values[index]);
        IAsyncEnumerable<NativeDatum> items = SnapshotDatums(storage, CancellationToken.None);
        NativeValue owner = NativeValue.FromAddress(storage.Address);
        NativeRange range = new(ResolveDatumItems(items, CancellationToken.None), itemType, [owner], physicalItemType: physicalItemType);
        rangeRoots.Add(range, new([new(owner)]));
        datumRanges.Add(range, new(items));
        return range;
    }

    private async IAsyncEnumerable<NativeDatum> SnapshotDatums(NativeAllocation storage, [EnumeratorCancellation] CancellationToken token)
    {
        for (int index = 0; index < storage.Layout.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            yield return memory.Read(storage.Field(index));
        }
        await Task.CompletedTask;
    }

    private NativePathSelection? AdaptDatumSelection<T>(NativeRange source, string operation, T operand,
        Func<NativeInterpreter, NativeRange, string, T, CancellationToken, IAsyncEnumerable<NativeDatum>> adapt)
    {
        if (source.Selection is not { } selection) return null;
        using var observation = context.Timeline?.Begin("range.selection.adapt");
        observation?.SetAttribute("range.operation", operation);
        observation?.SetAttribute("range.endpoint", selection.Directory.Connection?.ToString() ?? "local");
        NativeRuntimeContext origin = context.CaptureDirectory();
        return NativeBuiltins.AdaptSelection(source, (input, token) =>
            ResolveDatumItems(new ContextDatums(this, adapt(this, input, operation, operand, token), origin), token));
    }

    private NativeRange DatumRangeValue(IAsyncEnumerable<NativeDatum> items, TypeRef itemType, IReadOnlyList<NativeDatum> roots,
        NativeType physicalItemType, NativePathSelection? selection = null)
    {
        items = CaptureDatums(items);
        NativeRange range = new(ResolveDatumItems(items, CancellationToken.None), itemType,
            roots.Where(static value => value.IsReady).Select(static value => value.Value).ToArray(), selection, physicalItemType: physicalItemType);
        rangeRoots.Add(range, new(roots.ToArray()));
        datumRanges.Add(range, new(items, roots.Any(DatumExecutionMayDefer)));
        return range;
    }

    private static async IAsyncEnumerable<NativeValue> ResolveDatumItems(IAsyncEnumerable<NativeDatum> items,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeDatum value in items.WithCancellation(token)) yield return await value.ResolveAsync(token);
    }

    private async IAsyncEnumerable<NativeDatum> DatumItems(NativeRange range, [EnumeratorCancellation] CancellationToken token)
    {
        await using IAsyncEnumerator<NativeDatum> iterator = GetDatumIterator(range, token);
        while (await iterator.MoveNextAsync()) yield return iterator.Current;
    }

    private async ValueTask<List<NativeDatum>> CollectDatums(NativeRange range, CancellationToken token)
    {
        List<NativeDatum> result = [];
        await foreach (NativeDatum value in DatumItems(range, token)) result.Add(value);
        return result;
    }

    private static double RangeFinite(NativeValue value, string operation)
    {
        double number = NativeBuiltins.Number(value);
        return double.IsFinite(number) ? number : throw new MsyncException($"ranges.{operation} requires finite Number arguments");
    }

    private static long RangeOrdinal(NativeValue value, string operation, bool positive)
    {
        double number = RangeFinite(value, operation);
        if (number < (positive ? 1 : 0) || number != Math.Truncate(number) || number >= 9223372036854775808d)
            throw new MsyncException($"ranges.{operation} requires a {(positive ? "positive" : "nonnegative")} integral count representable by the platform iterator");
        return (long)number;
    }

    private async ValueTask<NativeDatum> SelectDatum(NativeRange source, string operation, IReadOnlyList<NativeDatum> args, CancellationToken token)
    {
        long selected = operation == "at" ? RangeOrdinal(await args[1].ResolveAsync(token), operation, false) : 0;
        long count = 0;
        NativeDatum? last = null;
        await foreach (NativeDatum value in DatumItems(source, token))
        {
            if (operation == "empty") return new(NativeValue.False);
            if (operation is "front" or "first" || operation == "at" && count == selected) return value;
            last = value;
            count++;
        }
        if (operation == "size") return new(NativeValue.FromNumber(count));
        if (operation == "empty") return new(NativeValue.True);
        if (operation is "back" or "last" && last is { } result) return result;
        throw new MsyncException(operation == "at" ? $"ranges.at has no item at index {selected}" : $"ranges.{operation} requires a nonempty Range");
    }

    private async ValueTask<NativeDatum> MatchDatum(NativeRange source, string operation, NativeDatum condition, CancellationToken token)
    {
        NativeValue target = await condition.ResolveAsync(token);
        await foreach (NativeDatum value in DatumItems(source, token))
        {
            bool matches = operation is "contains" or "find" ? NativeBuiltins.RangeEquals(await value.ResolveAsync(token), target)
                : (await (await InvokeDatumAsync(target, [value], token)).ResolveAsync(token)).AsBool();
            if (operation == "contains" && matches) return new(NativeValue.True);
            if (operation is "find" or "find_if" && matches) return value;
            if (operation is "any" or "any_of" && matches) return new(NativeValue.True);
            if (operation == "all_of" && !matches) return new(NativeValue.False);
            if (operation is "none" or "none_of" && matches) return new(NativeValue.False);
        }
        return operation switch
        {
            "all_of" or "none" or "none_of" => new(NativeValue.True),
            "contains" or "any" or "any_of" => new(NativeValue.False),
            _ => throw new MsyncException($"ranges.{operation} found no matching item")
        };
    }
}
