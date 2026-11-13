using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativePreparationState
{
    private bool Call(NativeInstruction instruction)
    {
        if (!Known(instruction.A, out NativeValue value) || !Arguments(instruction.Inputs, out NativeValue[] arguments)) return false;
        NativeFunction callable = value.AsFunction();
        if (callable.NativeName is { } name) return NativeCall(instruction with { Op = NativeOp.CallNative, Symbol = name });
        int target = callable.Function;
        if (instruction.C >= 0)
        {
            if (program.SessionDispatches is not { } tables || !tables[instruction.C].Targets.TryGetValue(target, out target)) return false;
        }
        else if (instruction.B >= 0) target = instruction.B;
        else if (instruction.Symbol is { } dispatch && !FunctionDispatch.Targets(dispatch).TryGetValue(target, out target)) return false;
        if (program.Functions[target].IsTemplate || !CaptureContract(callable.Environment, target) || !activeCalls.Add(target)) return false;
        try
        {
            NativePreparationState selected = new(program, null, token, target, callable.Environment, arguments, this);
            if (!selected.Evaluate(out NativeValue result)) return false;
            SetComputed(instruction, result);
            if (result.Kind == NativeKind.Range)
            {
                if (selected.snapshots.TryGetValue(result.AsRange(), out NativeValue[]? snapshot)) snapshots.TryAdd(result.AsRange(), snapshot);
                if (selected.selections.ContainsKey(result.AsRange())) selections.TryAdd(result.AsRange(), (pc, instruction));
            }
            return true;
        }
        finally { activeCalls.Remove(target); }
    }

    private bool CaptureContract(NativeEnvironment? environment, int target)
    {
        IReadOnlyList<NativeCaptureContract> captures = program.Functions[target].Signature.Captures;
        if (captures.Count == 0) return true;
        if (environment is null || environment.Count != captures.Count) return false;
        for (int index = 0; index < captures.Count; index++)
        {
            NativeCaptureLayout actual = environment.Layout.Captures[index];
            NativeCaptureContract expected = captures[index];
            if (actual.Location != (expected.Mode == NativeCaptureMode.Location)
                || !actual.Type.Equals(image.Type(expected.Type))) return false;
        }
        return true;
    }

    private bool Evaluate(out NativeValue result)
    {
        HashSet<int> visited = [];
        for (pc = 0; pc < function.Instructions.Count && visited.Add(pc); pc++)
        {
            token.ThrowIfCancellationRequested();
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op == NativeOp.Return)
            {
                if (instruction.A < 0) { result = NativeValue.Null; return true; }
                return Known(instruction.A, out result);
            }
            if (!Step(instruction)) break;
        }
        result = default;
        return false;
    }

    private bool MakeFunction(NativeInstruction instruction)
    {
        if (instruction.A < 0)
        {
            frame.Set(instruction.Result, NativeValue.FromFunction(new(-1, NativeName: instruction.Symbol)));
            return true;
        }
        Msync.Core.Native.NativeSignature target = program.Functions[instruction.A].Signature;
        NativeEnvironmentLayout layout = instruction.B < 0
            ? new(target.Captures.Select(capture => new NativeCaptureLayout(image.Type(capture.Type), capture.Mode == NativeCaptureMode.Location)))
            : new(program.Layouts[instruction.B].Fields.Select(field =>
            {
                int pointee = program.Types[field.Type].Pointee;
                return new NativeCaptureLayout(image.Type(pointee >= 0 ? pointee : field.Type), pointee >= 0);
            }));
        NativeEnvironment? environment = instruction.C == 0 ? null : new(layout, instruction.B);
        IReadOnlyList<int> inputs = instruction.Inputs ?? [];
        for (int index = 0; index < inputs.Count; index++)
        {
            if (!Known(inputs[index], out NativeValue input)) return false;
            NativeCaptureLayout capture = layout.Captures[index];
            environment!.Initialize(index, capture.Location ? new(frame.Pointer(inputs[index]), capture.Type) : new NativeCapture(input));
        }
        frame.Set(instruction.Result, NativeValue.FromFunction(new(instruction.A, environment)));
        return true;
    }

    private bool NativeCall(NativeInstruction instruction)
    {
        NativeBuiltinOperation operation = NativeBuiltins.Resolve(instruction.Symbol!);
        Msync.Core.NativeSignature signature = operation.Signature;
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        bool metadata = signature.Name.StartsWith("fs.", StringComparison.Ordinal) && signature.Effect == NativeEffect.Read;
        if (signature.Effect != NativeEffect.Pure && !metadata) return false;
        if ((behavior & NativeBehavior.Invokes) != 0) return false;
        if (!Arguments(instruction.Inputs, out NativeValue[] arguments))
        {
            if (metadata) return false;
            frame.Drop(instruction.Result); return true;
        }
        if (metadata && (pendingWrites || graph.Count > 0)) return false;
        if ((behavior & NativeBehavior.Enumerates) != 0
            && arguments.Any(value => value.Kind == NativeKind.Range && value.AsRange().SnapshotValues is null && !snapshots.ContainsKey(value.AsRange()))) return false;
        NativeValue result = NativeBuiltins.InvokeProducedAsync(operation, arguments, context,
            static (_, _, _) => throw new InvalidOperationException("Contextual computation does not invoke source callbacks."), token)
            .AsTask().GetAwaiter().GetResult();
        if (result.Kind == NativeKind.Range)
        {
            if (result.AsRange().SnapshotValues is { } ready) snapshots.TryAdd(result.AsRange(), ready.ToArray());
            if (metadata)
            {
                selections.TryAdd(result.AsRange(), (pc, instruction));
                if (signature.Name is not ("fs.directory_iterator" or "fs.recursive_directory_iterator"))
                {
                    NativeValue[] snapshot = Snapshot(result.AsRange()).AsTask().GetAwaiter().GetResult();
                    snapshots[result.AsRange()] = snapshot;
                    FreezeSelection(pc, instruction, snapshot, result.AsRange().ItemType);
                }
            }
            frame.Set(instruction.Result, result);
        }
        else SetComputed(instruction, result);
        return true;
    }

    private void SetComputed(NativeInstruction instruction, NativeValue value)
    {
        frame.Set(instruction.Result, value);
        if (Literal(value) is not { } literal) return;
        int index = constants.Count;
        constants.Add(literal);
        replacements[pc] = [new(NativeOp.Constant, instruction.Result, A: index, Type: function.Slots[instruction.Result].Type, Source: instruction.Source)];
    }

    private static NativeConstant? Literal(NativeValue value) => value.Kind switch
    {
        NativeKind.Null => new(NativeKind.Null),
        NativeKind.Bool => new(NativeKind.Bool, Boolean: value.AsBool()),
        NativeKind.Number => new(NativeKind.Number, Number: value.AsNumber()),
        NativeKind.String => new(NativeKind.String, Text: value.AsString()),
        NativeKind.Path => new(NativeKind.Path, Text: value.AsPath().Raw) { Path = value.AsPath() },
        NativeKind.Connection => new(NativeKind.Connection, Text: value.AsConnection().Address, ConnectionKind: value.AsConnection().Kind),
        NativeKind.Type => new(NativeKind.Type, Type: value.AsType()),
        _ => null
    };

    private NativePlanValue? Portable(NativeValue value)
    {
        if (value.Kind == NativeKind.Path) return NativePlanValue.FromPath(value.AsPath());
        if (value.Kind != NativeKind.Range) return Literal(value) is { } scalar ? NativePlanValue.Scalar(scalar) : null;
        NativeRange range = value.AsRange();
        if (!snapshots.TryGetValue(range, out NativeValue[]? values) && range.SnapshotValues is { } ready)
            snapshots.Add(range, values = ready.ToArray());
        if (values is null)
        {
            if (!selections.ContainsKey(range)) return null;
            if (pendingWrites || graph.Count > 0) return null;
            values = Snapshot(range).AsTask().GetAwaiter().GetResult();
            snapshots.Add(range, values);
        }
        List<NativePlanValue> items = [];
        foreach (NativeValue item in values)
        {
            if (Portable(item) is not { } portable) return null;
            items.Add(portable);
        }
        if (selections.TryGetValue(range, out var producer)) FreezeSelection(producer.Pc, producer.Instruction, values, range.ItemType);
        return NativePlanValue.Snapshot(items, range.ItemType);
    }

    private void FreezeInputs(NativeRange range)
    {
        if (pendingWrites || graph.Count != 0) return;
        HashSet<NativeRange> visited = new(ReferenceEqualityComparer.Instance);
        Stack<NativeRange> pending = new(); pending.Push(range);
        while (pending.TryPop(out NativeRange? current))
        {
            if (!visited.Add(current)) continue;
            if (selections.ContainsKey(current)
                && current.Selection?.Directory.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl))
                Portable(NativeValue.FromRange(current));
            foreach (NativeValue retained in current.RetainedValues)
                if (retained.Kind == NativeKind.Range) pending.Push(retained.AsRange());
        }
    }

    private async ValueTask<NativeValue[]> Snapshot(NativeRange range)
    {
        List<NativeValue> values = [];
        await foreach (NativeValue value in range.Items.WithCancellation(token)) values.Add(value);
        return values.ToArray();
    }

    private void FreezeSelection(int source, NativeInstruction instruction, IReadOnlyList<NativeValue> values, TypeRef itemType)
    {
        if (values.Any(value => Literal(value) is null)) return;
        int element = FindType(itemType);
        if (element < 0 || !frozenSelections.Add(source)) return;
        List<NativeInstruction> output = [];
        List<int> inputs = [];
        foreach (NativeValue value in values)
        {
            int constant = constants.Count, slot = slots.Count;
            constants.Add(Literal(value)!);
            slots.Add(new(element));
            inputs.Add(slot);
            output.Add(new(NativeOp.Constant, slot, A: constant, Type: element, Source: instruction.Source));
        }
        output.Add(new(NativeOp.MakeRange, instruction.Result, Inputs: inputs, Type: function.Slots[instruction.Result].Type, Source: instruction.Source));
        replacements[source] = output.ToArray();
    }

    private int FindType(TypeRef requested)
    {
        for (int index = 0; index < program.Types.Count; index++)
            if (program.Types[index].Pointee < 0 && image.SourceType(index) == requested) return index;
        return -1;
    }
}
