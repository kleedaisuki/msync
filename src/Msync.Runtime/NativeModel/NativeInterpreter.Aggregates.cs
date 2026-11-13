using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private void MakeFunction(NativeFrame frame, NativeInstruction instruction)
    {
        if (instruction.A < 0)
        {
            frame.Set(instruction.Result, NativeValue.FromFunction(new(-1, NativeName: instruction.Symbol)));
            return;
        }
        if (instruction.C == 0)
        {
            frame.Set(instruction.Result, NativeValue.FromFunction(new(instruction.A)));
            return;
        }
        NativeEnvironmentLayout layout = instruction.B < 0 ? environments[instruction.A] : EnvironmentLayout(instruction.B);
        NativeEnvironment environment = new(layout, instruction.B);
        IReadOnlyList<int> inputs = instruction.Inputs ?? [];
        for (int index = 0; index < inputs.Count; index++)
        {
            NativeCaptureLayout capture = layout.Captures[index];
            if (capture.Location)
            {
                NativeAddress address = frame.Pointer(inputs[index]);
                if (!address.Type.Equals(capture.Type))
                    throw new MsyncException($"Capture #{index} requires invariant storage {capture.Type}, received {address.Type}.", instruction.Source);
                environment.Initialize(index, new(address, capture.Type));
            }
            else memory.Write(environment.CaptureAddress(index), frame.GetDatum(inputs[index]));
        }
        frame.Set(instruction.Result, NativeValue.FromFunction(new(instruction.A, environment)));
    }

    private NativeEnvironmentLayout EnvironmentLayout(int index)
    {
        if (Volatile.Read(ref explicitEnvironments[index]) is { } existing) return existing;
        NativeEnvironmentLayout layout = new(image.Program.Layouts[index].Fields.Select(field =>
        {
            int pointee = image.Program.Types[field.Type].Pointee;
            return new NativeCaptureLayout(image.Type(pointee >= 0 ? pointee : field.Type), pointee >= 0);
        }));
        return Interlocked.CompareExchange(ref explicitEnvironments[index], layout, null) ?? layout;
    }

    private void AdaptFunction(NativeFrame frame, NativeInstruction instruction)
    {
        NativeFunction original = frame.GetDatum(instruction.A).Value.AsFunction();
        if (original.NativeName is not null)
        {
            frame.Set(instruction.Result, NativeValue.FromFunction(original));
            return;
        }
        int target = Target(original.Function, instruction.B, instruction.C, instruction.Symbol, instruction.Source);
        frame.Set(instruction.Result, NativeValue.FromFunction(new(target, RetargetEnvironment(original.Environment, target))));
    }

    private NativeEnvironment? RetargetEnvironment(NativeEnvironment? original, int target)
    {
        NativeEnvironmentLayout layout = environments[target];
        if (layout.Captures.Count == 0) return null;
        if (original is null || original.Count != layout.Captures.Count)
            throw new MsyncException($"Selected function #{target} '{image.Program.Functions[target].Name}' requires {layout.Captures.Count} captures, but the callable environment contains {original?.Count ?? 0}.");
        bool identical = true;
        for (int index = 0; index < original.Count; index++)
        {
            NativeCaptureLayout actual = original.Layout.Captures[index];
            NativeCaptureLayout expected = layout.Captures[index];
            identical &= actual.Type.Equals(expected.Type);
            if (actual.Location != expected.Location || (actual.Location
                ? !actual.Type.Equals(expected.Type) : (actual.Type.Kinds & ~expected.Type.Kinds) != 0))
                throw new MsyncException($"Selected function #{target} '{image.Program.Functions[target].Name}' capture #{index} requires {(expected.Location ? "location" : "value")} storage {expected.Type}, but the callable environment contains {(actual.Location ? "location" : "value")} storage {actual.Type}.");
        }
        if (identical) return original;
        NativeEnvironment adapted = new(layout);
        for (int index = 0; index < original.Count; index++)
        {
            if (layout.Captures[index].Location)
                adapted.Initialize(index, new(original[index].Location, layout.Captures[index].Type));
            else memory.Write(adapted.CaptureAddress(index), memory.Read(original.CaptureAddress(index)));
        }
        return adapted;
    }

    private int Target(int original, int direct, int session, string? dispatch, SourceSpan? source)
    {
        if (session >= 0)
        {
            IReadOnlyList<SessionDispatchTable>? tables = image.Program.SessionDispatches;
            if (tables is null || (uint)session >= tables.Count)
                throw new MsyncException($"Session call signature #{session} is unavailable.", source);
            return tables[session].Targets.TryGetValue(original, out int selected) ? selected
                : throw new MsyncException($"Function #{original} has no compiled specialization for session call signature #{session}.", source);
        }
        if (direct >= 0) return direct;
        if (dispatch is null) return original;
        return FunctionDispatch.Targets(dispatch).TryGetValue(original, out int target) ? target
            : throw new MsyncException($"Function #{original} has no compiled specialization.", source);
    }

    private async ValueTask<NativeStep> CallAsync(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        NativeValue callee = await frame.GetDatum(instruction.A).ResolveAsync(token);
        NativeDatum[] arguments = (instruction.Inputs ?? []).Select(frame.GetDatum).ToArray();
        NativeFunction function = callee.AsFunction();
        NativeDatum result;
        if (function.NativeName is { } nativeName)
            result = await InvokeNativeDatumAsync(nativeName, arguments, context.CaptureDirectory(), instruction.Source, token, RangeResultPhysical(frame, instruction.Result));
        else
        {
            int target = Target(function.Function, instruction.B, instruction.C, instruction.Symbol, instruction.Source);
            activation.Work.Counters.Call(pc, target);
            NativeEnvironment? environment = RetargetEnvironment(function.Environment, target);
            if (TailCallPlan(activation, pc) is { } tail)
            {
                activation.Tail = new(target, environment, arguments, pc, activation, instruction, tail);
                return new(pc + 1, true);
            }
            result = await RunFunctionAsync(target, environment, arguments, token);
        }
        frame.SetDatum(instruction.Result, result);
        return new(pc + 1);
    }

    private async ValueTask<NativeStep> CallNativeAsync(NativeFrame frame, NativeInstruction instruction, int pc, CancellationToken token, Msync.Core.NativeSignature? signature)
    {
        NativeDatum[] inputs = (instruction.Inputs ?? []).Select(frame.GetDatum).ToArray();
        NativeDatum result = await InvokeNativeDatumAsync(instruction.Symbol!, inputs, context.CaptureDirectory(), instruction.Source, token, RangeResultPhysical(frame, instruction.Result), signature);
        if (instruction.C >= 0)
        {
            TypeRef logicalElement = (await frame.GetDatum(instruction.C).ResolveAsync(token)).AsType();
            result = ApplyRangeMetadata(result, logicalElement, instruction.Source);
        }
        frame.SetDatum(instruction.Result, result);
        return new(pc + 1);
    }

    private NativeDatum ApplyRangeMetadata(NativeDatum input, TypeRef element, SourceSpan? source)
    {
        if (input.IsReady) return new(NativeValue.FromRange(RefineRange(input.Value.AsRange(), element)));
        return Track(input.Map(value => NativeValue.FromRange(RefineRange(value.AsRange(), element)), source,
            new(TypeKind.Range, element), NativeType.Of(NativeKind.Range), preserveIdentity: true));
    }

    private NativeType? RangeResultPhysical(NativeFrame frame, int slot)
    {
        int element = image.Program.Types[frame.Type(slot)].Element;
        return element >= 0 ? image.Type(element) : null;
    }

    private NativeRange RefineRange(NativeRange original, TypeRef itemType)
    {
        NativeRange refined = original.WithItemType(itemType);
        if (!ReferenceEquals(original, refined))
        {
            if (GetRangeRoots(original) is { } roots) rangeRoots.Add(refined, new(roots));
            if (datumRanges.TryGetValue(original, out DatumRange? items)) datumRanges.Add(refined, items);
        }
        return refined;
    }

    private static async ValueTask<NativeValue[]> ResolveArguments(IReadOnlyList<NativeDatum> arguments, CancellationToken token)
    {
        NativeValue[] values = new NativeValue[arguments.Count];
        for (int index = 0; index < values.Length; index++) values[index] = await arguments[index].ResolveAsync(token);
        return values;
    }

    private void MakeRange(NativeFrame frame, NativeInstruction instruction, bool lazy, TypeRef? logicalItemType = null)
    {
        NativeDatum[] values = (instruction.Inputs ?? []).Select(frame.GetDatum).ToArray();
        int element = image.Program.Types[instruction.Type].Element;
        TypeRef itemType = logicalItemType ?? (element >= 0 ? image.SourceType(element) : TypeRef.Any);
        NativeType physicalItemType = element >= 0 ? image.Type(element) : NativeType.FromTypeRef(TypeRef.Any);
        IAsyncEnumerable<NativeDatum> items = EnumerateDatums(values, lazy ? instruction.Symbol : "", CancellationToken.None);
        if (lazy) items = CaptureDatums(items);
        NativeRange range = !lazy && values.All(static value => value.IsReady)
            ? NativeRange.Snapshot(values.Select(static value => value.Value), itemType, physicalItemType)
            : new(ResolveDatumItems(items, CancellationToken.None), itemType,
                values.Where(static value => value.IsReady).Select(static value => value.Value).ToArray(), physicalItemType: physicalItemType);
        rangeRoots.Add(range, new(values));
        datumRanges.Add(range, new(items, lazy || values.Any(DatumMayDefer)));
        frame.Set(instruction.Result, NativeValue.FromRange(range));
    }

    private async IAsyncEnumerable<NativeDatum> EnumerateDatums(IReadOnlyList<NativeDatum> values, string? mask,
        [EnumeratorCancellation] CancellationToken token)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (mask is null || mask.Length > index && mask[index] == '1')
                yield return await InvokeDatumAsync(await values[index].ResolveAsync(token), [], token);
            else yield return values[index];
        }
    }

    private async IAsyncEnumerable<NativeValue> EnumerateItems(IReadOnlyList<NativeDatum> values, string? mask,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeDatum value in EnumerateDatums(values, mask, token))
            yield return await value.ResolveAsync(token);
    }

    private IAsyncEnumerator<NativeDatum> GetDatumIterator(NativeRange range, CancellationToken token) =>
        datumRanges.TryGetValue(range, out DatumRange? items) ? items.Items.GetAsyncEnumerator(token)
            : new DatumIterator(range.Items.GetAsyncEnumerator(token));

    private static async IAsyncEnumerable<NativeDatum> ReadyDatumItems(IAsyncEnumerable<NativeValue> items,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeValue value in items.WithCancellation(token)) yield return new(value);
    }

    private sealed class DatumIterator(IAsyncEnumerator<NativeValue> inner) : IAsyncEnumerator<NativeDatum>
    {
        public NativeDatum Current => new(inner.Current);
        public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private void MakeMemoryRange(NativeFrame frame, NativeInstruction instruction, int count, TypeRef? logicalItemType = null)
    {
        NativeAddress address = frame.Pointer(instruction.A);
        NativeLayout layout = image.Layout(instruction.C);
        image.ValidateRegion(address, layout, count);
        int ordinal = checked((int)instruction.Immediate);
        NativeFieldLayout field = layout[ordinal];
        int element = image.Program.Types[frame.Type(instruction.Result)].Element;
        TypeRef physicalDescription = element >= 0 ? image.SourceType(element) : TypeRef.Any;
        TypeRef itemType = logicalItemType ?? physicalDescription;
        NativeType physicalItemType = element >= 0 ? image.Type(element) : field.Type;
        if (instruction.Symbol == "move" && !memory.HasPending(address.Allocation))
        {
            NativeRange owned = NativeRange.FromOwnedMemory(address, count, layout, physicalDescription, ordinal).WithItemType(itemType);
            if ((itemType.Kind & TypeKind.Range) != 0)
                datumRanges.Add(owned, new(ReadyDatumItems(owned.Items, CancellationToken.None)));
            frame.Set(instruction.Result, NativeValue.FromRange(owned));
            return;
        }
        NativeDatum[] values = new NativeDatum[count];
        for (int index = 0; index < count; index++)
            values[index] = memory.Read(address.OffsetBy(layout.Extent * index + field.Offset, field.Type));
        NativeRange range = values.All(static value => value.IsReady)
            ? NativeRange.Snapshot(values.Select(static value => value.Value), itemType, physicalItemType)
            : new(EnumerateItems(values, "", CancellationToken.None), itemType,
                values.Where(static value => value.IsReady).Select(static value => value.Value).ToArray(), physicalItemType: physicalItemType);
        rangeRoots.Add(range, new(values));
        datumRanges.Add(range, new(EnumerateDatums(values, "", CancellationToken.None), values.Any(DatumMayDefer)));
        frame.Set(instruction.Result, NativeValue.FromRange(range));
    }

    private void MakeDeferredRange(NativeFrame frame, NativeInstruction instruction, TypeRef? logicalItemType = null)
    {
        NativeDatum function = frame.GetDatum(instruction.A);
        int element = image.Program.Types[instruction.Type].Element;
        TypeRef itemType = logicalItemType ?? (element >= 0 ? image.SourceType(element) : TypeRef.Any);
        NativeType physicalItemType = element >= 0 ? image.Type(element) : NativeType.FromTypeRef(TypeRef.Any);
        DeferredRange producer = new(this, function, itemType, context.CaptureDirectory());
        NativeRange range = new(producer, itemType,
            function.IsReady ? [function.Value] : [], resolve: producer.ResolveAsync, physicalItemType: physicalItemType);
        rangeRoots.Add(range, new([function]));
        datumRanges.Add(range, new(producer.Datums(CancellationToken.None)));
        frame.Set(instruction.Result, NativeValue.FromRange(range));
    }

    private sealed class DeferredRange(NativeInterpreter interpreter, NativeDatum function, TypeRef itemType, NativeRuntimeContext origin) : IAsyncEnumerable<NativeValue>
    {
        private Task<NativeRange>? resolved;
        internal ValueTask<NativeRange> ResolveAsync(CancellationToken token) => new(resolved ??= ResolveCoreAsync(token));
        private async Task<NativeRange> ResolveCoreAsync(CancellationToken token)
        {
            using var directory = interpreter.EnterDeferredDirectory(origin.ForkDirectory());
            NativeValue callable = await function.ResolveAsync(token);
            NativeRange range = (await interpreter.InvokeAsync(callable, [], token)).AsRange();
            return interpreter.RefineRange(await NativeBuiltins.DemandRangeAsync(range, token), itemType);
        }
        internal async IAsyncEnumerable<NativeDatum> Datums([EnumeratorCancellation] CancellationToken token)
        {
            NativeRange range = await ResolveAsync(token);
            await using IAsyncEnumerator<NativeDatum> iterator = interpreter.GetDatumIterator(range, token);
            while (await iterator.MoveNextAsync()) yield return iterator.Current;
        }
        public async IAsyncEnumerator<NativeValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            NativeRange range = await ResolveAsync(cancellationToken);
            await foreach (NativeValue value in range.Items.WithCancellation(cancellationToken)) yield return value;
        }
    }

    private async ValueTask<NativeStep> OpenIteratorAsync(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeRange range = (await activation.Frame.GetDatum(instruction.A).ResolveAsync(token)).AsRange();
        IAsyncEnumerator<NativeDatum>? previous = activation.Iterators[instruction.B];
        activation.Iterators[instruction.B] = null;
        if (previous is not null) await previous.DisposeAsync();
        activation.Iterators[instruction.B] = GetDatumIterator(range, token);
        return new(pc + 1);
    }

    private static async ValueTask<NativeStep> NextIteratorAsync(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IAsyncEnumerator<NativeDatum> iterator = activation.Iterators[instruction.A]
            ?? throw new InvalidOperationException("Iterator is not open.");
        if (!await iterator.MoveNextAsync()) return new(instruction.B);
        activation.Frame.SetDatum(instruction.Result, iterator.Current);
        return new(pc + 1);
    }

    private static async ValueTask<NativeStep> CloseIteratorAsync(Activation activation, int index, int pc)
    {
        IAsyncEnumerator<NativeDatum>? iterator = activation.Iterators[index];
        activation.Iterators[index] = null;
        if (iterator is not null) await iterator.DisposeAsync();
        return new(pc + 1);
    }
}
