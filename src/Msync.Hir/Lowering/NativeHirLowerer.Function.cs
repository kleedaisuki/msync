using System.Collections.Immutable;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    private sealed partial class FunctionLowering(Lowering owner, HirFunctionId id, HirFunction function)
    {
        private readonly List<NativeInstruction> code = [];
        private readonly List<NativeSlot> slots = [];
        private readonly List<LoopState> loops = [];
        private readonly List<int> iterators = [];
        private readonly List<string> acquireRegions = [];
        // Split AllocateScope bindings survive control flow; only lexical Scope
        // initializers have a dominating, statically known allocation base.
        private readonly Dictionary<HirLocationId, BoundField?> boundFields = [];
        private int iteratorCount;
        private int nextRegion;
        private bool reachable = true;
        private HirModule Hir => owner.Hir;

        private readonly record struct BoundField(int Base, int Layout, int Ordinal);

        private sealed record LoopState(HirLoopId Id, int Result, int IteratorDepth, int AcquireDepth)
        {
            internal List<int> Breaks { get; } = [];
        }

        internal NativeFunctionCode Run()
        {
            if (function.IsOpen)
                throw new InvalidOperationException($"Open function '{function.Name}' is not a finalized body.");
            PrepareLocations();
            foreach (var parameter in function.Parameters.Where(owner.IsCaptured)) PromoteEntry(parameter);
            foreach (var capture in function.Captures)
                if (capture.Mode == HirCaptureMode.Value && owner.IsCaptured(capture.Target)) PromoteEntry(capture.Target);
            if (!Hir.IsSession)
                foreach (var location in owner.FunctionLocations(id).Where(owner.SharedImport)) FreshLocation(location);
            int result = Emit(function.Body);
            if (reachable) EmitReturn(result, function.Body.IsNone ? null : Source(Hir.Node(function.Body)));
            var parameters = function.ParameterEntryTypes.IsDefaultOrEmpty
                ? function.Parameters.Select(owner.LocationType).ToArray()
                : function.ParameterEntryTypes.Select(type => owner.Type(type)).ToArray();
            return new(function.Name, slots.ToArray(), code.ToArray(),
                new(parameters, owner.Type(function.Result), owner.CaptureContracts(function.Captures)),
                function.Parameters.Length, function.Captures.Length, iteratorCount)
            { ReflectionSignature = owner.HistoricalSignature(function) };
        }

        private void PrepareLocations()
        {
            for (int i = 0; i < function.Captures.Length; i++)
                FixedSlot(function.Captures[i].Target, i, NativeSlotRole.Capture);
            for (int i = 0; i < function.Parameters.Length; i++)
                FixedSlot(function.Parameters[i], function.Captures.Length + i, NativeSlotRole.Parameter);
            foreach (var location in owner.FunctionLocations(id))
            {
                if (!owner.TrySlot(id, location, out int slot))
                {
                    slot = slots.Count;
                    owner.BindSlot(id, location, slot);
                }
                if (slot < function.Captures.Length + function.Parameters.Length) continue;
                NativeSlotRole role = Hir.Locations[location.Value].Kind == HirLocationKind.Temporary
                    ? NativeSlotRole.Temporary : NativeSlotRole.Local;
                PlaceSlot(location, slot, role);
            }
        }

        private void FixedSlot(HirLocationId location, int slot, NativeSlotRole role)
        {
            if (owner.TrySlot(id, location, out int previous) && previous != slot)
                throw new InvalidOperationException("A persistent callable changed its capture/parameter layout.");
            owner.BindSlot(id, location, slot); PlaceSlot(location, slot, role);
        }

        private void PlaceSlot(HirLocationId location, int slot, NativeSlotRole role)
        {
            while (slots.Count <= slot) slots.Add(new(owner.Type(new TypeRef(TypeKind.Never)), NativeSlotRole.Local));
            slots[slot] = new(owner.LocationType(location), role, owner.IsCaptured(location), Hir.Locations[location.Value].Name);
        }

        private int Register(int type)
        {
            int result = slots.Count; slots.Add(new(type)); return result;
        }
        private int Register(HirTypeId type) => Register(owner.Type(type));
        private int Add(NativeInstruction instruction)
        {
            if (!reachable) return -1;
            code.Add(instruction);
            if (instruction.Op is NativeOp.Jump or NativeOp.Branch or NativeOp.Return or NativeOp.Unreachable)
                reachable = false;
            return code.Count - 1;
        }
        private HirNodeId Child(HirNode node, int index) => Hir.Edges[node.Children.Start + index];
        private int[] Children(HirNode node)
        {
            int[] values = new int[node.Children.Count];
            for (int i = 0; i < values.Length; i++) values[i] = Emit(Child(node, i));
            return values;
        }
        private SourceSpan? Source(HirNode node) => owner.Source(node.Source);
        private int Null(HirSourceRange? source = null) => Literal(new(HirLiteralKind.Null),
            source is { } range ? owner.Source(range) : null);
        private void Copy(int destination, int value, SourceSpan? source = null) =>
            Add(new(NativeOp.Copy, destination, value, Type: slots[destination].Type, Source: source));
        private void PatchJump(int at, int target) => code[at] = code[at] with { A = target };
        private void PatchBranch(int at, int whenTrue, int whenFalse) =>
            code[at] = code[at] with { B = whenTrue, C = whenFalse };

        private int Emit(HirNodeId expression)
        {
            if (expression.IsNone) return Null();
            var node = Hir.Node(expression);
            if (!reachable) return Register(node.Type);
            return node.Kind switch
            {
                HirKind.Error => throw new InvalidOperationException("An erroneous semantic body cannot be lowered."),
                HirKind.Literal => Literal(Hir.Literals[node.Data], Source(node), node.Source.Source.Value),
                HirKind.TypeValue => TypeValue(new(node.Data), Source(node)),
                HirKind.TypeOf => TypeValue(Hir.Node(Child(node, 0)).Type, Source(node)),
                HirKind.NativeFunction => NativeFunction(node),
                HirKind.Builtin => Builtin(node),
                HirKind.ReadLocation or HirKind.ReadStorage => Read(node),
                HirKind.Declare => Declare(node),
                HirKind.Assign => Assign(node),
                HirKind.Discard => Discard(node),
                HirKind.Block => Block(node),
                HirKind.If => Conditional(node),
                HirKind.Match => Match(node),
                HirKind.For => For(node),
                HirKind.Loop => Loop(node),
                HirKind.Break => Break(node),
                HirKind.Return => Return(node),
                HirKind.Unary => Unary(node),
                HirKind.Binary => Binary(node),
                HirKind.Coerce or HirKind.Convert => Conversion(node),
                HirKind.IsNull => Test(node, NativeUnary.IsNull),
                HirKind.IsType => IsType(node),
                HirKind.RegexMatch => Regex(node),
                HirKind.Call => Call(node),
                HirKind.Closure => Closure(Hir.Closures[node.Data].Function,
                    Hir.Closures[node.Data].Captures, node),
                HirKind.TemplateClosure => TemplateClosure(node),
                HirKind.AdaptFunction => Adapt(node),
                HirKind.Scope or HirKind.AllocateScope => Scope(node),
                HirKind.MemberRead => Field(node, false),
                HirKind.MemberAssign => Field(node, true),
                HirKind.Range or HirKind.LazyRange => Range(node),
                HirKind.DeferRange => Defer(node),
                HirKind.Qualify => Pair(node, NativeBinary.Qualify, reverse: true),
                HirKind.QualifyOn => Pair(node, NativeBinary.Qualify),
                HirKind.PathJoin => Pair(node, NativeBinary.PathJoin),
                HirKind.SelectPath => SelectPath(node),
                HirKind.Transfer => Transfer(node),
                HirKind.Barrier => Barrier(node),
                _ => throw new InvalidOperationException($"Unsupported semantic node {node.Kind}.")
            };
        }

        private int Literal(HirLiteral literal, SourceSpan? source, int origin = -1)
        {
            NativeConstant value = literal.Kind switch
            {
                HirLiteralKind.Null => new(NativeKind.Null),
                HirLiteralKind.Bool => new(NativeKind.Bool, Boolean: literal.Boolean),
                HirLiteralKind.Number => new(NativeKind.Number, Number: literal.Number),
                HirLiteralKind.String => new(NativeKind.String, Text: literal.Text!),
                HirLiteralKind.Path => new(NativeKind.Path, Text: literal.Text!),
                HirLiteralKind.Connection => new(NativeKind.Connection, Text: literal.Text ?? "", ConnectionKind: literal.ConnectionKind),
                HirLiteralKind.Type => new(NativeKind.Type, Type: literal.Type!),
                _ => throw new InvalidOperationException($"Invalid literal kind {literal.Kind}.")
            };
            if (literal.Kind == HirLiteralKind.Path && Hir.IsSession)
                value = value with { OriginId = origin, OriginOffset = source?.Start ?? 0 };
            int type = owner.Type(literal.Kind switch
            {
                HirLiteralKind.Null => TypeRef.Null, HirLiteralKind.Bool => TypeRef.Bool,
                HirLiteralKind.Number => TypeRef.Number, HirLiteralKind.String => TypeRef.String,
                HirLiteralKind.Path => TypeRef.Path, HirLiteralKind.Connection => TypeRef.Connection,
                HirLiteralKind.Type => TypeRef.Type,
                _ => throw new InvalidOperationException("Invalid literal representation.")
            });
            int result = Register(type);
            Add(new(NativeOp.Constant, result, owner.Constant(value), Type: type, Source: source)); return result;
        }

        private int TypeValue(HirTypeId type, SourceSpan? source) =>
            Literal(new(HirLiteralKind.Type, Type: owner.Runtime(type)), source);

        private int NativeFunction(HirNode node)
        {
            int result = Register(node.Type);
            if (owner.HistoricalFunction(node.Text, out int target))
            {
                Add(new(NativeOp.MakeFunction, result, target, C: 0, Inputs: [], Source: Source(node)));
                return result;
            }
            Add(new(NativeOp.MakeFunction, result, A: -1, B: owner.EnvironmentLayout([]), Symbol: node.Text, Inputs: [], Source: Source(node))); return result;
        }

        private int Builtin(HirNode node)
        {
            string name = node.Text!.TrimStart('$');
            ConnectionKind kind = name switch
            {
                "local" or "pwd" or "file" => ConnectionKind.Local,
                "wsl" => ConnectionKind.Wsl,
                "env" => ConnectionKind.Environment,
                "path" => ConnectionKind.SearchPath,
                _ => throw new InvalidOperationException($"Unlowered builtin namespace '${name}' has no static HIR construction.")
            };
            return Literal(new(HirLiteralKind.Connection, Text: name is "pwd" or "file" ? name : "",
                ConnectionKind: kind), Source(node));
        }

        private int Address(HirLocationId location, SourceSpan? source = null)
        {
            int result = Register(owner.Pointer(owner.LocationType(location)));
            if (boundFields.TryGetValue(location, out var field) && field is { } bound)
                Add(new(NativeOp.AddressOffset, result, bound.Base, bound.Layout, bound.Ordinal, Source: source));
            else
                Add(new(NativeOp.FrameAddress, result, owner.Slot(id, location), Source: source));
            return result;
        }
        private int Read(HirNode node)
        {
            HirLocationId location = new(node.Data);
            int type = owner.LocationType(location);
            int address = Address(location, Source(node)); int result = Register(type);
            Add(new(NativeOp.Load, result, address, Type: type, Source: Source(node)));
            return Narrow(result, owner.Type(node.Type), Source(node));
        }
        private int Narrow(int value, int type, SourceSpan? source)
        {
            if (slots[value].Type == type) return value;
            int admission = owner.HistoricalAdmission(slots[value].Type, type);
            if (admission != slots[value].Type)
            {
                int widened = Register(admission);
                Copy(widened, value, source);
                value = widened;
            }
            int result = Register(type);
            Add(new(NativeOp.Narrow, result, value, Type: type, Source: source)); return result;
        }
        private void Store(HirLocationId location, int value, bool initialize, SourceSpan? source)
        {
            int address = Address(location, source);
            Add(new(NativeOp.Store, A: address, B: value, C: initialize ? 1 : 0,
                Type: owner.LocationType(location), Source: source));
        }
        private void FreshLocation(HirLocationId location)
        {
            int type = owner.LocationType(location);
            int layout = owner.BindingLayout(type);
            int allocation = Register(owner.Type(TypeRef.Scope));
            int instruction = Add(new(NativeOp.Allocate, allocation, layout));
            owner.Allocation(owner.Function(id), instruction, layout);
            int address = Register(owner.Pointer(type));
            Add(new(NativeOp.AddressOffset, address, allocation, layout, 0));
            Add(new(NativeOp.BindLocation, A: owner.Slot(id, location), B: address));
        }
        private void PromoteEntry(HirLocationId location)
        {
            int type = owner.LocationType(location);
            int previous = Address(location); int value = Register(type);
            Add(new(NativeOp.Load, value, previous, Type: type));
            FreshLocation(location); Store(location, value, true, null);
        }
        private int Declare(HirNode node)
        {
            HirLocationId location = new(node.Data);
            if (owner.IsCaptured(location) && !owner.IsRoot(location) && !owner.SharedImport(location)
                && !boundFields.ContainsKey(location)) FreshLocation(location);
            int value = Emit(Child(node, 0)); Store(location, value, true, Source(node)); return Null();
        }
        private int Assign(HirNode node)
        {
            int value = Emit(Child(node, 0)); Store(new(node.Data), value, false, Source(node)); return Null();
        }
        private int Discard(HirNode node)
        {
            int value = Emit(Child(node, 0)); Add(new(NativeOp.Drop, A: value)); return Null();
        }
        private int Block(HirNode node)
        {
            int value = -1;
            foreach (var child in Hir.Children(node))
            {
                if (value >= 0) Add(new(NativeOp.Drop, A: value));
                value = Emit(child);
            }
            return value < 0 ? Null(node.Source) : value;
        }
        private int Unary(HirNode node) => Test(node,
            (HirUnaryOperator)node.Data == HirUnaryOperator.Not ? NativeUnary.Not : NativeUnary.Negate);
        private int Test(HirNode node, NativeUnary operation)
        {
            int value = Emit(Child(node, 0)); int result = Register(node.Type);
            Add(new(NativeOp.Unary, result, value, Immediate: (int)operation, Source: Source(node))); return result;
        }
        private int IsType(HirNode node)
        {
            int value = Emit(Child(node, 0));
            int target = node.Children.Count == 2 ? Emit(Child(node, 1)) : TypeValue(new(node.Data), Source(node));
            int result = Register(node.Type);
            if (owner.HistoricalTypeTest >= 0)
            {
                int callee = Register(owner.Type(TypeRef.Function));
                Add(new(NativeOp.MakeFunction, callee, owner.HistoricalTypeTest, C: 0, Inputs: [], Source: Source(node)));
                Add(new(NativeOp.Call, result, callee, owner.HistoricalTypeTest, Inputs: [value, target], Source: Source(node)));
                return result;
            }
            Add(new(NativeOp.Unary, result, value, target, Immediate: (int)NativeUnary.IsType, Source: Source(node))); return result;
        }
        private int Conversion(HirNode node)
        {
            int value = Emit(Child(node, 0)); int result = Register(node.Type);
            Add(new(NativeOp.Convert, result, value, Type: owner.Type(new HirTypeId(node.Data)),
                Immediate: node.Kind == HirKind.Convert ? 1 : 0, Source: Source(node))); return result;
        }
        private int Pair(HirNode node, NativeBinary operation, bool reverse = false)
        {
            int first = Emit(Child(node, 0)); int second = Emit(Child(node, 1));
            int result = Register(node.Type);
            Add(new(NativeOp.Binary, result, reverse ? second : first, reverse ? first : second,
                Immediate: (int)operation, Source: Source(node))); return result;
        }
        private int Regex(HirNode node)
        {
            int value = Emit(Child(node, 0));
            int pattern = Literal(new(HirLiteralKind.String, Text: node.Text), Source(node));
            int result = Register(node.Type);
            Add(new(NativeOp.Binary, result, value, pattern, Immediate: (int)NativeBinary.RegexMatch, Source: Source(node))); return result;
        }
        private int SelectPath(HirNode node)
        {
            int value = Emit(Child(node, 0)); int result = Register(node.Type);
            Add(new(NativeOp.SelectPath, result, value, Symbol: node.Text, Source: Source(node))); return result;
        }
        private int Transfer(HirNode node)
        {
            int source = Emit(Child(node, 0)); int target = Emit(Child(node, 1)); int result = Register(node.Type);
            Add(new(NativeOp.Transfer, result, source, target, Source: Source(node))); return result;
        }
    }
}
