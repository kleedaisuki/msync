using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

/// <summary>Selected contextual computation before construction of the execution image.</summary>
public static class NativePreparation
{
    public static NativeProgram Prepare(NativeProgram program, ExecutionOptions? options = null,
        CancellationToken cancellationToken = default, CompilationTrace? trace = null)
    {
        if (program.Stage == ArtifactStage.Executable) return program;
        cancellationToken.ThrowIfCancellationRequested();
        NativeRuntimeContext origin = new(options, program.Context);
        return Prepare(program, new BindingContext(origin.WorkingDirectory, origin.HomeDirectory, origin.SourceFile), options, cancellationToken, trace);
    }

    public static NativeProgram Prepare(NativeProgram program, BindingContext binding, ExecutionOptions? options = null,
        CancellationToken cancellationToken = default, CompilationTrace? trace = null)
    {
        if (program.Stage == ArtifactStage.Executable) return program;
        cancellationToken.ThrowIfCancellationRequested();
        NativeProgram prepared = !program.Functions.Any(function => function.Instructions.Any(IsContextual))
            ? program with { Context = binding, Stage = ArtifactStage.Executable, ExecutionPlans = [] }
            : new NativePreparationState(program, options, cancellationToken, binding: binding, trace: trace?.Detail).Run();
        return NativeBackendCompiler.Compile(prepared, new(options, binding), trace);
    }

    private static bool IsContextual(NativeInstruction instruction) => instruction.Op is NativeOp.Transfer or NativeOp.SelectPath
        || (instruction.Op is NativeOp.CallNative or NativeOp.MakeFunction or NativeOp.ComposeFunction) && instruction.Symbol is { } name && name.StartsWith("fs.", StringComparison.Ordinal)
            && NativeSignatures.TryGet(name, out var signature) && signature.Effect == NativeEffect.Read;
}

internal sealed partial class NativePreparationState
{
    private readonly NativeProgram program;
    private readonly CompilationTrace? trace;
    private readonly NativeRuntimeContext context;
    private readonly NativeImage image;
    private readonly NativeMemory memory = new();
    private readonly NativeFrame frame;
    private readonly CancellationToken token;
    private readonly NativeFunctionCode function;
    private readonly bool computingCall;
    private readonly bool pendingWrites;
    private readonly HashSet<NativeAllocation> owned = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> activeCalls;
    private readonly List<NativeConstant> constants;
    private readonly List<NativeSlot> slots;
    private readonly Dictionary<int, NativeInstruction[]> replacements = [];
    private readonly HashSet<int> frozenSelections = [];
    private readonly Dictionary<string, string> canonicalPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<int, NativeAllocation> statics = [];
    private readonly HashSet<int> readyStatics = [];
    private readonly Dictionary<NativeRange, NativeValue[]> snapshots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NativeRange, (int Pc, NativeInstruction Instruction)> selections = new(ReferenceEqualityComparer.Instance);
    private readonly List<NativeExecutionPlan> plans = [];
    private readonly List<(int Pc, int Node, NativeInstruction Instruction)> transfers = [];
    private NativeTransferGraph graph = new();
    private int pc;

    internal NativePreparationState(NativeProgram program, ExecutionOptions? options, CancellationToken token,
        int functionIndex = -1, NativeEnvironment? environment = null, IReadOnlyList<NativeValue>? arguments = null, NativePreparationState? parent = null, BindingContext? binding = null, CompilationTrace? trace = null)
    {
        this.program = program;
        this.trace = trace ?? parent?.trace;
        this.token = token;
        int selected = functionIndex < 0 ? program.EntryPoint : functionIndex;
        computingCall = parent is not null;
        pendingWrites = parent is not null && (parent.pendingWrites || parent.graph.Count != 0);
        context = parent?.context ?? new(options, binding ?? program.Context);
        image = parent?.image ?? new(program, context);
        if (parent is null) context.DescribeFunction = image.DescribeFunction;
        activeCalls = parent?.activeCalls ?? [];
        if (parent is not null)
        {
            memory = parent.memory;
            statics = parent.statics;
            readyStatics = parent.readyStatics;
        }
        function = program.Functions[selected];
        frame = new(image, selected, environment, (arguments ?? []).Select(value => new NativeDatum(value)).ToArray(), memory);
        for (int index = function.CaptureCount; index < function.Slots.Count; index++) owned.Add(frame.Location(index).Allocation);
        constants = program.Constants.ToList();
        slots = function.Slots.ToList();
    }

    internal NativeProgram Run()
    {
        bool[] cyclic = new NativeControlFlow(function.Instructions).Cyclic;
        HashSet<int> visited = [];
        // First-iteration values are not loop invariants. Keep cyclic code residual
        // until preparation has an invariant/iteration representation for it.
        for (pc = 0; pc < function.Instructions.Count && !cyclic[pc] && visited.Add(pc); pc++)
        {
            token.ThrowIfCancellationRequested();
            NativeInstruction instruction = function.Instructions[pc];
            try { if (!Step(instruction)) break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException
                or InvalidOperationException or ArgumentException)
            {
                // A failed observation is residual work, not a compile-time execution failure.
                break;
            }
        }
        Flush();
        return Rewrite();
    }

    private bool Step(NativeInstruction instruction)
    {
        switch (instruction.Op)
        {
            case NativeOp.Nop: return true;
            case NativeOp.Constant: frame.Set(instruction.Result, image.Constant(instruction.A)); return true;
            case NativeOp.Copy:
            case NativeOp.Move:
                if (Known(instruction.A, out _)) frame.Copy(instruction.A, instruction.Result);
                else frame.Drop(instruction.Result);
                if (instruction.Op == NativeOp.Move && instruction.A != instruction.Result) frame.Drop(instruction.A);
                return true;
            case NativeOp.Drop: frame.Drop(instruction.A); return true;
            case NativeOp.FrameAddress: frame.SetPointer(instruction.Result, frame.Location(instruction.A)); return true;
            case NativeOp.BindLocation:
                if (!Known(instruction.B, out _)) return false;
                frame.Bind(instruction.A, frame.Pointer(instruction.B)); return true;
            case NativeOp.Allocate:
                if (!Count(instruction.C, out int count)) return false;
                NativeAllocation fresh = new(image.Layout(instruction.A), instruction.A, count);
                owned.Add(fresh);
                frame.SetPointer(instruction.Result, fresh.Address);
                return true;
            case NativeOp.IndexAddress:
                if (!Known(instruction.A, out _) || !Count(instruction.B, out int index)) return false;
                frame.SetPointer(instruction.Result, image.IndexAddress(frame.Pointer(instruction.A), index, image.Layout(instruction.C))); return true;
            case NativeOp.CopyMemory:
            case NativeOp.ClearMemory:
                return Memory(instruction);
            case NativeOp.MakeType:
                if (instruction.A >= 0 && !Known(instruction.A, out _)) return false;
                SetComputed(instruction, NativeValue.FromType(new((TypeKind)instruction.Immediate,
                    instruction.A < 0 ? null : frame.GetDatum(instruction.A).Value.AsType()))); return true;
            case NativeOp.AddressOffset:
                if (!Known(instruction.A, out _)) return false;
                frame.SetPointer(instruction.Result, image.AddressField(frame.Pointer(instruction.A), instruction.B, instruction.C)); return true;
            case NativeOp.ProjectAddress:
                if (!Known(instruction.A, out _)) return false;
                frame.SetPointer(instruction.Result, image.Project(frame.Pointer(instruction.A), instruction.B)); return true;
            case NativeOp.Load:
                if (Known(instruction.A, out _) && memory.TryRead(frame.Pointer(instruction.A), out NativeDatum loaded))
                    frame.Set(instruction.Result, loaded.Value);
                else frame.Drop(instruction.Result);
                return true;
            case NativeOp.Store:
                if (!Known(instruction.A, out _)) return false;
                if (computingCall && !owned.Contains(frame.Pointer(instruction.A).Allocation)) return false;
                if (Known(instruction.B, out NativeValue stored)) memory.Write(frame.Pointer(instruction.A), new(stored));
                else memory.Clear(frame.Pointer(instruction.A));
                return true;
            case NativeOp.Jump:
                if (instruction.A <= pc) return false;
                pc = instruction.A - 1; return true;
            case NativeOp.Branch:
                if (!Known(instruction.A, out NativeValue condition)) return false;
                int next = condition.AsBool() ? instruction.B : instruction.C;
                if (next <= pc) return false;
                replacements[pc] = [new(NativeOp.Jump, A: next, Source: instruction.Source)];
                pc = next - 1; return true;
            case NativeOp.Unary: return Unary(instruction);
            case NativeOp.Binary:
                if (Known(instruction.A, out NativeValue left) && Known(instruction.B, out NativeValue right))
                    SetComputed(instruction, NativeInterpreter.BinaryValue((NativeBinary)instruction.Immediate, left, right, context));
                else frame.Drop(instruction.Result);
                return true;
            case NativeOp.MachineBinary: case NativeOp.MachineCompare: case NativeOp.MachineCast:
            case NativeOp.MachineIntrinsic: case NativeOp.MachineUnary: return Machine(instruction);
            case NativeOp.Select:
                if (Known(instruction.A, out NativeValue selectionCondition) &&
                    Known(selectionCondition.AsBool() ? instruction.B : instruction.C, out NativeValue selectedValue))
                    SetComputed(instruction, selectedValue);
                else frame.Drop(instruction.Result);
                return true;
            case NativeOp.Narrow:
            case NativeOp.Convert:
                if (Known(instruction.A, out NativeValue converted))
                    SetComputed(instruction, instruction.Op == NativeOp.Narrow
                        ? image.Narrow(converted, image.Type(instruction.Type), instruction.Type, instruction.Source, function.Name, pc)
                        : image.Convert(converted, instruction.Type, instruction.Immediate != 0, context));
                else frame.Drop(instruction.Result);
                return true;
            case NativeOp.MakeRange:
                if (!Arguments(instruction.Inputs, out NativeValue[] items)) { frame.Drop(instruction.Result); return true; }
                NativeRange range = NativeRange.Snapshot(items, image.SourceType(function.Slots[instruction.Result].Type).Element ?? TypeRef.Any);
                snapshots.Add(range, items);
                frame.Set(instruction.Result, NativeValue.FromRange(range)); return true;
            case NativeOp.SelectPath:
                if (!Known(instruction.A, out NativeValue directory)) return false;
                NativeRange selected = NativeHostIO.SelectPath(context.AsPath(directory), instruction.Symbol!, context);
                selections.Add(selected, (pc, instruction));
                frame.Set(instruction.Result, NativeValue.FromRange(selected)); return true;
            case NativeOp.FunctionComponent: case NativeOp.ComposeFunction: return FunctionPart(instruction);
            case NativeOp.MakeFunction: return MakeFunction(instruction);
            case NativeOp.EnvironmentAddress:
                if (!Known(instruction.A, out NativeValue callable) || callable.AsFunction().Environment is not { } environment) return false;
                frame.SetPointer(instruction.Result, environment.Address); return true;
            case NativeOp.OpenIterator:
                if (Known(instruction.A, out NativeValue iterated) && iterated.Kind == NativeKind.Range) FreezeInputs(iterated.AsRange());
                return false;
            case NativeOp.Call: return Call(instruction);
            case NativeOp.CallNative: return NativeCall(instruction);
            case NativeOp.Transfer: return !computingCall && Transfer(instruction);
            case NativeOp.TestStatic: frame.Set(instruction.Result, NativeValue.FromBool(readyStatics.Contains(instruction.A))); return true;
            case NativeOp.StaticAddress:
                if (!statics.TryGetValue(instruction.A, out NativeAllocation? allocation))
                    statics.Add(instruction.A, allocation = new(image.Layout(program.Statics[instruction.A].Layout), program.Statics[instruction.A].Layout));
                frame.SetPointer(instruction.Result, allocation.Address); return true;
            case NativeOp.PublishStatic: readyStatics.Add(instruction.A); return true;
            case NativeOp.Return: return false;
            case NativeOp.Barrier:
                if (computingCall) return false;
                if (instruction.Symbol is not null) return false;
                Flush(); return true;
            case NativeOp.Acquire:
                if (computingCall) return false;
                Flush(); return instruction.A < 0;
            default: return false;
        }
    }

    private bool Memory(NativeInstruction instruction)
    {
        if (!Known(instruction.A, out _) || !Count(instruction.C, out int count)) return false;
        NativeAddress destination = frame.Pointer(instruction.A);
        if (computingCall && !owned.Contains(destination.Allocation)) return false;
        NativeLayout layout = image.Layout(checked((int)instruction.Immediate));
        image.CheckRegionBounds(destination, layout, count);
        if (instruction.Op == NativeOp.ClearMemory) memory.ClearRegionTrusted(destination, layout, count);
        else
        {
            if (!Known(instruction.B, out _)) return false;
            NativeAddress source = frame.Pointer(instruction.B);
            image.CheckRegionBounds(source, layout, count);
            memory.CopyRegionTrusted(source, destination, layout, count);
        }
        return true;
    }

    private bool Known(int slot, out NativeValue value)
    {
        if (slot >= 0 && memory.TryRead(frame.Location(slot), out NativeDatum datum)) { value = datum.Value; return true; }
        value = default;
        return false;
    }

    private bool Count(int slot, out int count)
    {
        count = 1;
        if (slot < 0) return true;
        if (!Known(slot, out NativeValue value)) return false;
        double number = value.AsNumber();
        if (!double.IsFinite(number) || number < 0 || number > int.MaxValue || number != Math.Truncate(number)) return false;
        count = (int)number;
        return true;
    }

    private bool Arguments(IReadOnlyList<int>? inputs, out NativeValue[] values)
    {
        values = new NativeValue[inputs?.Count ?? 0];
        for (int index = 0; index < values.Length; index++)
            if (!Known(inputs![index], out values[index])) return false;
        return true;
    }

    private bool Unary(NativeInstruction instruction)
    {
        if (!Known(instruction.A, out NativeValue value)) { frame.Drop(instruction.Result); return true; }
        NativeUnary operation = (NativeUnary)instruction.Immediate;
        NativeValue type = default;
        if (operation == NativeUnary.IsType && !Known(instruction.B, out type))
        { frame.Drop(instruction.Result); return true; }
        NativeValue result = operation == NativeUnary.HasLayout
            ? NativeValue.FromBool(value.Kind == NativeKind.Address && image.HasLayout(instruction.B, value.AsAddress()))
            : NativeInterpreter.UnaryValue(operation, value, type);
        SetComputed(instruction, result);
        return true;
    }
}
