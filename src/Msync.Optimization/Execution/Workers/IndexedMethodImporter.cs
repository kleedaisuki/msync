using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using static Msync.Optimization.Execution.IndexedMethodPlan;

namespace Msync.Optimization.Execution;

internal sealed partial class IndexedMethodImporter(Module image, ExecutionRegion graph,
    Func<int, ExecutionTargets.Target?>? predicates, BoundWorkerMode mode)
{
    private enum ValueKind : byte { Unknown, Scalar, Projection, Null, Word, NumberLiteral }
    private readonly record struct Value(ValueKind Kind, int Id);

    private bool prefix => mode is BoundWorkerMode.ArenaPrefix or BoundWorkerMode.ArenaWritePrefix;
    private bool writeNumbers => mode == BoundWorkerMode.VectorWrite;
    private bool writeArena => mode is BoundWorkerMode.ArenaWrite or BoundWorkerMode.ArenaWritePrefix;
    private bool scalarWorker => mode == BoundWorkerMode.Scalar;

    private readonly FunctionCode body = image.Functions[graph.FunctionId];
    private readonly List<NativeWorkerInstruction> code = [];
    private readonly List<ImmutableArray<NativeWorkerPc>> logical = [];
    private readonly List<NativeWorkerPc> pending = [];
    private readonly List<NativeWorkerPredicate> comparisons = [];
    private readonly Dictionary<int, int> entries = [];
    private readonly Dictionary<Access, int> accessIds = [];
    private readonly List<Access> accesses = [];
    private readonly HashSet<CallGuard> calls = [];
    private readonly Dictionary<int, int> inputs = [];
    private readonly List<NativeWorkerType?> types = [];
    private readonly List<int> parents = [];
    private readonly Dictionary<int, ExecutionTargets.Target?> localPredicates = [];
    private readonly Dictionary<int, ExecutionRegion?> inlineBodies = [];
    private readonly Value[] locals = new Value[image.Functions[graph.FunctionId].LocalCount];
    private readonly Value[] values = new Value[graph.ValueCount];
    private int storage = -1;
    private int storageCountAccess = -1;
    private int result = -1;
    private readonly HashSet<int> writableParameters = [];
    private int scalarStores;
    private bool returnsNull;

    private int InputCount => body.CaptureCount + body.ParameterCount;

    internal bool Prepare([NotNullWhen(true)] out IndexedMethodPlan? plan, out IndexedMethodFailure failure)
    {
        plan = null;
        failure = new(-1, default);
        if (graph.Blocks.IsEmpty) return false;
        var frontier = prefix ? SelectPrefix() : null;
        if (prefix && frontier is null) return false;
        foreach (var block in graph.Blocks)
        {
            if (frontier is not null && (!frontier.Blocks.Contains(block.StartPc) || frontier.Stops.Contains(block.StartPc)))
                continue;
            foreach (var op in block.Operations)
            {
                if (op.RequiresCanonicalDispatch
                    || IndexedMethodPreparation.StopsReadonlyVersion(op.Effects) && !AdmissibleStore(op.Instruction)
                    || op.Instruction.Op is OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal
                        && (uint)op.Instruction.A >= (uint)body.LocalCount)
                {
                    failure = new(op.Pc, op.Effects);
                    return false;
                }
                if (writeNumbers && op.Instruction is { Op: OpCode.StoreLocal or OpCode.InitializeLocal, A: var slot }
                    && slot >= body.CaptureCount && slot < InputCount)
                    writableParameters.Add(slot);
                if (!writeNumbers && op.Instruction is { Op: OpCode.StoreLocal or OpCode.InitializeLocal, A: var written })
                    TrackScalarWrite(written);
            }
        }

        var definitions = Definitions(frontier);
        foreach (var block in graph.Blocks)
        {
            if (frontier is not null && !frontier.Blocks.Contains(block.StartPc)) continue;
            entries.Add(block.StartPc, code.Count);
            var defined = (BitArray)definitions[block.StartPc].Clone();
            if (frontier?.Stops.Contains(block.StartPc) == true)
            {
                if (PrepareExit(block.StartPc, defined)) continue;
                failure = new(block.StartPc, default);
                return false;
            }
            EnterBlockParameters(block);
            foreach (var op in block.Operations)
            {
                pending.Add(new(graph.FunctionId, op.Pc));
                if (Lower(op, defined)) continue;
                failure = new(op.Pc, op.Effects);
                return false;
            }
            if (!LowerExit(block))
            {
                var end = block.Operations[^1];
                failure = new(end.Pc, end.Effects);
                return false;
            }
        }

        if (result < 0 || (scalarWorker ? scalarIntrinsics == 0 : storage < 0)
            || writeNumbers && (scalarStores == 0 || !returnsNull)
            || writeArena && (arenaStores == 0 || !prefix && !returnsNull)
            || !ResolveTypes(out var slotTypes)) return false;
        if (!FinalizeCode(slotTypes)) return false;
        var parameters = scalarWorker ? inputs.Values.Concat(WordParameters()).ToImmutableArray() : inputs.Values.ToImmutableArray();
        int stride = arenaRows ? ArenaNumberRowLayout.Stride : writeNumbers ? sizeof(double) : 0;
        var worker = new NativeWorkerPlan([.. code], slotTypes, parameters, result, stride)
        { LogicalInstructions = [.. logical], Predicates = [.. comparisons] };
        var inputSlots = inputs.Select(pair => new Input(pair.Key, slotTypes[pair.Value]));
        var nativeInputs = (scalarWorker ? inputSlots.Concat(WordGuards()) : inputSlots).ToImmutableArray();
        if (scalarWorker && !ProveWordCounts(worker, nativeInputs)) return false;
        int maximumRows = int.MaxValue;
        if (writeNumbers)
        {
            if (!ProveScalarRows(worker, nativeInputs, out maximumRows)) return false;
            if (storageCountAccess < 0) storageCountAccess = Project(new(AccessKind.Count, storage, 0)).Id;
        }
        plan = new(worker, [.. accesses], [.. calls], storage, nativeInputs)
        {
            WritesScalarStorage = writeNumbers,
            UsesArenaRows = arenaRows,
            WritesArenaRows = writeArena,
            ReturnsNull = returnsNull,
            StorageCountAccess = storageCountAccess,
            MaximumRows = maximumRows,
            WritableParameters = [.. writableParameters],
            Exits = PrepareExitMaps(),
            GuestResult = guestResult,
            SpillCount = prefix ? Math.Max(1, exits.Max(exit => exit.Live.Count)) : 0
        };
        return true;
    }

    private Dictionary<int, BitArray> Definitions(Prefix? frontier)
    {
        var incoming = graph.Blocks.ToDictionary(block => block.StartPc, _ => new BitArray(body.LocalCount, true));
        var entry = incoming[0];
        for (int slot = InputCount; slot < entry.Length; slot++) entry[slot] = false;
        bool changed;
        do
        {
            changed = false;
            foreach (var block in graph.Blocks)
            {
                if (frontier is not null && (!frontier.Blocks.Contains(block.StartPc) || frontier.Stops.Contains(block.StartPc)))
                    continue;
                var outgoing = (BitArray)incoming[block.StartPc].Clone();
                foreach (var op in block.Operations)
                    if (op.Instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal)
                        outgoing[op.Instruction.A] = true;
                foreach (var edge in block.Successors)
                {
                    if (edge.TargetPc == 0 || !incoming.TryGetValue(edge.TargetPc, out var target)) continue;
                    for (int slot = InputCount; slot < target.Length; slot++)
                        if (target[slot] && !outgoing[slot]) { target[slot] = false; changed = true; }
                }
            }
        } while (changed);
        return incoming;
    }

    private bool Lower(ExecutionOperation op, BitArray defined)
    {
        var instruction = op.Instruction;
        switch (instruction.Op)
        {
            case OpCode.Nop or OpCode.Dup or OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.Return:
                return true;
            case OpCode.Pop:
                return Operand(op, 0).Kind != ValueKind.Unknown;
            case OpCode.LoadLocal:
                int local = instruction.A;
                if (local < InputCount)
                {
                    if (!writableParameters.Contains(local)) return Set(op, Slot(local));
                    return InitializeParameter(local) && Set(op, locals[local]);
                }
                return defined[local] && locals[local].Kind != ValueKind.Unknown && Set(op, locals[local]);
            case OpCode.InitializeLocal or OpCode.StoreLocal:
                if (instruction.A < InputCount && (!writableParameters.Contains(instruction.A)
                    || !InitializeParameter(instruction.A)) || !Store(op, instruction.A)) return false;
                defined[instruction.A] = true;
                return true;
            case OpCode.Constant:
                if ((uint)instruction.A >= (uint)image.Constants.Count) return false;
                return image.Constants[instruction.A] switch
                {
                    NumberValue when scalarWorker => Set(op, new(ValueKind.NumberLiteral, instruction.A)),
                    NumberValue n => Set(op, Constant(n.Data)),
                    BoolValue b => Set(op, Constant(b.Data)),
                    _ => false
                };
            case OpCode.True or OpCode.False:
                return Set(op, Constant(instruction.Op == OpCode.True));
            case OpCode.Null when writeNumbers || writeArena:
                return Set(op, new(ValueKind.Null, -1));
            case OpCode.LoadField:
                return TryMember(Operand(op, 0), instruction, out var member) && Set(op, member);
            case OpCode.AdmitScope:
                var checkedReceiver = Operand(op, 0);
                return checkedReceiver.Kind == ValueKind.Projection
                    && Set(op, Project(new(AccessKind.AdmittedScope, checkedReceiver.Id, instruction.A)));
            case OpCode.NativeCall:
                return scalarWorker ? ScalarIntrinsic(op) : Native(op);
            case OpCode.Call:
                return !scalarWorker && Call(op);
            case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Negate:
                return ScalarOperation(op, NativeWorkerType.Number);
            case OpCode.Less or OpCode.GreaterEqual or OpCode.Equal or OpCode.NotEqual:
                return Compare(op);
            case OpCode.Not:
                return ScalarOperation(op, NativeWorkerType.Bool);
            default:
                return false;
        }
    }

    private bool InitializeParameter(int slot)
    {
        if (locals[slot].Kind != ValueKind.Unknown) return true;
        if (!Scalar(Slot(slot), out var value)) return false;
        locals[slot] = value;
        return true;
    }

    private bool AdmissibleStore(Instruction instruction) =>
        writeNumbers && instruction is { Op: OpCode.NativeCall, Text: "storage.vector_set", A: 3 }
        || writeArena && ExecutionArenaOperation.Writes(ExecutionArenaOperation.Bind(instruction));

    private bool Store(ExecutionOperation operation, int slot)
    {
        var value = Operand(operation, 0);
        ref var local = ref locals[slot];
        if (!writeNumbers && local.Kind == ValueKind.Unknown && CanAliasScalar(slot, value))
        {
            local = value;
            return true;
        }
        bool word = value.Kind == ValueKind.Word;
        if (!word && !Scalar(value, out value)) return false;
        var kind = word ? ValueKind.Word : ValueKind.Scalar;
        if (local.Kind == ValueKind.Unknown) local = new(kind, NewSlot());
        if (local.Kind != kind || !Unify(local.Id, value.Id)) return false;

        // The original loaded value is a snapshot of the slot. Do not
        // replace an outstanding stack use with the post-store value.
        if (operation.StackBefore.Depth > 1)
        {
            var before = new ExecutionValueId[operation.StackBefore.Depth];
            graph.CopyStack(operation.StackBefore, before);
            int saved = -1;
            foreach (var existing in before.AsSpan(0, before.Length - 1))
            {
                ref var live = ref values[existing.Index];
                if (live.Id != local.Id || live.Kind is not (ValueKind.Scalar or ValueKind.Word))
                    continue;
                if (saved < 0)
                {
                    saved = NewSlot();
                    Move(saved, local.Id);
                }
                live = live with { Id = saved };
            }
        }

        Move(local.Id, value.Id);
        return true;
    }

    private bool TryMember(Value receiver, Instruction instruction, out Value value)
    {
        value = default;
        if (receiver.Kind != ValueKind.Projection) return false;
        value = Project(new(AccessKind.Member, receiver.Id, instruction.A));
        return true;
    }

    private bool Native(ExecutionOperation op)
    {
        var arena = ExecutionArenaOperation.Bind(op.Instruction);
        if (arena != ArenaOperation.None) return !writeNumbers && Arena(op, arena);
        if (!writeNumbers) return false;
        switch (op.Instruction)
        {
            case { Text: "storage.vector_get", A: 2 }:
                return TryLoadVector(Operand(op, 0), Operand(op, 1), out var item) && Set(op, item);
            case { Text: "storage.vector_size", A: 1 }:
                return TryCount(Operand(op, 0), out var count) && Set(op, count);
            case { Text: "storage.vector_set", A: 3 }:
                var receiver = Operand(op, 0);
                if (receiver.Kind != ValueKind.Projection || receiver.Id != storage
                    || !Scalar(Operand(op, 1), out var address) || !Require(address.Id, NativeWorkerType.Index)
                    || !Scalar(Operand(op, 2), out var replacement) || !Require(replacement.Id, NativeWorkerType.Number))
                    return false;
                Emit(new(NativeWorkerOp.StoreNumber, Left: address.Id, Right: replacement.Id));
                scalarStores++;
                return Set(op, new(ValueKind.Null, -1));
            default:
                return false;
        }
    }

    private bool TryCount(Value receiver, out Value value)
    {
        value = default;
        if (receiver.Kind != ValueKind.Projection || storage != -1 && storage != receiver.Id) return false;
        storage = receiver.Id;
        value = Project(new(AccessKind.Count, receiver.Id, 0));
        storageCountAccess = value.Id;
        return true;
    }

    private bool TryLoadVector(Value receiver, Value index, out Value value)
    {
        value = default;
        if (receiver.Kind != ValueKind.Projection || storage != -1 && storage != receiver.Id
            || !Scalar(index, out var ordinal) || !Require(ordinal.Id, NativeWorkerType.Index)) return false;
        storage = receiver.Id;
        // The owner can change at the next selected store, not this load.
        int register = NewSlot(NativeWorkerType.Number);
        Emit(new(NativeWorkerOp.LoadNumber, register, ordinal.Id));
        value = new(ValueKind.Scalar, register);
        return true;
    }

    private bool Call(ExecutionOperation op)
    {
        var instruction = op.Instruction;
        var callee = Operand(op, 0);
        if (callee.Kind != ValueKind.Projection || !TryCall(instruction, out int target, out int? identity)) return false;
        var selected = image.Functions[target];
        if (selected.ParameterCount != instruction.A) return false;
        calls.Add(new(callee.Id, selected.CaptureCount, identity));
        if (PredicateOperation(target) is { } native)
        {
            if (!Scalar(Operand(op, 1), out var left) || !Scalar(Operand(op, 2), out var right)
                || !Require(left.Id, NativeWorkerType.Number) || !Require(right.Id, NativeWorkerType.Number)) return false;
            int boolean = NewSlot(NativeWorkerType.Bool);
            comparisons.Add(new(code.Count, target, Predicate(target)!));
            Emit(new(native, boolean, left.Id, right.Id));
            return Set(op, new(ValueKind.Scalar, boolean));
        }
        return TryInlineVectorRead(target, callee, op, out var resultValue) && Set(op, resultValue);
    }

    private ExecutionTargets.Target? Predicate(int target)
    {
        if (predicates is not null) return predicates(target);
        if (!localPredicates.TryGetValue(target, out var predicate))
            localPredicates.Add(target, predicate = ExecutionTargets.BuildPredicate(image.Functions[target]));
        return predicate;
    }

    private NativeWorkerOp? PredicateOperation(int target)
    {
        if ((uint)target >= (uint)image.Functions.Count || Predicate(target) is not { Numeric: true } relation)
            return null;
        const int less = 1 << (int)ExecutionTargets.Relation.Less;
        const int rightNaN = 1 << (int)ExecutionTargets.Relation.RightNaN;
        return relation.Mask switch
        {
            less => NativeWorkerOp.NumberLess,
            less | rightNaN => NativeWorkerOp.NumberLessNaNLast,
            _ => null
        };
    }

    private bool TryCall(Instruction instruction, out int target, out int? identity)
    {
        identity = null;
        target = instruction.B - 1;
        if (instruction.Text is { } dispatch)
        {
            try
            {
                var targets = FunctionDispatch.Targets(dispatch);
                var supported = targets.Count == 1 ? targets.ToArray()
                    : targets.Where(candidate => PredicateOperation(candidate.Value) is not null).ToArray();
                if (supported is not [var only]) return false;
                identity = only.Key;
                target = only.Value;
            }
            catch (MsyncException) { return false; }
        }
        return instruction.B >= 0 && (uint)target < (uint)image.Functions.Count;
    }

    // A call is transparent only if this backend can execute its selected
    // body. A whole-image effect summary would not establish that capability.
    private ExecutionRegion? InlineBody(int target)
    {
        if (inlineBodies.TryGetValue(target, out var region)) return region;
        var function = image.Functions[target];
        if (!writeNumbers || function.ReturnType.WithoutNull != TypeRef.Number
            || !ExecutionRegionImporter.TryImport(image, target, out region, out _)
            || region.Blocks is not [var block] || !block.Successors.IsEmpty
            || block.Operations.Any(op => IndexedMethodPreparation.StopsReadonlyVersion(op.Effects)))
            region = null;
        inlineBodies.Add(target, region);
        return region;
    }

    private bool TryInlineVectorRead(int target, Value callee, ExecutionOperation call, out Value returned)
    {
        returned = default;
        if (InlineBody(target) is not { } inline) return false;
        var block = inline.Blocks[0];
        var function = image.Functions[target];
        var arguments = new Value[function.LocalCount];
        var outputs = new Value[inline.ValueCount];
        for (int i = 0; i < function.CaptureCount; i++) arguments[i] = Project(new(AccessKind.Capture, callee.Id, i));
        for (int i = 0; i < function.ParameterCount; i++) arguments[function.CaptureCount + i] = Operand(call, i + 1);
        int firstPrivate = function.CaptureCount + function.ParameterCount;
        foreach (var op in block.Operations)
        {
            pending.Add(new(target, op.Pc));
            Value Input(int i) => outputs[op.Inputs[i].Index];
            var instruction = op.Instruction;
            Value value;
            switch (instruction.Op)
            {
                case OpCode.LoadLocal when (uint)instruction.A < (uint)arguments.Length:
                    value = arguments[instruction.A];
                    break;
                case OpCode.LoadField when TryMember(Input(0), instruction, out value):
                    break;
                case OpCode.AdmitScope when Input(0) is { Kind: ValueKind.Projection } projection:
                    value = Project(new(AccessKind.AdmittedScope, projection.Id, instruction.A));
                    break;
                case OpCode.NativeCall when instruction is { Text: "storage.vector_get", A: 2 }
                    && TryLoadVector(Input(0), Input(1), out value):
                    break;
                case OpCode.NativeCall when instruction is { Text: "storage.vector_size", A: 1 }
                    && TryCount(Input(0), out value):
                    break;
                case OpCode.Return:
                    if (op.Inputs.Length != 1) return false;
                    returned = Input(0);
                    return returned.Kind == ValueKind.Scalar
                        || returned.Kind == ValueKind.Projection && accesses[returned.Id].Kind == AccessKind.Count;
                case OpCode.StoreLocal or OpCode.InitializeLocal
                    when instruction.A >= firstPrivate && instruction.A < arguments.Length:
                    arguments[instruction.A] = Input(0);
                    continue;
                case OpCode.Pop when Input(0).Kind is ValueKind.Projection or ValueKind.Scalar:
                case OpCode.Dup or OpCode.Nop:
                    continue;
                default:
                    return false;
            }
            if (value.Kind == ValueKind.Unknown) return false;
            outputs[op.Result!.Value.Index] = value;
        }
        return false;
    }

    private bool ScalarOperation(ExecutionOperation op, NativeWorkerType type)
    {
        bool flexible = writeNumbers && op.Instruction.Op is OpCode.Add or OpCode.Multiply;
        if (!Scalar(Operand(op, 0), out var left) || !flexible && !Require(left.Id, type)) return false;
        Value right = default;
        if (op.Inputs.Length == 2 && (!Scalar(Operand(op, 1), out right) || !Unify(left.Id, right.Id))) return false;
        int slot = NewSlot(flexible ? null : type);
        if (!Unify(slot, left.Id)) return false;
        NativeWorkerOp operation = op.Instruction.Op switch
        {
            OpCode.Add => NativeWorkerOp.NumberAdd,
            OpCode.Subtract => NativeWorkerOp.NumberSubtract,
            OpCode.Multiply => NativeWorkerOp.NumberMultiply,
            OpCode.Divide => NativeWorkerOp.NumberDivide,
            OpCode.Negate => NativeWorkerOp.NumberNegate,
            _ => NativeWorkerOp.BoolNot,
        };
        Emit(new(operation, slot, left.Id, right.Id));
        return Set(op, new(ValueKind.Scalar, slot));
    }

    private bool Compare(ExecutionOperation op)
    {
        if (!Scalar(Operand(op, 0), out var left) || !Scalar(Operand(op, 1), out var right)
            || !Unify(left.Id, right.Id)) return false;
        int slot = NewSlot(NativeWorkerType.Bool);
        var compare = op.Instruction.Op switch
        {
            OpCode.Equal => NativeWorkerOp.NumberEqual,
            OpCode.NotEqual => NativeWorkerOp.NumberNotEqual,
            OpCode.Less => NativeWorkerOp.NumberLess,
            _ => NativeWorkerOp.NumberGreaterEqual,
        };
        Emit(new(compare, slot, left.Id, right.Id));
        return Set(op, new(ValueKind.Scalar, slot));
    }

    private bool LowerExit(ExecutionBlock block)
    {
        var end = block.Operations[^1];
        if (end.Instruction.Op == OpCode.Return)
        {
            if ((writeNumbers || writeArena)
                && (end.Inputs.Length == 0 || end.Inputs.Length == 1 && Operand(end, 0).Kind == ValueKind.Null))
            {
                returnsNull = true;
                ReturnStatus(0);
                return true;
            }
            if (writeNumbers || writeArena || end.Inputs.Length != 1 || !Scalar(Operand(end, 0), out var value))
                return false;
            if (prefix)
            {
                if (guestResult < 0) guestResult = NewSlot();
                if (!Unify(guestResult, value.Id)) return false;
                Move(guestResult, value.Id);
                ReturnStatus(0);
                return true;
            }
            if (result < 0) result = NewSlot();
            if (!Unify(result, value.Id)) return false;
            Emit(new(NativeWorkerOp.Return, Left: value.Id));
            return true;
        }
        return !block.Successors.IsEmpty && LowerEdges(block, end);
    }

    private Value Slot(int slot) => Project(new(AccessKind.Slot, -1, slot));

    private TypeRef SlotType(int slot) => slot >= body.CaptureCount && body.ParameterTypes is { } parameters
        && (uint)(slot - body.CaptureCount) < (uint)parameters.Count
        ? parameters[slot - body.CaptureCount] : TypeRef.Any;

    private Value Project(Access access)
    {
        if (!accessIds.TryGetValue(access, out int id))
        {
            id = accesses.Count;
            accesses.Add(access);
            accessIds.Add(access, id);
        }
        return new(ValueKind.Projection, id);
    }

    private Value Constant(double value)
    {
        int slot = NewSlot();
        Emit(new(NativeWorkerOp.NumberConstant, slot, Constant: value));
        return new(ValueKind.Scalar, slot);
    }

    private Value Constant(bool value)
    {
        int slot = NewSlot(NativeWorkerType.Bool);
        Emit(new(NativeWorkerOp.BoolConstant, slot, Data: value ? 1 : 0));
        return new(ValueKind.Scalar, slot);
    }

    private Value Operand(ExecutionOperation op, int index) => values[op.Inputs[index].Index];
    private bool Set(ExecutionOperation op, Value value)
    {
        values[op.Result!.Value.Index] = value;
        return true;
    }

    private bool Scalar(Value value, out Value scalar)
    {
        scalar = value;
        if (value.Kind == ValueKind.Scalar) return true;
        if (value.Kind == ValueKind.NumberLiteral)
        {
            scalar = Constant(((NumberValue)image.Constants[value.Id]).Data);
            return true;
        }
        if (value.Kind == ValueKind.Word)
        {
            int number = NewSlot(NativeWorkerType.Number);
            Emit(new(NativeWorkerOp.Word32ToNumber, number, value.Id));
            scalar = new(ValueKind.Scalar, number);
            return true;
        }
        if (value.Kind == ValueKind.Projection)
        {
            if (!inputs.TryGetValue(value.Id, out int slot))
            {
                var access = accesses[value.Id];
                var type = access.Kind == AccessKind.Slot ? SlotType(access.Operand) : TypeRef.Any;
                inputs.Add(value.Id, slot = NewSlot(access.Kind is AccessKind.Count or AccessKind.ArenaRoot ? NativeWorkerType.Index
                    : type.WithoutNull == TypeRef.Bool ? NativeWorkerType.Bool : null));
            }
            scalar = new(ValueKind.Scalar, slot);
            return true;
        }
        return false;
    }

    private void Emit(NativeWorkerInstruction instruction)
    {
        code.Add(instruction);
        logical.Add([.. pending]);
        pending.Clear();
    }

    private void Move(int destination, int source)
    {
        if (destination == source) return;
        Unify(destination, source);
        Emit(new(NativeWorkerOp.Move, destination, source));
    }
}
