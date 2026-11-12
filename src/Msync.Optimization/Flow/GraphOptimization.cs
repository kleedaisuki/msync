namespace Msync.Optimization;

/// <summary>Reaching definitions and backwards demand over the executable stack CFG.</summary>
internal static class GraphOptimization
{
    private const int Unpromoted = -1;
    private static readonly DefinitionSet FreshStorage = new(Unpromoted);

    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants,
        IReadOnlyList<InstructionEffect>? summaries = null, ConstantInterner? interner = null)
        => OptimizeWithAnalysis(module, function, constants, summaries, interner, new(module));

    internal static FunctionCode OptimizeWithAnalysis(Module module, FunctionCode function, List<Value> constants,
        IReadOnlyList<InstructionEffect>? summaries, ConstantInterner? interner, FunctionAnalysisContext context)
    {
        interner ??= new(constants);
        var previous = function;
        while (true)
        {
            var graph = new Graph(module, previous, constants, summaries, interner, context.DefUse(previous));
            var next = graph.Rewrite();
            if (next.Instructions.SequenceEqual(previous.Instructions))
                return next.LocalCount == previous.LocalCount ? previous : next;
            previous = next;
        }
    }

    private sealed class Node(Instruction instruction, int index, SparseDefUse definitions)
    {
        internal readonly Instruction Instruction = instruction;
        internal readonly int Index = index;
        private readonly ReadOnlyMemory<DefinitionSet> operands = definitions.InputMemory(index);
        internal ReadOnlySpan<DefinitionSet> Inputs => operands.Span;
        internal readonly int StackInputs = definitions.StackInputCount(index);
        internal readonly bool Reachable = definitions.IsReachable(index);
        internal Value? Constant;
        internal bool ConstantVisited;
        internal bool ConstantResolving;
        internal bool Root;
        internal string? NativeName;
        internal bool Kept;
        internal int LiveOutputs;
    }

    private sealed class Graph(Module module, FunctionCode function, List<Value> constants,
        IReadOnlyList<InstructionEffect>? summaries, ConstantInterner interner, SparseDefUse definitions, bool simplifySameSuccessor = true)
    {
        private readonly Node[] nodes = function.Instructions.Select((instruction, index) => new Node(instruction, index, definitions)).ToArray();
        private readonly HashSet<int> demanded = [];
        private readonly Queue<int> demands = new();
        private readonly HashSet<int> mutableSlots = function.Instructions.Where(instruction => instruction.Op == OpCode.LoadCell).Select(instruction => instruction.A).ToHashSet();
        private readonly HashSet<int> externalCells = Enumerable.Range(0, function.CaptureCount).ToHashSet();
        private readonly Dictionary<int, HashSet<int>> scopeWrites = [];
        private readonly Dictionary<int, HashSet<int>> cellWrites = [];
        private readonly int[] cellParents = Enumerable.Range(0, function.LocalCount).ToArray();
        private readonly Dictionary<int, List<int>> cellAliases = [];
        private readonly HashSet<int> observedCells = [];
        private readonly List<int>?[] localDefinitions = new List<int>?[function.LocalCount];
        private readonly HashSet<int> escapedScopes = [];
        private readonly Dictionary<int, List<int>> equivalent = [];
        private readonly int[] representatives = Enumerable.Range(0, function.Instructions.Count * 2).ToArray();
        private Dictionary<int, DefinitionSet>? promotionInputs;
        private List<int>? transfers;
        private bool removedSameSuccessor;

        internal FunctionCode Rewrite()
        {
            DiscoverStorageActivations();
            EquateStackJoins();
            DiscoverCellAliases();
            DiscoverEscapes();
            foreach (var node in nodes.Where(node => node.Reachable)) Resolve(node);
            DiscoverWrites();
            for (var pc = 0; pc < nodes.Length; pc++)
            {
                var node = nodes[pc];
                if (!node.Reachable || !IsRoot(node)) continue;
                Keep(pc);
            }
            while (demands.TryDequeue(out var token))
            {
                if (token < 0) continue;
                var pc = token / 2;
                var node = nodes[pc];
                node.LiveOutputs |= 1 << (token % 2);
                Keep(pc);
                // A raw load can observe earlier cell activation even when its
                // activation's own stack result was discarded.
                if (promotionInputs is { } storage && storage.TryGetValue(pc, out var activations))
                    foreach (var promotion in activations)
                        if (promotion != Unpromoted) Keep(promotion);
                if (node.Instruction.Op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage && mutableSlots.Contains(node.Instruction.A)
                    && observedCells.Add(CellRoot(node.Instruction.A)))
                {
                    foreach (var slot in cellAliases[CellRoot(node.Instruction.A)])
                    {
                        if (cellWrites.TryGetValue(slot, out var mutations))
                            foreach (var mutation in mutations) Keep(mutation);
                        if (localDefinitions[slot] is { } definitions)
                            foreach (var definition in definitions) Demand(definition * 2);
                    }
                }
                if (scopeWrites.TryGetValue(pc, out var writes))
                    foreach (var write in writes) Keep(write);
            }
            return Emit();
        }

        private void DiscoverStorageActivations()
        {
            // Promotion is a change to storage identity, not a stack value
            // definition. Keep this DCE-specific lattice outside def-use.
            HashSet<int>? seen = null;
            Dictionary<int, int>? slots = null;
            foreach (var node in nodes)
            {
                var instruction = node.Instruction;
                if (simplifySameSuccessor && instruction.Op is OpCode.Transfer or OpCode.LocalTransfer)
                    (transfers ??= []).Add(node.Index);
                if (instruction.Op == OpCode.LoadStorage && mutableSlots.Contains(instruction.A)
                    || instruction.Op == OpCode.LoadCell && !(seen ??= []).Add(instruction.A))
                {
                    slots ??= [];
                    slots.TryAdd(instruction.A, slots.Count);
                }
            }
            if (slots is null) return;
            var graph = definitions.Graph;
            var states = new DefinitionSet[]?[graph.Blocks.Count];
            var queued = new bool[states.Length];
            Queue<int> pending = new();
            var initial = new DefinitionSet[slots.Count];
            Array.Fill(initial, FreshStorage);
            Merge(0, initial);
            while (pending.TryDequeue(out int block))
            {
                queued[block] = false;
                var state = (DefinitionSet[])states[block]!.Clone();
                var range = graph.Blocks[block];
                for (int pc = range.Start; pc < range.End; pc++)
                {
                    var instruction = nodes[pc].Instruction;
                    if (instruction.Op is not (OpCode.InitializeLocal or OpCode.LoadCell or OpCode.LoadStorage)
                        || !slots.TryGetValue(instruction.A, out int storage)) continue;
                    if (instruction.Op == OpCode.InitializeLocal)
                    {
                        state[storage] = FreshStorage;
                        continue;
                    }
                    var preceding = state[storage];
                    if (preceding.Count > 1 || !preceding.Contains(Unpromoted))
                    {
                        var observed = promotionInputs ??= [];
                        observed[pc] = observed.GetValueOrDefault(pc).Union(preceding);
                    }
                    if (instruction.Op == OpCode.LoadCell) state[storage] = Promote(preceding, pc);
                }
                foreach (int target in graph.Successors(block)) Merge(target, state);
            }

            void Merge(int block, DefinitionSet[] incoming)
            {
                bool changed = false;
                if (states[block] is { } previous)
                {
                    for (int index = 0; index < incoming.Length; index++)
                    {
                        var merged = previous[index].Union(incoming[index]);
                        changed |= merged.Count != previous[index].Count;
                        previous[index] = merged;
                    }
                }
                else
                {
                    states[block] = (DefinitionSet[])incoming.Clone();
                    changed = true;
                }
                if (!changed || queued[block]) return;
                queued[block] = true;
                pending.Enqueue(block);
            }
        }

        private static DefinitionSet Promote(DefinitionSet incoming, int pc)
        {
            if (!incoming.Contains(Unpromoted)) return incoming;
            // LoadCell reuses an existing cell. Only paths that can still hold
            // plain storage acquire a new activation definition here.
            DefinitionSet result = new(pc);
            foreach (var preceding in incoming)
                if (preceding != Unpromoted) result = result.Union(new(preceding));
            return result;
        }

        private Value? Resolve(DefinitionSet tokens)
        {
            Value? result = null;
            foreach (var token in tokens)
            {
                if (token < 0) return null;
                var value = Resolve(nodes[token / 2]);
                if (value is null || result is not null && !ValueRepresentation.Same(result, value)) return null;
                result = value;
            }
            return result;
        }

        private Value? Resolve(Node node)
        {
            if (node.ConstantVisited) return node.Constant;
            if (node.ConstantResolving) return null;
            node.ConstantResolving = true;
            Value? Input(int index) => index < node.Inputs.Length ? Resolve(node.Inputs[index]) : null;
            var instruction = node.Instruction;
            var left = node.Inputs.Length > 0 ? Input(0) : null;
            var right = node.Inputs.Length > 1 ? Input(1) : null;
            if (instruction.Op == OpCode.Call && instruction.B >= 0
                && left is FunctionValue { NativeName: not null } native) node.NativeName = native.NativeName;
            node.Constant = instruction.Op switch
            {
                OpCode.Constant => ConstantInterner.IsImmutable(constants[instruction.A]) ? constants[instruction.A] : null,
                OpCode.True => Value.True,
                OpCode.False => Value.False,
                OpCode.Null => Value.Null,
                OpCode.LoadLocal or OpCode.LoadStorage when !mutableSlots.Contains(instruction.A) => left,
                OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Dup => left,
                OpCode.MakeClosure when instruction.B == 0 => new FunctionValue(instruction.A),
                OpCode.SpecializeClosure when instruction.A >= 0 && left is FunctionValue function && instruction.Text is null => function with { Function = instruction.A },
                OpCode.Not when left is BoolValue boolean => Bool(!boolean.Data),
                OpCode.IsNull when left is not null => Bool(left is NullValue),
                OpCode.Negate when left is NumberValue number => new NumberValue(-number.Data),
                OpCode.Add when left is NumberValue a && right is NumberValue b => new NumberValue(a.Data + b.Data),
                OpCode.Add when left is StringValue a && right is StringValue b => new StringValue(a.Data + b.Data),
                OpCode.Subtract when left is NumberValue a && right is NumberValue b => new NumberValue(a.Data - b.Data),
                OpCode.Multiply when left is NumberValue a && right is NumberValue b => new NumberValue(a.Data * b.Data),
                OpCode.Divide when left is NumberValue a && right is NumberValue b => new NumberValue(a.Data / b.Data),
                OpCode.Remainder when left is NumberValue a && right is NumberValue b => new NumberValue(a.Data % b.Data),
                OpCode.Equal when Comparable(left, right) => Bool(Compare(left!, right!) == 0),
                OpCode.NotEqual when Comparable(left, right) => Bool(Compare(left!, right!) != 0),
                OpCode.Less when Comparable(left, right) => Bool(Compare(left!, right!) < 0),
                OpCode.LessEqual when Comparable(left, right) => Bool(Compare(left!, right!) <= 0),
                OpCode.Greater when Comparable(left, right) => Bool(Compare(left!, right!) > 0),
                OpCode.GreaterEqual when Comparable(left, right) => Bool(Compare(left!, right!) >= 0),
                OpCode.IsType when left is not null && right is TypeValue expected => Bool(expected.Data.Accepts(left is TypeValue actual ? actual.Data : left.Type)),
                OpCode.Convert when instruction.Text == "String" && left is StringValue => left,
                OpCode.Convert when instruction.Text == "String" && left is PathValue path && module.Stage == ArtifactStage.Executable => new StringValue(path.NativeText),
                OpCode.Convert when instruction.Text == "Path" && left is PathValue => left,
                OpCode.Convert when instruction.Text == "Path" && left is StringValue text && module.Context is { } context => new PathValue(text.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory),
                OpCode.PathJoin when left is PathValue path && right is PathValue or StringValue && (module.Stage == ArtifactStage.Executable || path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl) => PathSemantics.JoinValue(path, right),
                OpCode.NativeCall => ResolveNative(node),
                OpCode.Call when node.NativeName is not null => ResolveNative(node, 1),
                OpCode.LoadField => ResolveMember(node),
                OpCode.Qualify when left is ConnectionValue { Kind: ConnectionKind.Local or ConnectionKind.Ssh or ConnectionKind.Wsl } connection && (connection.Kind != ConnectionKind.Local || connection.Address.Length == 0) && right is PathValue path => PureEvaluation.Qualify(connection, path, module.Context),
                _ => null
            };
            node.ConstantResolving = false;
            node.ConstantVisited = true;
            return node.Constant;
        }

        private Value? ResolveNative(Node node, int skip = 0)
        {
            var arguments = new Value[node.Inputs.Length - skip];
            for (var index = 0; index < arguments.Length; index++)
            {
                var argument = Resolve(node.Inputs[index + skip]);
                if (argument is null) return null;
                arguments[index] = argument;
            }
            return PureEvaluation.Native(node.NativeName ?? node.Instruction.Text, arguments, module);
        }

        private Value? ResolveMember(Node node)
        {
            var allocations = AllocationOrigins(node.Inputs[0], []);
            if (allocations is null) return null;
            Value? result = null;
            foreach (var allocation in allocations)
            {
                if (escapedScopes.Contains(allocation)) return null;
                var scope = nodes[allocation];
                var index = ScopeOperands.Ordinal(module, node.Instruction, scope.Instruction.A);
                if (index < 0) return null;
                foreach (var write in nodes.Where(candidate => candidate.Reachable && candidate.Instruction.Op == OpCode.StoreField))
                {
                    var targets = AllocationOrigins(write.Inputs[0], []);
                    if (targets is null || targets.Contains(allocation)) return null;
                }
                var value = Resolve(scope.Inputs[index]);
                if (value is null || result is not null && !ValueRepresentation.Same(result, value)) return null;
                result = value;
            }
            return result;
        }

        private bool HasDeclaredMember(Node node)
        {
            var allocations = AllocationOrigins(node.Inputs[0], []);
            return allocations is not null && allocations.All(index =>
                ScopeOperands.Ordinal(module, node.Instruction, nodes[index].Instruction.A) >= 0);
        }

        private static Value Bool(bool value) => value ? Value.True : Value.False;
        private static bool Comparable(Value? left, Value? right) => left is BoolValue && right is BoolValue || left is NumberValue && right is NumberValue || left is StringValue && right is StringValue;
        private static int? Compare(Value left, Value right) => (left, right) switch
        {
            (BoolValue a, BoolValue b) => a.Data.CompareTo(b.Data),
            (NumberValue a, NumberValue b) => NumberSemantics.Compare(a.Data, b.Data),
            (StringValue a, StringValue b) => TextSemantics.CompareOrdinal(a.Data, b.Data),
            _ => throw new InvalidOperationException()
        };

        private int Representative(int token)
        {
            while (representatives[token] != token)
            {
                representatives[token] = representatives[representatives[token]];
                token = representatives[token];
            }
            return token;
        }

        private void EquateStackJoins()
        {
            // A surviving stack column must exist on every incoming edge. Its
            // discarded consumers still pop it on paths that do not use it.
            foreach (var node in nodes.Where(node => node.Reachable))
                for (var index = 0; index < node.StackInputs; index++)
                {
                    var first = -1;
                    foreach (var token in node.Inputs[index])
                    {
                        if (token < 0) continue;
                        if (first < 0) first = token;
                        else representatives[Representative(token)] = Representative(first);
                    }
                }
            for (var token = 0; token < representatives.Length; token++)
            {
                var key = Representative(token);
                if (key == token) continue;
                if (!equivalent.TryGetValue(key, out var group)) equivalent[key] = group = [key];
                group.Add(token);
            }
        }

        private int CellRoot(int slot)
        {
            while (cellParents[slot] != slot)
            {
                cellParents[slot] = cellParents[cellParents[slot]];
                slot = cellParents[slot];
            }
            return slot;
        }

        private void DiscoverCellAliases()
        {
            // Raw capture and storage instructions can put the same cell into
            // several slots. Demand and invalidation follow storage identity.
            bool external = false;
            void Origins(DefinitionSet tokens, HashSet<int> seen, HashSet<int> slots)
            {
                foreach (var token in tokens)
                {
                    if (token < 0 || !seen.Add(token)) continue;
                    var node = nodes[token / 2];
                    if (node.Instruction.Op == OpCode.GetCapture) { external = true; continue; }
                    if (node.Instruction.Op == OpCode.LoadCell || node.Instruction.Op == OpCode.LoadStorage && mutableSlots.Contains(node.Instruction.A))
                    { slots.Add(node.Instruction.A); continue; }
                    if (node.Instruction.Op is OpCode.Dup or OpCode.InitializeLocal)
                        Origins(node.Inputs[0], seen, slots);
                }
            }
            mutableSlots.UnionWith(externalCells);
            foreach (var node in nodes.Where(node => node.Reachable && node.Instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal))
                (localDefinitions[node.Instruction.A] ??= []).Add(node.Index);
            bool changed;
            do
            {
                changed = false;
                foreach (var node in nodes.Where(node => node.Reachable && node.Instruction.Op == OpCode.InitializeLocal))
                {
                    var slots = new HashSet<int>();
                    external = false;
                    Origins(node.Inputs[0], [], slots);
                    var destination = node.Instruction.A;
                    if (external) { changed |= externalCells.Add(destination); changed |= mutableSlots.Add(destination); }
                    foreach (var source in slots)
                    {
                        changed |= mutableSlots.Add(destination);
                        var left = CellRoot(source); var right = CellRoot(destination);
                        if (left != right) { cellParents[right] = left; changed = true; }
                    }
                }
            } while (changed);
            foreach (var slot in mutableSlots)
            {
                var root = CellRoot(slot);
                if (!cellAliases.TryGetValue(root, out var aliases)) cellAliases[root] = aliases = [];
                aliases.Add(slot);
            }
            foreach (var aliases in cellAliases.Values)
                if (aliases.Any(externalCells.Contains)) externalCells.UnionWith(aliases);
        }

        private void DiscoverEscapes()
        {
            var seen = new HashSet<int>();
            void Visit(int token)
            {
                if (token < 0 || !seen.Add(token)) return;
                var node = nodes[token / 2];
                if (node.Instruction.Op == OpCode.NewScope) escapedScopes.Add(token / 2);
                if (node.Instruction.Op == OpCode.LoadField)
                {
                    var receivers = AllocationOrigins(node.Inputs[0], []);
                    if (receivers is not null)
                    {
                        foreach (var receiver in receivers)
                        {
                            var allocation = nodes[receiver];
                            var field = ScopeOperands.Ordinal(module, node.Instruction, allocation.Instruction.A);
                            if (field < 0) continue;
                            foreach (var input in allocation.Inputs[field]) Visit(input);
                        }
                        return;
                    }
                }
                foreach (var input in node.Inputs) foreach (var dependency in input) Visit(dependency);
            }
            foreach (var node in nodes.Where(node => node.Reachable && node.Instruction.Op is OpCode.Call or OpCode.ProcessCall or OpCode.NativeCall or OpCode.LoadCell or OpCode.PublishStatic or OpCode.StoreStaticField))
                foreach (var input in node.Inputs) foreach (var token in input) Visit(token);
        }

        private void DiscoverWrites()
        {
            for (var pc = 0; pc < nodes.Length; pc++)
            {
                var node = nodes[pc];
                if (!node.Reachable || node.Instruction.Op != OpCode.StoreField) continue;
                var scopes = AllocationOrigins(node.Inputs[0], []);
                if (scopes is null || !HasDeclaredMember(node)) { node.Root = true; continue; }
                // Structural stores still admit the actual field payload even
                // if this analysis can see the receiver's layout.
                if (ScopeOperands.Projection(module, node.Instruction).Cases.Count == 0) node.Root = true;
                foreach (var scope in scopes)
                {
                    if (!scopeWrites.TryGetValue(scope, out var writes)) scopeWrites[scope] = writes = [];
                    writes.Add(pc);
                    var allocation = nodes[scope];
                    var member = ScopeOperands.Ordinal(module, node.Instruction, allocation.Instruction.A);
                    foreach (var token in allocation.Inputs[member])
                    {
                        if (token < 0) continue;
                        var storage = nodes[token / 2].Instruction;
                        if (storage.Op != OpCode.LoadStorage || !mutableSlots.Contains(storage.A)) continue;
                        if (!cellWrites.TryGetValue(storage.A, out var mutations)) cellWrites[storage.A] = mutations = [];
                        mutations.Add(pc);
                    }
                }
            }
        }

        private HashSet<int>? AllocationOrigins(DefinitionSet tokens, HashSet<int> seen)
        {
            var allocations = new HashSet<int>();
            foreach (var token in tokens)
            {
                if (token < 0) return null;
                if (!seen.Add(token)) continue;
                var node = nodes[token / 2];
                if (node.Instruction.Op == OpCode.NewScope) { allocations.Add(token / 2); continue; }
                if (node.Instruction.Op is OpCode.LoadLocal or OpCode.LoadStorage && mutableSlots.Contains(node.Instruction.A)) return null;
                if (node.Instruction.Op is not (OpCode.LoadLocal or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Dup) || node.Inputs.Length != 1) return null;
                var nested = AllocationOrigins(node.Inputs[0], seen);
                if (nested is null) return null;
                allocations.UnionWith(nested);
            }
            return allocations.Count == 0 ? null : allocations;
        }

        private bool IsRoot(Node node)
        {
            if (node.Root || node.Instruction.Op == OpCode.StoreLocal && externalCells.Contains(node.Instruction.A)) return true;
            if (node.Instruction.A == node.Index + 1)
            {
                if (node.Instruction.Op == OpCode.Jump) return false;
                if (simplifySameSuccessor && node.Instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse && HasImmediateCondition(node))
                {
                    removedSameSuccessor = true;
                    return false;
                }
            }
            if (node.Constant is not null && HasOutput(node.Instruction)) return false;
            return node.Instruction.Op switch
            {
                OpCode.Nop or OpCode.Constant or OpCode.Null or OpCode.True or OpCode.False or OpCode.LoadLocal or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.LoadCell or OpCode.Dup or OpCode.Pop => false,
                OpCode.MakeClosure or OpCode.MakeRange or OpCode.MakeLazyRange or OpCode.DeferRange or OpCode.IsNull or OpCode.SelectPath => false,
                OpCode.NewScope => false,
                OpCode.StoreField => false,
                OpCode.NativeCall => InstructionEffects.NativeEffects(node.Instruction.Text) != InstructionEffect.None,
                OpCode.PathJoin => !CompatiblePathOperands(node),
                OpCode.Convert => Resolve(node.Inputs[0]) is not (PathValue or StringValue),
                OpCode.Call when node.NativeName is not null => InstructionEffects.NativeEffects(node.NativeName) != InstructionEffect.None,
                OpCode.Call when node.Instruction.B > 0 && summaries is not null && Resolve(node.Inputs[0]) is FunctionValue { NativeName: null } callee
                    && (callee.Captures?.Count ?? 0) <= module.Functions[node.Instruction.B - 1].LocalCount =>
                    (summaries[node.Instruction.B - 1] & ~InstructionEffect.Capture) != InstructionEffect.None,
                OpCode.LoadField => !HasDeclaredMember(node),
                OpCode.LoadBuiltin => node.Instruction.Text?.TrimStart('$') is not ("std" or "local" or "pwd" or "file" or "env" or "path" or "wsl"),
                _ => true
            };
        }

        private bool HasImmediateCondition(Node node)
        {
            var conditions = node.Inputs[0];
            if (conditions.Count == 0) return false;
            foreach (var token in conditions)
            {
                if (token < 0) return false;
                var producer = nodes[token / 2];
                if (producer.Constant is not BoolValue && producer.Instruction.Op is not (OpCode.IsNull or OpCode.IsType or OpCode.RegexMatch))
                    return false;
            }
            return true;
        }

        private bool CompatiblePathOperands(Node node) => Resolve(node.Inputs[0]) is PathValue or StringValue
            && Resolve(node.Inputs[1]) is PathValue or StringValue;

        private void Demand(int token)
        {
            if (token < 0) return;
            if (!equivalent.TryGetValue(Representative(token), out var group))
            {
                if (demanded.Add(token)) demands.Enqueue(token);
                return;
            }
            foreach (var peer in group)
                if (demanded.Add(peer)) demands.Enqueue(peer);
        }
        private void Keep(int pc)
        {
            var node = nodes[pc];
            if (node.Kept) return;
            node.Kept = true;
            if (node.Instruction.Op == OpCode.StoreLocal && mutableSlots.Contains(node.Instruction.A))
                foreach (var token in definitions.DefinitionsBefore(pc, node.Instruction.A)) Demand(token);
            if (node.Constant is not null && HasOutput(node.Instruction)) return;
            if (node.Instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse && Resolve(node.Inputs[0]) is BoolValue) return;
            foreach (var operand in node.Inputs[(node.NativeName is null ? 0 : 1)..]) foreach (var token in operand) Demand(token);
        }

        private FunctionCode Emit()
        {
            var positions = new int[nodes.Length + 1];
            var emitted = new List<Instruction>();
            var temporary = -1;
            for (var pc = 0; pc < nodes.Length; pc++)
            {
                positions[pc] = emitted.Count;
                var node = nodes[pc];
                if (!node.Reachable) continue;
                var instruction = node.Instruction;
                bool DroppedInputs() => !node.Kept || node.Constant is not null && HasOutput(instruction)
                    || instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse && Resolve(node.Inputs[0]) is BoolValue;
                if (DroppedInputs())
                    for (var input = node.StackInputs - 1; input >= 0; input--)
                        if (node.Inputs[input].Overlaps(demanded)) emitted.Add(new(OpCode.Pop, Span: instruction.Span));
                if (!node.Kept) continue;
                if (instruction.Op == OpCode.Dup)
                {
                    if (node.Constant is { } duplicated)
                    {
                        if ((node.LiveOutputs & 1) != 0) emitted.Add(Literal(duplicated, instruction.Span));
                        if ((node.LiveOutputs & 2) != 0) emitted.Add(Literal(duplicated, instruction.Span));
                    }
                    else if (node.LiveOutputs == 3) emitted.Add(instruction);
                    continue;
                }
                if (instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse && Resolve(node.Inputs[0]) is BoolValue condition)
                {
                    if (condition.Data == (instruction.Op == OpCode.JumpIfTrue)) emitted.Add(new(OpCode.Jump, instruction.A, Span: instruction.Span));
                    continue;
                }
                if (node.Constant is { } value && HasOutput(instruction))
                    emitted.Add(instruction.Op == OpCode.Constant && value is not (BoolValue or NullValue)
                        ? instruction : Literal(value, instruction.Span));
                else emitted.Add(node.NativeName is null ? instruction : new(OpCode.NativeCall, instruction.A, Text: node.NativeName, Span: instruction.Span));
                if (HasOutput(instruction) && node.LiveOutputs == 0)
                    emitted.Add(pc + 1 < nodes.Length && nodes[pc + 1].Instruction.Op == OpCode.Pop
                        ? nodes[pc + 1].Instruction : new(OpCode.Pop, Span: instruction.Span));
                if (node.NativeName is not null && node.Constant is null && node.Inputs[0].Overlaps(demanded))
                {
                    if (node.LiveOutputs != 0)
                    {
                        if (temporary < 0) temporary = function.LocalCount;
                        emitted.Add(new(OpCode.InitializeLocal, temporary, Span: instruction.Span));
                    }
                    emitted.Add(new(OpCode.Pop, Span: instruction.Span));
                    if (node.LiveOutputs != 0) emitted.Add(new(OpCode.LoadLocal, temporary, Span: instruction.Span));
                }
            }
            positions[^1] = emitted.Count;
            if (removedSameSuccessor && transfers is { } transferSites && ChangedTransferDisposition(transferSites, emitted, positions))
                return new Graph(module, function, constants, summaries, interner, definitions, simplifySameSuccessor: false).Rewrite();
            for (var index = 0; index < emitted.Count; index++)
            {
                var instruction = emitted[index];
                emitted[index] = instruction.Op switch
                {
                    OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady => instruction with { A = positions[instruction.A] },
                    OpCode.IteratorNext => instruction with { B = positions[instruction.B] },
                    _ => instruction
                };
            }
            return function with { Instructions = emitted, LocalCount = temporary < 0 ? function.LocalCount : temporary + 1 };
        }

        private bool ChangedTransferDisposition(List<int> transferSites, List<Instruction> emitted, int[] positions)
        {
            // The VM interprets an adjacent Pop as permission to admit a scalar
            // transfer without awaiting its result. Check the emitted boundary,
            // including transfers whose result was below the removed condition.
            foreach (int site in transferSites)
            {
                if (!nodes[site].Kept) continue;
                var next = site + 1;
                var emittedNext = positions[site] + 1;
                bool discarded = next < nodes.Length && nodes[next].Instruction.Op == OpCode.Pop;
                if (discarded != (emittedNext < emitted.Count && emitted[emittedNext].Op == OpCode.Pop)) return true;
            }
            return false;
        }

        private Instruction Literal(Value value, SourceSpan? span)
        {
            if (value is NullValue) return new(OpCode.Null, Span: span);
            if (value is BoolValue boolean) return new(boolean.Data ? OpCode.True : OpCode.False, Span: span);
            return new(OpCode.Constant, interner.Intern(value), Span: span);
        }

        private static bool HasOutput(Instruction instruction) => instruction.Op == OpCode.Acquire ? instruction.A != 0 : instruction.Op is not (OpCode.Nop or OpCode.Pop or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady or OpCode.Return or OpCode.Barrier or OpCode.ExecutePlan or OpCode.PublishStatic);
    }
}
