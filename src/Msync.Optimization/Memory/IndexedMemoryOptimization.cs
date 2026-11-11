using System.Collections.Immutable;

namespace Msync.Optimization;

internal static partial class IndexedMemoryOptimization
{
    private readonly record struct Atom(int Number, bool Storage = false);
    private readonly record struct Index(int Number, long Constant = 0, bool Known = false);
    private readonly record struct Access(string Operation, int Buffer, Index Index);
    private readonly record struct Clobber(int Buffer, IndexedRegion Region, int? Index);
    private sealed record Read(int Position, Atom Result, Phi? Merge = null);
    private readonly record struct Source(Read? Read, int Local = -1);
    private readonly record struct PhiInput(ControlFlowEdge Edge, Source Source);
    private sealed record Phi(PhiInput[] Inputs);
    private readonly record struct Identity(int Kind, int Left, int Right = 0, int Third = 0, string? Name = null);

    private const int Definition = 0, CellRead = 1, CellsPhi = 2, MemoryPhi = 3, FieldPhi = 4,
        CellsClobber = 5, MemoryClobber = 6, CellsWrite = 7, MemberVersion = 8, FieldWrite = 9,
        Operator = 10, MemberRead = 80;

    private sealed class State(Atom[] locals, List<Atom> stack)
    {
        internal readonly Atom[] Locals = locals;
        internal readonly List<Atom> Stack = stack;
        internal ImmutableDictionary<Access, Read> Reads = ImmutableDictionary<Access, Read>.Empty;
        internal ImmutableDictionary<string, int> Fields = ImmutableDictionary<string, int>.Empty;
        internal Join? Join;
        internal ImmutableHashSet<Clobber>? Clobbers;
        internal int Cells;
        internal int Memory;
        internal State Copy() => new((Atom[])Locals.Clone(), [.. Stack])
        {
            Reads = Reads, Fields = Fields, Cells = Cells, Memory = Memory,
            Join = Join, Clobbers = Clobbers
        };
    }
    private sealed class BlockState
    {
        internal State? Output;
        internal State? IteratorFailure;
    }

    internal static FunctionCode Optimize(FunctionCode function, IReadOnlyList<Value> constants) =>
        OptimizeWithAnalysis(function, constants, null);

    internal static FunctionCode OptimizeWithAnalysis(FunctionCode function, IReadOnlyList<Value> constants,
        FunctionAnalysisContext? analysis, CallTargetGraph? callGraph = null,
        Func<IReadOnlyList<FunctionMemoryEffects>>? summaries = null, int originalId = -1)
    {
        int gets = 0, sizes = 0;
        bool unknown = false, repeated = false;
        HashSet<long>? indices = null;
        long first = -1;
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            var instruction = function.Instructions[pc];
            if (instruction.Op != OpCode.NativeCall) continue;
            if (instruction.Text == "storage.buffer_size" && ++sizes == 2) return Run();
            if (instruction.Text != "storage.buffer_get") continue;
            gets++;
            if (pc == 0 || function.Instructions[pc - 1] is not { Op: OpCode.Constant } literal
                || constants[literal.A] is not NumberValue { Data: var index }
                || !(index >= 0 && index <= int.MaxValue && index == Math.Truncate(index))) unknown = true;
            else if (first < 0) first = (long)index;
            else if (first == index) repeated = true;
            else repeated |= !(indices ??= [first]).Add((long)index);
            if (gets >= 2 && (unknown || repeated)) return Run();
        }
        return function;

        FunctionCode Run() => new Analysis(function, constants, analysis?.ControlFlow(function) ?? new(function),
            callGraph, summaries, originalId).Run();
    }

    private sealed partial class Analysis(FunctionCode function, IReadOnlyList<Value> constants, ControlFlowGraph graph,
        CallTargetGraph? callGraph, Func<IReadOnlyList<FunctionMemoryEffects>>? summaries, int originalId)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly Dictionary<Identity, int> identities = [];
        private readonly Dictionary<Value, int> literals = new(ValueRepresentation.Comparer);
        private readonly Dictionary<int, double> numbers = [];
        private readonly Dictionary<int, Source> replacements = [];
        private readonly List<Identity> definitions = [default];
        private HashSet<int>? freshBuffers;
        private Dictionary<int, Atom[]>? captures;
        private Dictionary<int, int>? callTargets;
        private BlockState[] states = [];
        private bool[]? storageSlots;

        internal FunctionCode Run()
        {
            states = new BlockState[graph.Blocks.Count];
            foreach (int index in graph.ReversePostOrder)
            {
                State? input = Input(index);
                if (input is not null) Transfer(graph.Blocks[index], states[index] = new(), input);
            }
            return replacements.Count == 0 ? function : Rewrite();
        }

        private int Number(Identity identity)
        {
            if (identities.TryGetValue(identity, out int number)) return number;
            identities.Add(identity, number = Define(identity));
            return number;
        }

        private int Define(Identity identity = default)
        {
            int number = definitions.Count;
            definitions.Add(identity);
            return number;
        }

        private Atom Unknown(int position, int operand = 0, bool storage = false) =>
            new(Number(new(Definition, position, operand)), storage);

        private Atom Literal(Value value)
        {
            if (value is ScopeValue or FunctionValue or RangeValue or CellValue) return new(Define(), value is CellValue);
            if (!literals.TryGetValue(value, out int number)) literals.Add(value, number = Define());
            if (value is NumberValue numeric) numbers[number] = numeric.Data;
            return new(number);
        }

        private Atom Dereference(State state, Atom value) => value.Storage
            ? new(Number(new(CellRead, value.Number, state.Cells))) : value;

        private Index Subscript(Atom value) => numbers.TryGetValue(value.Number, out double index)
            && IndexedNativeEffect.ExactIndex(index) is int exact
            ? new(0, exact, true) : new(value.Number);

        private State? Input(int index)
        {
            var block = graph.Blocks[index];
            if (index == 0)
            {
                State entry = Initial(block.Start);
                if (!graph.IncomingEdges(index).IsEmpty) ResetLoop(entry, block.Start);
                return entry;
            }

            State? first = null;
            List<JoinEdge>? edges = null;
            ControlFlowEdge firstEdge = default;
            bool missing = false;
            foreach (var edge in graph.IncomingEdges(index))
            {
                if (!graph.IsReachable(edge.Source)) continue;
                var state = edge.Kind == ControlFlowEdgeKind.IteratorExhausted
                    ? states[edge.Source]?.IteratorFailure : states[edge.Source]?.Output;
                if (state is null) { missing = true; continue; }
                if (first is null) { first = state; firstEdge = edge; continue; }
                if (state.Stack.Count != first.Stack.Count) return null;
                (edges ??= [new(firstEdge, first)]).Add(new(edge, state));
            }
            if (first is null) return null;
            State result = first.Copy();
            // An unanalysed predecessor is a clobber boundary; a backedge
            // cannot carry read availability into a new dynamic iteration.
            if (missing)
            {
                ResetLoop(result, block.Start);
                return result;
            }
            if (edges is null) return result;
            var incoming = edges.ToArray();
            bool translate = incoming.All(edge => edge.State.Reads.Count > 0 || edge.State.Join is not null);
            for (int slot = 0; slot < result.Locals.Length; slot++)
                result.Locals[slot] = Merge(incoming, block.Start, slot + 1, result.Locals[slot], slot, stack: false, translate);
            for (int item = 0; item < result.Stack.Count; item++)
                result.Stack[item] = Merge(incoming, block.Start, -item - 1, result.Stack[item], item, stack: true, translate);
            if (incoming.Skip(1).Any(edge => edge.State.Cells != result.Cells)) result.Cells = Number(new(CellsPhi, block.Start));
            if (incoming.Skip(1).Any(edge => edge.State.Memory != result.Memory)) result.Memory = Number(new(MemoryPhi, block.Start));
            foreach (string field in incoming.SelectMany(edge => edge.State.Fields.Keys).Distinct(StringComparer.Ordinal))
                if (incoming.Skip(1).Any(edge => edge.State.Fields.GetValueOrDefault(field) != result.Fields.GetValueOrDefault(field)))
                    result.Fields = result.Fields.SetItem(field, Number(new(FieldPhi, block.Start, Name: field)));
            foreach (var entry in result.Reads)
                if (incoming.Skip(1).Any(edge => !edge.State.Reads.TryGetValue(entry.Key, out var read) || read != entry.Value))
                    result.Reads = result.Reads.Remove(entry.Key);
            result.Join = translate ? new(index, incoming, result) : null;
            result.Clobbers = null;
            return result;
        }

        private State Initial(int start) => new(Enumerable.Range(0, function.LocalCount)
            .Select(slot => Unknown(start, slot + 1, slot < function.CaptureCount + function.ParameterCount)).ToArray(), []);

        private void ResetLoop(State state, int start)
        {
            Invalidate(state, start);
            var storage = storageSlots ??= FindStorageSlots();
            for (int slot = 0; slot < state.Locals.Length; slot++)
                state.Locals[slot] = Unknown(start, slot + 1, storage[slot]);
            for (int item = 0; item < state.Stack.Count; item++) state.Stack[item] = Unknown(start, -item - 1, storage: true);
        }

        private bool[] FindStorageSlots()
        {
            var result = new bool[function.LocalCount];
            Array.Fill(result, true, 0, function.CaptureCount + function.ParameterCount);
            for (int pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op == OpCode.LoadCell
                    || (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal)
                    && (graph.Blocks[graph.BlockAt(pc)].Start == pc || !ProducesPlainValue(code[pc - 1])))
                    result[instruction.A] = true;
            }
            return result;
        }

        private bool ProducesPlainValue(Instruction instruction) => instruction.Op switch
        {
            OpCode.Constant => constants[instruction.A] is not CellValue,
            OpCode.NativeCall when instruction.Text is "storage.buffer_new" or "storage.buffer_snapshot"
                or "storage.buffer_size" => true,
            OpCode.Null or OpCode.True or OpCode.False or OpCode.MakeClosure or OpCode.MakeScope or OpCode.MakeRange
                or OpCode.MakeLazyRange or OpCode.GetIterator or OpCode.SpecializeClosure or OpCode.SelectPath
                or OpCode.DeferRange or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
                or OpCode.Remainder or OpCode.Negate or OpCode.Equal or OpCode.NotEqual or OpCode.Less
                or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.Not or OpCode.IsNull
                or OpCode.IsType => true,
            _ => false
        };

        private void Invalidate(State state, int pc)
        {
            ForgetReads(state);
            ForgetLocations(state, pc);
        }

        private void ForgetLocations(State state, int pc)
        {
            state.Cells = Number(new(CellsClobber, pc));
            state.Memory = Number(new(MemoryClobber, pc));
            state.Fields = ImmutableDictionary<string, int>.Empty;
        }

        private void Transfer(BasicBlock block, BlockState output, State state)
        {
            Atom Pop() { var item = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return item; }
            Atom[] Arguments(int count)
            {
                Atom[] values = new Atom[count];
                for (int index = count - 1; index >= 0; index--) values[index] = Pop();
                return values;
            }
            for (int pc = block.Start; pc < block.End; pc++)
            {
                var instruction = code[pc];
                switch (instruction.Op)
                {
                    case OpCode.Nop: case OpCode.Jump: break;
                    case OpCode.Constant: state.Stack.Add(Literal(constants[instruction.A])); break;
                    case OpCode.True: state.Stack.Add(Literal(Value.True)); break;
                    case OpCode.False: state.Stack.Add(Literal(Value.False)); break;
                    case OpCode.Null: state.Stack.Add(Literal(Value.Null)); break;
                    case OpCode.LoadLocal: state.Stack.Add(Dereference(state, state.Locals[instruction.A])); break;
                    case OpCode.LoadStorage: state.Stack.Add(state.Locals[instruction.A]); break;
                    case OpCode.LoadCell:
                        state.Locals[instruction.A] = Unknown(pc, instruction.A + 1, storage: true);
                        state.Stack.Add(state.Locals[instruction.A]); break;
                    case OpCode.InitializeLocal: state.Locals[instruction.A] = Pop(); break;
                    case OpCode.StoreLocal:
                        Atom stored = Pop();
                        if (state.Locals[instruction.A].Storage)
                        {
                            // The old value might be a cell or a plain value
                            // also copied into a different local. Neither proves
                            // that both locals still read the same value.
                            state.Cells = Number(new(CellsWrite, pc));
                            state.Locals[instruction.A] = Unknown(pc, instruction.A + 1, storage: true);
                        }
                        else state.Locals[instruction.A] = stored;
                        break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup: Atom duplicate = Pop(); state.Stack.Add(duplicate); state.Stack.Add(duplicate); break;
                    case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(); break;
                    case OpCode.Return: if (state.Stack.Count > 0) Pop(); break;
                    case OpCode.GetMember:
                        Atom receiver = Pop();
                        int stamp = Number(new(MemberVersion, state.Memory, state.Cells,
                            state.Fields.GetValueOrDefault(instruction.Text!), instruction.Text));
                        state.Stack.Add(new(Number(new(MemberRead, receiver.Number, stamp))));
                        break;
                    case OpCode.SetMember:
                        Arguments(2);
                        state.Fields = state.Fields.SetItem(instruction.Text!, Number(new(FieldWrite, pc)));
                        state.Cells = Number(new(CellsWrite, pc));
                        state.Stack.Add(Literal(Value.Null)); break;
                    case OpCode.NativeCall: Native(state, instruction, pc, Arguments(instruction.A)); break;
                    case OpCode.Call: case OpCode.ProcessCall:
                        Call(state, instruction, pc, Arguments(instruction.A + 1)); break;
                    case OpCode.Barrier: Invalidate(state, pc); break;
                    case OpCode.ExecutePlan: case OpCode.PlanResult:
                        for (int item = 0; item < instruction.B; item++) Pop();
                        Invalidate(state, pc);
                        if (instruction.Op == OpCode.PlanResult) state.Stack.Add(Unknown(pc));
                        break;
                    case OpCode.Acquire:
                        if (instruction.A != 0) { Atom acquired = Pop(); state.Stack.Add(acquired); }
                        Invalidate(state, pc); break;
                    case OpCode.GetIterator:
                        Pop(); Invalidate(state, pc); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.IteratorNext:
                        Invalidate(state, pc); output.IteratorFailure = state.Copy(); state.Stack.Add(Unknown(pc, storage: true)); break;
                    case OpCode.MakeClosure:
                        var captured = Arguments(instruction.B);
                        var closure = Unknown(pc);
                        if (callGraph is not null && captured.Length > 0) (captures ??= [])[closure.Number] = captured;
                        state.Stack.Add(closure); break;
                    case OpCode.MakeScope: case OpCode.MakeRange: case OpCode.MakeLazyRange:
                        Arguments(instruction.A); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.GetCapture: Pop(); state.Stack.Add(Unknown(pc, storage: true)); break;
                    case OpCode.GetSingleton: case OpCode.LoadBuiltin: state.Stack.Add(Unknown(pc)); break;
                    case OpCode.SetSingleton: Pop(); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.Not: case OpCode.IsNull: case OpCode.Negate: case OpCode.Convert:
                    case OpCode.SpecializeClosure: case OpCode.SelectPath: case OpCode.DeferRange:
                        Unary(state, instruction, Pop()); break;
                    case OpCode.RegexMatch when instruction.Text is not null: Pop(); state.Stack.Add(Unknown(pc)); break;
                    default:
                        Atom right = Pop(), left = Pop();
                        if (instruction.Op is OpCode.Transfer or OpCode.LocalTransfer) Invalidate(state, pc);
                        state.Stack.Add(new(ExpressionNumber(new(Operator + (int)instruction.Op, left.Number, right.Number, Name: instruction.Text))));
                        break;
                }
            }
            output.Output = state;
        }

        private void Unary(State state, Instruction instruction, Atom input)
        {
            var result = new Atom(ExpressionNumber(new(Operator + (int)instruction.Op, input.Number, Name: instruction.Text)));
            if (instruction.Op == OpCode.SpecializeClosure && captures?.TryGetValue(input.Number, out var locations) == true)
                captures[result.Number] = locations;
            state.Stack.Add(result);
        }

        private void Call(State state, Instruction instruction, int pc, Atom[] operands)
        {
            if (instruction.Op == OpCode.Call && instruction.B >= 0 && callGraph is not null && summaries is not null
                && (state.Reads.Count > 0 || state.Join is not null))
            {
                callTargets ??= callGraph.AnalyzeBody(function, constants, originalId).Targets;
                if (callTargets.TryGetValue(pc, out int target))
                {
                    var effect = summaries()[target];
                    if (!effect.UnknownWrites && !effect.HasDeferredInvocation && !effect.HasOrderingBoundary
                        && effect.Enumerates.Count == 0)
                    {
                        foreach (var footprint in effect.IndexedWrites)
                            Kill(state, new(Receiver(state, operands, footprint.Receiver), footprint.Region, footprint.Index));
                        // A successful call can change cells or replace a
                        // member without changing the earlier Buffer object.
                        ForgetLocations(state, pc);
                        state.Stack.Add(Unknown(pc, storage: true));
                        return;
                    }
                }
            }
            Invalidate(state, pc);
            state.Stack.Add(Unknown(pc, storage: true));
        }

        private int Receiver(State state, Atom[] operands, MemoryAccess? receiver)
        {
            if (receiver is null || receiver.Fields.Count != 0 || receiver.Descendants) return 0;
            int index = receiver.Root.Index;
            if (receiver.Root.Kind == MemoryRootKind.Parameter && index + 1 < operands.Length)
                return Dereference(state, operands[index + 1]).Number;
            if (receiver.Root.Kind == MemoryRootKind.Capture
                && captures?.TryGetValue(operands[0].Number, out var locations) == true && index < locations.Length)
                return Dereference(state, locations[index]).Number;
            return 0;
        }

        private void Native(State state, Instruction instruction, int pc, Atom[] arguments)
        {
            string name = instruction.Text!;
            if (name is "storage.buffer_get" or "storage.buffer_size")
            {
                var access = new Access(name, Dereference(state, arguments[0]).Number,
                    name == "storage.buffer_get" ? Subscript(Dereference(state, arguments[1])) : default);
                if (Available(state, access) is { } read)
                {
                    // Keep the selected first read and its failure span on each
                    // path. Only the redundant access is replaced.
                    replacements[pc] = read.Merge is null ? new(read) : ExistingSource(state, read);
                    state.Reads = state.Reads.SetItem(access, read);
                    state.Stack.Add(read.Result);
                }
                else
                {
                    Atom value = Unknown(pc, storage: name == "storage.buffer_get");
                    state.Reads = state.Reads.Add(access, new(pc, value));
                    state.Stack.Add(value);
                }
                return;
            }
            var effect = IndexedNativeEffect.For(name);
            if (effect.Writes != IndexedRegion.None)
            {
                int buffer = Dereference(state, arguments[0]).Number;
                if (effect.Indices == 0) Kill(state, new(buffer, effect.Writes, null));
                else for (int index = 1; index <= effect.Indices; index++)
                {
                    var subscript = Subscript(Dereference(state, arguments[index]));
                    Kill(state, new(buffer, effect.Writes, subscript.Known ? (int)subscript.Constant : null));
                }
            }
            else if ((InstructionEffects.NativeEffects(name) & (InstructionEffect.Write | InstructionEffect.Invoke)) != 0)
                Invalidate(state, pc);
            bool item = NativeSignatures.TryGet(name, out var signature) && signature.ResultRule == NativeResultRule.RangeItem;
            var result = Unknown(pc, storage: item);
            if (name == "storage.buffer_new") (freshBuffers ??= []).Add(result.Number);
            state.Stack.Add(result);
        }

    }
}
