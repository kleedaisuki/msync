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
        NativeEnvironment environment = AllocateEnvironment(layout, instruction.B);
        IReadOnlyList<int> inputs = instruction.Inputs ?? [];
        for (int index = 0; index < inputs.Count; index++)
        {
            NativeCaptureLayout capture = layout.Captures[index];
            if (capture.Location)
            {
                NativeAddress address = frame.Pointer(inputs[index]);
                memory.WriteAddress(environment.CaptureAddress(index), address);
            }
            else frame.CopyTo(inputs[index], environment.CaptureAddress(index));
        }
        PublishMachineCaptures(environment);
        frame.Set(instruction.Result, NativeValue.FromFunction(new(instruction.A, environment)));
    }

    private NativeEnvironment AllocateEnvironment(NativeEnvironmentLayout layout, int id)
    {
        RuntimeProfile.NativeFrameCounters counters = currentWork.Value?.Counters ?? default;
        NativeAllocation allocation = guestHeap.NewObject(layout.Storage, id, 1, 0, counters);
        if (allocation.GuestReferences is null) return NativeEnvironment.View(layout, allocation.Address);
        NativeEnvironment environment = allocation.OpenGuestEnvironment(layout, out bool reused);
        counters.NativeGuestEnvironment(reused);
        return environment;
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
        if (ReferenceEquals(original.Layout, layout)) return original;
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
        NativeEnvironment adapted = AllocateEnvironment(layout, -1);
        for (int index = 0; index < original.Count; index++)
        {
            if (layout.Captures[index].Location)
                adapted.Initialize(index, new(original[index].Location, layout.Captures[index].Type));
            else memory.Write(adapted.CaptureAddress(index), memory.Read(original.CaptureAddress(index)));
        }
        PublishMachineCaptures(adapted);
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

    private NativeFunction ReadCall(Activation activation, NativeInstruction instruction, int pc, CancellationToken token,
        out NativeArguments arguments) =>
        ReadCall(activation, activation.Frame.Location(instruction.A), pc, token, out arguments);

    private NativeFunction ReadCall(Activation activation, NativeAddress callee, int pc, CancellationToken token,
        out NativeArguments arguments)
    {
        NativeFrame frame = activation.Frame;
        bool calleePending = memory.TryPending(callee, out NativeFuture? pendingCallee);
        arguments = activation.Execution.Arguments(frame, pc, calleePending);
        return calleePending
            ? Demand(pendingCallee!.CompleteAsync(token)).AsFunction() : NativeStorage.LoadFunctionDirect(callee);
    }

    private bool CallFixedContinuation(Activation activation, NativeInstruction instruction, NativeAddress callee, int pc,
        CancellationToken token, NativeFunction? ready, TierInvocation? nativeCaller)
    {
        NativeArguments arguments;
        NativeFunction function;
        if (ready is { } loaded)
        {
            function = loaded;
            arguments = activation.Execution.Arguments(activation.Frame, pc, calleePending: false);
        }
        else function = ReadCall(activation, callee, pc, token, out arguments);
        if (function.NativeName is { } nativeName)
        {
            CallHost(activation.Frame, instruction, pc, token, nativeName, arguments);
            return false;
        }
        int target = instruction.B;
        NativeExecutionFunction executable = ExecutionFunction(image, target);
        if (executable.IsPureSynchronous)
            RunDirectFunction(activation, executable, function.Environment, arguments,
                activation.Frame.Location(instruction.Result), pc, token);
        else
        {
            activation.Work.Counters.Call(pc, target);
            if (nativeCaller is not null && TryPrepareFixedTier(nativeCaller, executable, function.Environment,
                arguments, pc, ready.HasValue, in callee)) return true;
            activation.Frame.SetDatum(instruction.Result, RunFixedGuest(activation, executable, function.Environment, arguments, token));
        }
        return false;
    }

    private NativeStep CallDirect(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeFunction function = ReadCall(activation, instruction, pc, token, out NativeArguments arguments);
        if (function.NativeName is { } nativeName)
            return CallHost(activation.Frame, instruction, pc, token, nativeName, arguments);
        int target = Target(function.Function, instruction.B, instruction.C, instruction.Symbol, instruction.Source);
        RunDirectFunction(activation, ExecutionFunction(image, target), function.Environment, arguments,
            activation.Frame.Location(instruction.Result), pc, token);
        return new NativeStep(pc + 1);
    }

    private NativeStep Call(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeFunction function = ReadCall(activation, instruction, pc, token, out NativeArguments arguments);
        NativeFrame frame = activation.Frame;
        if (function.NativeName is { } nativeName)
            return CallHost(frame, instruction, pc, token, nativeName, arguments);
        int target = Target(function.Function, instruction.B, instruction.C, instruction.Symbol, instruction.Source);
        if (instruction.B < 0 || instruction.C >= 0) ValidateInvocation(target, arguments.Count);
        NativeEnvironment? environment = function.Environment;
        if (TailCallPlan(activation, pc) is { } tail)
        {
            activation.Work.Counters.Call(pc, target);
            if (!activation.ReturnDestination.IsNull)
                RunDirectTail(activation, target, environment, arguments, pc, tail, token);
            else activation.Tail = new(target, environment, arguments, pc, activation.Function, instruction, tail);
            return new NativeStep(pc + 1, true);
        }

        NativeExecutionFunction executable = ExecutionFunction(image, target);
        if (executable.IsContextFree || executable.IsPureSynchronous && RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            RunDirectFunction(activation, executable, environment, arguments, frame.Location(instruction.Result), pc, token);
            return new NativeStep(pc + 1);
        }
        activation.Work.Counters.Call(pc, target);
        if (profile is not null && executable.IsPureSynchronous) activation.Work.Counters.StackLimitedCall();
        if (!activation.ReturnDestination.IsNull)
        {
            if (profile is not null) activation.Work.Counters.GuestLoopHandoff();
            frame.SetDatum(instruction.Result, RunFunction(target, environment, arguments, token));
            return new NativeStep(pc + 1);
        }
        activation.Call = new(target, environment, arguments, instruction.Result, pc);
        return new NativeStep(pc + 1, true);
    }

    private NativeStep CallHost(NativeFrame frame, NativeInstruction instruction, int pc, CancellationToken token,
        string nativeName, NativeArguments arguments)
    {
        ValueTask<NativeDatum> callTask = InvokeNativeDatumAsync(nativeName, arguments.Materialize(memory),
            context.CaptureDirectory(), instruction.Source, token, RangeResultPhysical(frame, instruction.Result));
        frame.SetDatum(instruction.Result, Demand(callTask));
        return new NativeStep(pc + 1);
    }

    private NativeStep CallNative(Activation activation, NativeInstruction instruction, int pc, CancellationToken token, Msync.Core.NativeSignature? signature)
    {
        NativeFrame frame = activation.Frame;
        if (TryFastUnaryMath(activation.Work, frame, in instruction)) return new NativeStep(pc + 1);
        if (instruction.C < 0 && instruction.Symbol == "path.join" && instruction.Inputs is { Count: 2 } path
            && TryNativeTextBinary(frame, NativeBinary.PathJoin, path[0], path[1], instruction.Result, activation.Work.Counters))
        {
            activation.Work.Counters.Native("path.join");
            return new NativeStep(pc + 1);
        }
        NativeDatum[] inputs;
        if (instruction.Inputs is { Count: > 0 } slotInputs)
        {
            inputs = new NativeDatum[slotInputs.Count];
            for (int i = 0; i < inputs.Length; i++) inputs[i] = frame.GetDatum(slotInputs[i]);
        }
        else inputs = [];
        ValueTask<NativeDatum> pending = InvokeNativeDatumAsync(instruction.Symbol!, inputs, context.CaptureDirectory(), instruction.Source, token, RangeResultPhysical(frame, instruction.Result), signature);
        if (!pending.IsCompleted || instruction.C >= 0) return AwaitCallNativeAsync(frame, instruction, pc, token, pending);
        frame.SetDatum(instruction.Result, pending.GetAwaiter().GetResult());
        return new NativeStep(pc + 1);
    }

    private async ValueTask<NativeStep> AwaitCallNativeAsync(NativeFrame frame, NativeInstruction instruction, int pc, CancellationToken token, ValueTask<NativeDatum> pending)
    {
        NativeDatum result = await pending;
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

    private void MakeRange(NativeFrame frame, NativeInstruction instruction, TypeRef? logicalItemType = null)
    {
        using RuntimeTimelineScope? observation = context.Timeline?.Begin("range.initialize");
        observation?.SetSource(instruction.Source);
        NativeDatum[] values = (instruction.Inputs ?? []).Select(frame.GetDatum).ToArray();
        observation?.SetMetric("elements", values.Length);
        if (observation is not null) observation.SetMetric("elements.ready", values.Count(static value => value.IsReady));
        int element = image.Program.Types[instruction.Type].Element;
        TypeRef itemType = logicalItemType ?? (element >= 0 ? image.SourceType(element) : TypeRef.Any);
        NativeType physicalItemType = element >= 0 ? image.Type(element) : NativeType.FromTypeRef(TypeRef.Any);
        IAsyncEnumerable<NativeDatum> items = new DatumSequence(values);
        NativeRange range = values.All(static value => value.IsReady)
            ? ReadySnapshot(values, itemType, physicalItemType)
            : new(ResolveDatumItems(items, CancellationToken.None), itemType,
                values.Where(static value => value.IsReady).Select(static value => value.Value).ToArray(), physicalItemType: physicalItemType);
        rangeRoots.Add(range, new(values));
        datumRanges.Add(range, new(items, values.Any(DatumMayDefer)));
        frame.Set(instruction.Result, NativeValue.FromRange(range));
    }

    private sealed class DatumSequence(IReadOnlyList<NativeDatum> values) : IAsyncEnumerable<NativeDatum>
    {
        public IAsyncEnumerator<NativeDatum> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new DatumSequenceEnumerator(values, cancellationToken);
    }

    private sealed class DatumSequenceEnumerator(IReadOnlyList<NativeDatum> values, CancellationToken token)
        : IAsyncEnumerator<NativeDatum>
    {
        private int index;
        public NativeDatum Current { get; private set; }
        public ValueTask<bool> MoveNextAsync()
        {
            if (index >= values.Count) return new(false);
            token.ThrowIfCancellationRequested();
            Current = values[index++];
            return new(true);
        }
        public ValueTask DisposeAsync() => default;
    }

    private static async IAsyncEnumerable<NativeValue> EnumerateItems(IReadOnlyList<NativeDatum> values,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        for (int index = 0; index < values.Count; index++)
            yield return await values[index].ResolveAsync(token);
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
        image.CheckRegionBounds(address, layout, count);
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
            ? ReadySnapshot(values, itemType, physicalItemType)
            : new(EnumerateItems(values, CancellationToken.None), itemType,
                values.Where(static value => value.IsReady).Select(static value => value.Value).ToArray(), physicalItemType: physicalItemType);
        rangeRoots.Add(range, new(values));
        datumRanges.Add(range, new(new DatumSequence(values), values.Any(DatumMayDefer)));
        frame.Set(instruction.Result, NativeValue.FromRange(range));
    }

    private NativeStep OpenIterator(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        NativeDatum rangeDatum = activation.Frame.GetDatum(instruction.A);
        if (!rangeDatum.IsReady) return AwaitOpenIteratorAsync(activation, instruction, pc, rangeDatum, token);
        return OpenReadyIterator(activation, instruction.B, pc, rangeDatum.Value, token);
    }

    private NativeStep OpenReadyIterator(Activation activation, int index, int pc, NativeValue range, CancellationToken token)
    {
        activation.Frame.SequenceCursor(index) = default;
        activation.Frame.RangeCursor(index).Close();
        IAsyncEnumerator<NativeDatum>? previous = activation.RemoveIterator(index);
        if (previous is not null)
        {
            ValueTask dispose = previous.DisposeAsync();
            if (!dispose.IsCompletedSuccessfully) return AwaitOpenAfterDisposeAsync(activation, index, pc, range, dispose, token);
        }
        StartIterator(activation, index, range, token);
        return new NativeStep(pc + 1);
    }

    private void StartIterator(Activation activation, int index, NativeValue value, CancellationToken token)
    {
        if (value.RangePlan != 0)
        {
            StartRangePlan(activation, index, value.RangePlanArena, value.RangePlan, token);
            return;
        }
        NativeRange range = value.AsRange();
        if (range.NumberSequence is { } sequence && sequence.UsesToken(token))
        {
            activation.Frame.SequenceCursor(index) = new(sequence);
            activation.Work.Counters.SequenceOpen();
        }
        else activation.SetIterator(index, GetDatumIterator(range, token));
    }

    private async ValueTask<NativeStep> AwaitOpenIteratorAsync(Activation activation, NativeInstruction instruction, int pc, NativeDatum rangeDatum, CancellationToken token)
    {
        NativeValue range = await rangeDatum.ResolveAsync(token);
        activation.Frame.SequenceCursor(instruction.B) = default;
        activation.Frame.RangeCursor(instruction.B).Close();
        IAsyncEnumerator<NativeDatum>? previous = activation.RemoveIterator(instruction.B);
        if (previous is not null) await previous.DisposeAsync();
        StartIterator(activation, instruction.B, range, token);
        return new(pc + 1);
    }

    private async ValueTask<NativeStep> AwaitOpenAfterDisposeAsync(Activation activation, int index, int pc, NativeValue range, ValueTask dispose, CancellationToken token)
    {
        await dispose;
        StartIterator(activation, index, range, token);
        return new(pc + 1);
    }

    private NativeStep NextIterator(Activation activation, NativeInstruction instruction, int pc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ref NativeSequenceCursor cursor = ref activation.Frame.SequenceCursor(instruction.A);
        if ((cursor.State & NativeSequenceCursor.Open) != 0)
        {
            if (!cursor.Next(out double value)) return new NativeStep(instruction.B);
            activation.Frame.SetSequenceValue(instruction.Result, value);
            activation.Work.Counters.SequenceSteps(1, machine: false);
            return new NativeStep(pc + 1);
        }
        ref NativeRangeCursor plan = ref activation.Frame.RangeCursor(instruction.A);
        if (plan.Root != 0)
        {
            ref NativeRangeNode node = ref NativeRangeNode.At(plan.Root);
            if (node.StringItems || node.UsesValues)
            {
                if (!plan.NextValue(guestHeap.References!, token, out NativeValue text)) return new NativeStep(instruction.B);
                activation.Frame.Set(instruction.Result, text);
            }
            else
            {
                if (!plan.Next(guestHeap.References!, token, out double number)) return new NativeStep(instruction.B);
                activation.Frame.SetSequenceValue(instruction.Result, number);
            }
            activation.Work.Counters.RangePlanStep();
            return new NativeStep(pc + 1);
        }
        IAsyncEnumerator<NativeDatum> iterator = activation.Iterator(instruction.A)
            ?? throw new InvalidOperationException("Iterator is not open.");
        ValueTask<bool> next = iterator.MoveNextAsync();
        if (!next.IsCompletedSuccessfully) return AwaitNextIteratorAsync(activation, iterator, instruction, pc, next);
        if (!next.Result) return new NativeStep(instruction.B);
        activation.Frame.SetDatum(instruction.Result, iterator.Current);
        return new NativeStep(pc + 1);
    }

    private static async ValueTask<NativeStep> AwaitNextIteratorAsync(Activation activation, IAsyncEnumerator<NativeDatum> iterator,
        NativeInstruction instruction, int pc, ValueTask<bool> next)
    {
        if (!await next) return new(instruction.B);
        activation.Frame.SetDatum(instruction.Result, iterator.Current);
        return new(pc + 1);
    }

    private static NativeStep CloseIterator(Activation activation, int index, int pc)
    {
        activation.Frame.SequenceCursor(index) = default;
        activation.Frame.RangeCursor(index).Close();
        IAsyncEnumerator<NativeDatum>? iterator = activation.RemoveIterator(index);
        if (iterator is not null)
        {
            ValueTask dispose = iterator.DisposeAsync();
            if (!dispose.IsCompletedSuccessfully) return AwaitCloseIteratorAsync(dispose, pc);
        }
        return new NativeStep(pc + 1);
    }

    private static async ValueTask<NativeStep> AwaitCloseIteratorAsync(ValueTask dispose, int pc)
    {
        await dispose;
        return new(pc + 1);
    }
}
