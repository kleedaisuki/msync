namespace Msync.Core.Native;

internal sealed partial class NativeAddressAnalysis
{
    internal readonly record struct Target(int Allocation, int Slot, int Field = -1);
    internal interface ISymbolic { }
    internal enum SymbolKind : byte { Formal, Application, Adaptation, Projection, Read, Element, Range }
    private readonly record struct Symbol(SymbolKind Kind, ISymbolic Value, bool Layout);

    internal sealed class Points : ISymbolic
    {
        private HashSet<Symbol>? symbolic;
        // These typed projections own no sets: all unresolved edges and their
        // alias/layout uses live in the one symbolic bank.
        internal readonly struct SymbolSet<T>(Points owner, SymbolKind kind, bool layout = false) : IReadOnlyCollection<T> where T : ISymbolic
        {
            public int Count
            {
                get
                {
                    if (owner.symbolic is null) return 0;
                    int count = 0;
                    foreach (Symbol edge in owner.symbolic) if (edge.Kind == kind && edge.Layout == layout) count++;
                    return count;
                }
            }
            internal bool Add(T value) => (owner.symbolic ??= []).Add(new(kind, value, layout));
            internal bool Contains(T value) => owner.symbolic?.Contains(new(kind, value, layout)) == true;
            internal void UnionWith(IEnumerable<T> values) { foreach (T value in values.ToArray()) Add(value); }
            public IEnumerator<T> GetEnumerator() => Count == 0 ? Enumerable.Empty<T>().GetEnumerator() : Entries(owner.symbolic, kind, layout).GetEnumerator();
            private static IEnumerable<T> Entries(HashSet<Symbol>? edges, SymbolKind kind, bool layout)
            {
                if (edges is null) yield break;
                foreach (Symbol edge in edges)
                    if (edge.Kind == kind && edge.Layout == layout) yield return (T)edge.Value;
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private HashSet<Target>? targets;
        internal HashSet<Target> Targets => targets ??= [];
        private HashSet<ForeignTarget>? foreign;
        internal HashSet<ForeignTarget> Foreign => foreign ??= [];
        internal SymbolSet<Formal> Formals => new(this, SymbolKind.Formal);
        internal SymbolSet<Application> Applications => new(this, SymbolKind.Application);
        internal SymbolSet<Adaptation> Adaptations => new(this, SymbolKind.Adaptation);
        internal SymbolSet<Projection> Projections => new(this, SymbolKind.Projection);
        internal SymbolSet<ReadExpression> Reads => new(this, SymbolKind.Read);
        internal SymbolSet<ReadExpression> LayoutReads => new(this, SymbolKind.Read, layout: true);
        internal SymbolSet<Points> ElementSources => new(this, SymbolKind.Element);
        internal SymbolSet<Points> RangeSources => new(this, SymbolKind.Range);
        internal SymbolSet<Application> LayoutApplications => new(this, SymbolKind.Application, layout: true);
        internal SymbolSet<Points> LayoutElements => new(this, SymbolKind.Element, layout: true);
        private HashSet<NativeCallableOrigin>? callables;
        internal HashSet<NativeCallableOrigin> Callables => callables ??= [];
        private HashSet<Points>? elements;
        internal HashSet<Points> Elements => elements ??= [];
        private HashSet<Points>? retained;
        internal HashSet<Points> Retained => retained ??= [];
        private HashSet<int>? layouts;
        internal HashSet<int> Layouts => layouts ??= [];
        internal SymbolSet<Formal> LayoutFormals => new(this, SymbolKind.Formal, layout: true);
        internal bool LayoutUnknown { get; private set; }
        internal bool Top { get; private set; }
        internal bool ElementsUnknown { get; private set; }
        private bool HasUnresolvedAddress => symbolic?.Any(edge => !edge.Layout && edge.Kind != SymbolKind.Range) == true;
        internal bool Unknown => Top || HasUnresolvedAddress || foreign?.Count > 0;
        internal bool MayAliasCurrentFrame => MayAliasFrame([]);
        private bool MayAliasFrame(HashSet<Points> active)
        {
            if (!active.Add(this) || Top || targets?.Any(target => target.Allocation < 0) == true) return true;
            if (symbolic is not null)
                foreach (Symbol edge in symbolic)
                {
                    if (edge.Layout || edge.Kind == SymbolKind.Range) continue;
                    if (edge.Value is not Projection projection || projection.Source.MayAliasFrame(active)) return true;
                }
            active.Remove(this);
            return false;
        }
        internal Formal? PureFormal
        {
            get
            {
                if (Top || LayoutUnknown || ElementsUnknown || targets?.Count > 0 || foreign?.Count > 0
                    || callables?.Count > 0 || elements?.Count > 0 || retained?.Count > 0 || layouts?.Count > 0 || symbolic is null) return null;
                Formal? value = null;
                foreach (Symbol edge in symbolic)
                {
                    if (edge.Kind != SymbolKind.Formal) return null;
                    if (!edge.Layout) { if (value is not null) return null; value = (Formal)edge.Value; }
                }
                return value is { } formal && symbolic.All(edge => (Formal)edge.Value == formal) ? formal : null;
            }
        }
        internal Points Canonical { get; set; }
        internal Points() { Canonical = this; }
        internal bool MakeUnknown()
        {
            bool changed = !Top || !LayoutUnknown || !ElementsUnknown;
            Top = LayoutUnknown = ElementsUnknown = true;
            return changed;
        }
        internal bool UnknownAliases() { bool changed = !Top; Top = true; return changed; }
        internal bool UnknownLayout() { bool changed = !LayoutUnknown; LayoutUnknown = true; return changed; }
        internal bool UnknownElements() { bool changed = !ElementsUnknown; ElementsUnknown = true; return changed; }
        internal bool Merge(Points other, bool layout = true, bool local = true)
        {
            bool changed = other.Top && !Top || other.ElementsUnknown && !ElementsUnknown;
            Top |= other.Top; ElementsUnknown |= other.ElementsUnknown;
            if (local) changed |= Union(ref targets, other.targets) | Union(ref foreign, other.foreign);
            changed |= Union(ref callables, other.callables) | Union(ref elements, other.elements) | Union(ref retained, other.retained);
            if (other.symbolic is { } edges)
                foreach (Symbol edge in edges)
                    if (!edge.Layout || layout) changed |= (symbolic ??= []).Add(edge);
            if (layout)
            {
                changed |= other.LayoutUnknown && !LayoutUnknown;
                LayoutUnknown |= other.LayoutUnknown;
                changed |= Union(ref layouts, other.layouts);
            }
            return changed;
        }
        private static bool Union<T>(ref HashSet<T>? destination, HashSet<T>? source)
        {
            if (source is not { Count: > 0 }) return false;
            destination ??= [];
            int count = destination.Count;
            destination.UnionWith(source);
            return count != destination.Count;
        }
    }

    private readonly NativeProgram program;
    private readonly NativeFunctionCode function;
    private readonly ProgramFacts owner;
    private readonly int functionIndex;
    private readonly Points returns = new();
    private readonly Points writes = new(), effects = new(), externalWrites = new();
    private readonly Points[] values;
    private readonly Points[] bindings;
    private readonly Points[] cellWrites;
    private readonly Points?[] nodes;
    private Points Node(int id) => nodes[id] ??= new();
    private NativeValueFlow? valueFlow;
    private NativeValueFlow Flow => valueFlow ??= new(function);
    private readonly Dictionary<(int Pc, int Slot, bool Location), Points> inputs = [];
    private readonly Dictionary<int, Points>?[] memory;
    private readonly List<NativeInstruction>?[] definitions;

    internal IEnumerable<Points> Contents(Target target) => target.Allocation < 0
        ? [values[target.Slot], bindings[target.Slot]]
        : memory[target.Allocation]?.Values ?? Enumerable.Empty<Points>();

    internal NativeAddressAnalysis(NativeProgram program, NativeFunctionCode function) : this(program, function, new(program)) { }

    private NativeAddressAnalysis(NativeProgram program, NativeFunctionCode function, ProgramFacts facts, int? index = null)
    {
        this.program = program; this.function = function;
        owner = facts;
        functionIndex = index ?? Enumerable.Range(0, program.Functions.Count).First(candidate => ReferenceEquals(program.Functions[candidate], function)
            || program.Functions[candidate] == function);
        owner.Register(functionIndex, this);
        definitions = new List<NativeInstruction>?[function.Slots.Count];
        memory = new Dictionary<int, Points>?[function.Instructions.Count];
        foreach (NativeInstruction instruction in function.Instructions)
            if (instruction.Result >= 0) (definitions[instruction.Result] ??= []).Add(instruction);
        values = Enumerable.Range(0, function.Slots.Count).Select(_ => new Points()).ToArray();
        bindings = Enumerable.Range(0, function.Slots.Count).Select(_ => new Points()).ToArray();
        cellWrites = Enumerable.Range(0, function.Slots.Count).Select(_ => new Points()).ToArray();
        nodes = new Points?[2 * function.Slots.Count + function.Instructions.Count];
        for (int slot = 0; slot < values.Length; slot++)
            if (function.Slots[slot].Role == NativeSlotRole.Capture
                && (slot >= function.Signature.Captures.Count || function.Signature.Captures[slot].Mode == NativeCaptureMode.Location))
                Node(values.Length + slot).Formals.Add(new(FormalKind.CaptureAddress, slot));
            else Node(values.Length + slot).Targets.Add(new(-1, slot));
        for (int slot = 0; slot < values.Length; slot++)
        {
            if (!AddressType(slot) || function.Slots[slot].Role is not (NativeSlotRole.Parameter or NativeSlotRole.Capture)) continue;
            Node(slot).Formals.Add(function.Slots[slot].Role == NativeSlotRole.Parameter
                ? new(FormalKind.Argument, slot - function.CaptureCount)
                : new(function.Signature.Captures[slot].Mode == NativeCaptureMode.Location ? FormalKind.CaptureContents : FormalKind.CaptureValue, slot));
            Node(slot).LayoutFormals.UnionWith(Node(slot).Formals);
            values[slot].Merge(Node(slot));
        }
    }

    private bool frozen;
    private bool inputChanged;
    private readonly Points escaped = new();
    private readonly HashSet<Target> clobbered = [];
    private bool unknownWrite;
    private void Solve() => owner.Solve();
    private bool Step()
    {
        inputChanged = false;
        bool changed = false;

        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (NativeInstructionFacts.EscapingUse(instruction))
                foreach (int source in NativeInstructionFacts.ValueReads(instruction))
                    if (AddressType(source)) changed |= escaped.Merge(Input(pc, source));
            changed |= Observe(pc, instruction);
            int defined = instruction.Result >= 0 ? instruction.Result : instruction.Op == NativeOp.BindLocation ? instruction.A : -1;
            if (defined >= 0 && AddressType(defined))
            {
                changed |= values[defined].Merge(Node(Flow.Definition(pc)));
                changed |= Write(Input(pc, defined, location: true), Node(Flow.Definition(pc)), function.Slots[defined].Type, defined);
            }
            if (instruction.Op == NativeOp.Return) changed |= returns.Merge(Input(pc, instruction.A));
        }
        for (int slot = 0; slot < values.Length; slot++) changed |= values[slot].Merge(cellWrites[slot]);
        foreach (int slot in Enumerable.Range(0, function.Slots.Count).Where(slot => function.Slots[slot].Escapes))
            changed |= escaped.Merge(values[slot]);
        if (program.PreserveLocals && functionIndex == program.EntryPoint)
            foreach (int slot in program.SessionRoots ?? Enumerable.Range(0, function.Slots.Count)
                .Where(slot => function.Slots[slot].Role != NativeSlotRole.Temporary).ToArray()) changed |= escaped.Merge(values[slot]);
        changed |= writes.Merge(effects);
        changed |= externalWrites.Merge(writes, layout: false, local: false);
        foreach (ForeignTarget target in writes.Foreign.Where(target => target.Region.Allocation <= -2)) changed |= externalWrites.Foreign.Add(target);
        changed |= ClobberEscaped();
        return changed | inputChanged;
    }

    internal Points this[int register] { get { lock (owner.Gate) { Solve(); return values[register]; } } }
    internal NativeControlFlow ControlFlow { get { lock (owner.Gate) return Flow.ControlFlow; } }
    internal Points Binding(int slot) { lock (owner.Gate) { Solve(); return bindings[slot]; } }
    internal Points At(int pc, int slot) { lock (owner.Gate) { Solve(); return Input(pc, slot); } }

    private Points Input(int pc, int slot, bool location = false)
    {
        if (!inputs.TryGetValue((pc, slot, location), out Points? result)) inputs.Add((pc, slot, location), result = new());
        foreach (int id in location ? Flow.Locations(pc, slot) : Flow.Values(pc, slot)) inputChanged |= result.Merge(Node(id));
        if (!location)
        {
            inputChanged |= result.Merge(cellWrites[slot]);
            foreach (int id in Flow.Locations(pc, slot))
                if (id != values.Length + slot) inputChanged |= Read(result, Node(id), function.Slots[slot].Type);
        }
        return result;
    }

    internal IEnumerable<Target> MemoryTargets(NativeInstruction instruction)
    {
        foreach (Target target in this[instruction.A].Targets) yield return Normalize(target, instruction.Type);
    }

    private bool AddressType(int slot)
    {
        NativeCodeType type = program.Types[function.Slots[slot].Type];
        return type.Pointee >= 0 || (type.Kind & (TypeKind.Scope | TypeKind.Function | TypeKind.Range)) != 0;
    }

    private bool Observe(int pc, NativeInstruction instruction)
    {
        int result = instruction.Result;
        if (instruction.Op == NativeOp.Store)
        {
            bool changed = writes.Merge(Input(pc, instruction.A));
            if (AddressType(instruction.B) && (Input(pc, instruction.A).Top || Input(pc, instruction.A).Formals.Count != 0))
            {
                changed |= !unknownWrite;
                unknownWrite = true;
                changed |= escaped.Merge(Input(pc, instruction.B));
            }
            return Store(pc, instruction) | changed;
        }
        if (instruction.Op == NativeOp.BindLocation)
        {
            bool changed = bindings[instruction.A].Merge(Input(pc, instruction.B));
            if (AddressType(instruction.A))
            {
                int type = function.Slots[instruction.A].Type;
                changed |= Read(Node(Flow.Definition(pc)), Input(pc, instruction.B), type);
            }
            return changed;
        }
        if (instruction.Op is NativeOp.CopyMemory or NativeOp.ClearMemory)
        {
            Points address = Input(pc, instruction.A);
            return writes.Merge(address) | effects.Retained.Add(address);
        }
        if (instruction.Op == NativeOp.OpenIterator) return Node(Flow.Definition(pc)).Merge(Input(pc, instruction.A));
        if (result < 0) return false;
        Points destination = Node(Flow.Definition(pc));
        if (instruction.Op == NativeOp.Call) return Invoke(pc, Input(pc, instruction.A), Arguments(pc, instruction), destination, instruction);
        if (instruction.Op == NativeOp.CallNative) return Native(pc, instruction.Symbol!, Arguments(pc, instruction), destination);
        if (!AddressType(result)) return false;
        return instruction.Op switch
        {
            NativeOp.Allocate => destination.Targets.Add(new(pc, -1)) | destination.Layouts.Add(instruction.A),
            NativeOp.FrameAddress => destination.Merge(Input(pc, instruction.A, location: true)),
            NativeOp.Copy or NativeOp.Move or NativeOp.Convert => destination.Merge(Input(pc, instruction.A)),
            NativeOp.Narrow => Narrow(pc, instruction, destination),
            NativeOp.MakeFunction => Function(pc, instruction, destination),
            NativeOp.AdaptFunction => Adapt(pc, instruction, destination),
            NativeOp.Call => Invoke(pc, Input(pc, instruction.A), Arguments(pc, instruction), destination, instruction),
            NativeOp.CallNative => Native(pc, instruction.Symbol!, Arguments(pc, instruction), destination),
            NativeOp.MakeRange or NativeOp.MakeLazyRange => Range(pc, instruction, destination),
            NativeOp.DeferRange => Deferred(pc, instruction, destination),
            NativeOp.NextIterator => Next(pc, instruction, destination),
            NativeOp.EnvironmentAddress => Environment(pc, instruction, destination),
            NativeOp.StaticAddress => Static(pc, instruction, destination),
            NativeOp.IndexAddress => ProjectValue(instruction, Input(pc, instruction.A), destination),
            NativeOp.AddressOffset or NativeOp.ProjectAddress => Field(pc, instruction, destination),
            NativeOp.Load => Read(destination, Input(pc, instruction.A), instruction.Type),
            NativeOp.RangeFromMemory => Region(pc, instruction, destination),
            NativeOp.Constant => false,
            _ => destination.MakeUnknown()
        };
    }

    private bool Field(int pc, NativeInstruction instruction, Points destination) => ProjectValue(instruction, Input(pc, instruction.A), destination);
    private bool ProjectValue(NativeInstruction instruction, Points source, Points destination)
    {
        if (instruction.Op == NativeOp.IndexAddress)
            return destination.Projections.Add(ProjectionAt(source, instruction)) | destination.Retained.Add(source) | destination.Layouts.Add(instruction.C);
        bool changed = source.Top && destination.MakeUnknown();
        if (source.Formals.Count != 0 || source.Applications.Count != 0 || source.Reads.Count != 0 || source.Projections.Count != 0 || source.ElementSources.Count != 0)
        {
            changed |= destination.Projections.Add(ProjectionAt(source, instruction));
            changed |= destination.Retained.Add(source);
        }
        foreach (Target target in source.Targets.ToArray())
        {
            int field = Field(target, instruction);
            if (field >= 0) changed |= destination.Targets.Add(target with { Field = field });
            else changed |= destination.MakeUnknown();
        }
        foreach (ForeignTarget target in source.Foreign)
        {
            int field = target.Region.Layout < 0 || target.Field >= 0 ? -1 : instruction.Op == NativeOp.AddressOffset
                ? instruction.B == target.Region.Layout ? instruction.C : -1
                : program.Projections[instruction.B].Cases.FirstOrDefault(item => item.Layout == target.Region.Layout, new(-1, -1)).Field;
            if (field >= 0) changed |= destination.Foreign.Add(target with { Field = field });
            else changed |= destination.Foreign.Add(target with { Field = -2 });
        }
        changed |= destination.UnknownLayout();
        return changed;
    }

    private int Field(Target target, NativeInstruction instruction)
    {
        if (target.Allocation < 0 || target.Field >= 0) return -1;
        int layout = function.Instructions[target.Allocation].A;
        if (instruction.Op == NativeOp.AddressOffset) return instruction.B == layout ? instruction.C : -1;
        foreach (NativeProjectionCase alternative in program.Projections[instruction.B].Cases)
            if (alternative.Layout == layout) return alternative.Field;
        return -1;
    }

    private bool Store(int pc, NativeInstruction instruction) => AddressType(instruction.B)
        && Write(Input(pc, instruction.A), Input(pc, instruction.B), instruction.Type);

    private bool Write(Points address, Points source, int type, int ownSlot = -1)
    {
        bool changed = false;
        foreach (Target original in address.Targets.ToArray())
        {
            Target target = Normalize(original, type);
            if (target.Allocation < 0)
            {
                if (target.Slot == ownSlot) continue;
                changed |= cellWrites[target.Slot].Merge(source);
                continue;
            }
            Dictionary<int, Points> fields = memory[target.Allocation] ??= [];
            if (!fields.TryGetValue(target.Field, out Points? contents)) fields.Add(target.Field, contents = new());
            changed |= contents.Merge(source);
            if (Clobbered(target)) changed |= contents.MakeUnknown();
        }
        foreach (Formal formal in address.Formals) changed |= mutated.Add(formal);
        foreach (Projection projection in address.Projections)
            if (projection.Instruction.Op == NativeOp.IndexAddress) changed |= effects.Retained.Add(projection.Source);
        foreach (ForeignTarget target in address.Foreign)
        {
            if (!target.Region.Fields.TryGetValue(target.Field, out Points? contents)) target.Region.Fields.Add(target.Field, contents = new());
            changed |= contents.Merge(source);
            if (target.Region.Clobbered) changed |= contents.MakeUnknown();
        }
        return changed;
    }

    private bool Read(Points destination, Points address, int type)
    {
        bool changed = address.Top && destination.MakeUnknown();
        foreach (Formal formal in address.Formals)
            if (formal.Kind == FormalKind.CaptureAddress && !mutated.Contains(formal))
            {
                Formal contents = formal with { Kind = FormalKind.CaptureContents };
                changed |= destination.Formals.Add(contents);
                changed |= destination.LayoutFormals.Add(contents);
            }
            else
            {
                ReadExpression read = ReadAt(address, type);
                changed |= destination.Reads.Add(read) | destination.LayoutReads.Add(read);
                changed |= destination.Retained.Add(address);
            }
        if (address.Applications.Count != 0 || address.Projections.Count != 0 || address.Reads.Count != 0 || address.ElementSources.Count != 0)
        {
            ReadExpression read = ReadAt(address, type);
            changed |= destination.Reads.Add(read) | destination.LayoutReads.Add(read);
            changed |= destination.Retained.Add(address);
        }
        foreach (ForeignTarget target in address.Foreign)
            if (target.Region.Fields.TryGetValue(target.Field, out Points? contents)) changed |= destination.Merge(contents);
            else changed |= destination.MakeUnknown();
        foreach (Target original in address.Targets.ToArray())
        {
            Target target = Normalize(original, type);
            // A register and its FrameAddress name the same cell. Definitions through either
            // surface must feed alias loads, including inlined parameters and bound captures.
            if (target.Allocation < 0) changed |= destination.Merge(values[target.Slot]);
            else if (memory[target.Allocation] is { } fields && fields.TryGetValue(target.Field, out Points? contents))
                changed |= destination.Merge(contents);
        }
        return changed;
    }

    private Target Normalize(Target target, int type)
    {
        if (target.Allocation < 0 || target.Field >= 0) return target;
        NativeCodeLayout layout = program.Layouts[function.Instructions[target.Allocation].A];
        return layout.Fields.Count != 0 && layout.Fields[0].Type == type ? target with { Field = 0 } : target;
    }

    private bool Region(int pc, NativeInstruction instruction, Points destination)
    {
        Points address = Input(pc, instruction.A), item = ElementValue(pc, -7), selected = ElementValue(pc, -8);
        bool changed = false;
        foreach (Target target in address.Targets) changed |= selected.Targets.Add(target with { Field = (int)instruction.Immediate });
        foreach (ForeignTarget target in address.Foreign) changed |= selected.Foreign.Add(target with { Field = (int)instruction.Immediate });
        if (address.Top || address.Formals.Count != 0) changed |= selected.MakeUnknown();
        changed |= Read(item, selected, program.Layouts[instruction.C].Fields[(int)instruction.Immediate].Type);
        changed |= destination.Elements.Add(item);
        if (instruction.Symbol == "move") changed |= destination.Retained.Add(address);
        return changed;
    }

    private bool Clobbered(Target target) => clobbered.Any(written => written.Allocation == target.Allocation
        && (written.Field < 0 || target.Field < 0 || Compatible(written, target)));

    private bool Compatible(Target left, Target right)
    {
        NativeCodeLayout layout = program.Layouts[function.Instructions[left.Allocation].A];
        int first = layout.Fields[left.Field].Type, second = layout.Fields[right.Field].Type;
        return NativeTypeRelations.StorageAccepts(program.Types, first, second) || NativeTypeRelations.StorageAccepts(program.Types, second, first);
    }

    private bool ClobberEscaped()
    {
        bool changed = false;
        HashSet<Points> seen = [];
        Queue<Points> pending = new([effects]);
        if (effects.Top || unknownWrite) pending.Enqueue(escaped);
        while (pending.TryDequeue(out Points? points))
        {
            if (!seen.Add(points)) continue;
            foreach (Points retained in points.Retained.Concat(points.Elements)) pending.Enqueue(retained);
            foreach (Projection projection in points.Projections) pending.Enqueue(projection.Source);
            foreach (ReadExpression read in points.Reads) pending.Enqueue(read.Address);
            foreach (Application application in points.Applications)
            {
                foreach (Points argument in application.Arguments) pending.Enqueue(argument);
                pending.Enqueue(application.Callee);
            }
            foreach (NativeCallableOrigin callable in points.Callables)
                foreach (Points capture in callable.Captures) pending.Enqueue(capture);
            foreach (Target target in points.Targets)
            {
                if (target.Allocation < 0)
                {
                    pending.Enqueue(values[target.Slot]); pending.Enqueue(bindings[target.Slot]);
                    if (AddressType(target.Slot)) changed |= cellWrites[target.Slot].MakeUnknown();
                    continue;
                }
                changed |= clobbered.Add(target);
                if (memory[target.Allocation] is not { } fields) continue;
                foreach (var field in fields)
                {
                    if (target.Field >= 0 && field.Key >= 0 && !Compatible(target, target with { Field = field.Key })) continue;
                    pending.Enqueue(field.Value);
                    changed |= field.Value.MakeUnknown();
                }
            }
            foreach (ForeignTarget target in points.Foreign)
            {
                if (!target.Region.Clobbered) { target.Region.Clobbered = true; changed = true; }
                foreach (Points contents in target.Region.Fields.Values)
                { pending.Enqueue(contents); changed |= contents.MakeUnknown(); }
            }
        }
        return changed;
    }

    private readonly Dictionary<int, HashSet<int>> physical = [];

    internal IReadOnlySet<int> PhysicalTypes(int register)
    {
        if (physical.TryGetValue(register, out HashSet<int>? cached)) return cached;
        HashSet<int> types = [];
        foreach (var (current, instruction) in Producers(register))
        {
            NativeCodeType contract = program.Types[function.Slots[current].Type];
            switch (instruction?.Op)
            {
                case NativeOp.FrameAddress: types.Add(function.Slots[instruction.A].Type); break;
                case NativeOp.AddressOffset: types.Add(program.Layouts[instruction.B].Fields[instruction.C].Type); break;
                case NativeOp.ProjectAddress:
                    HashSet<int>? admitted = Layouts(instruction.A);
                    foreach (NativeProjectionCase item in program.Projections[instruction.B].Cases)
                        if (admitted is null || admitted.Contains(item.Layout))
                            types.Add(program.Layouts[item.Layout].Fields[item.Field].Type);
                    break;
                default: if (contract.Pointee >= 0) types.Add(contract.Pointee); break;
            }
        }
        if (types.Count == 0 && program.Types[function.Slots[register].Type].Pointee is >= 0 and var fallback)
            types.Add(fallback);
        physical.Add(register, types);
        return types;
    }

    internal HashSet<int>? Layouts(int register)
    {
        HashSet<int> result = [];
        foreach (var (current, instruction) in Producers(register))
        {
            switch (instruction?.Op)
            {
                case NativeOp.Allocate: result.Add(instruction.A); break;
                case NativeOp.IndexAddress: result.Add(instruction.C); break;
                case NativeOp.EnvironmentAddress: result.Add(instruction.B); break;
                case NativeOp.StaticAddress: result.Add(program.Statics[instruction.A].Layout); break;
                default: if (!ContractLayouts(current, result)) return null; break;
            }
        }
        return result.Count == 0 ? null : result;
    }

    // Declared legacy cases select alternatives; only admitted values justify an unchecked offset.
    internal IReadOnlySet<int>? ProvenLayoutsAt(int pc, int slot)
    {
        Points actual = At(pc, slot);
        if (actual.LayoutUnknown || actual.LayoutFormals.Count != 0 || actual.LayoutApplications.Count != 0 || actual.LayoutElements.Count != 0 || actual.LayoutReads.Count != 0 || actual.Layouts.Count == 0) return null;
        if (!actual.Top && actual.Formals.Count == 0 && actual.Applications.Count == 0 && actual.Reads.Count == 0 && actual.Projections.Count == 0 && actual.ElementSources.Count == 0)
        {
            HashSet<int> producers = actual.Targets.Where(target => target.Allocation >= 0 && target.Field < 0)
                .Select(target => function.Instructions[target.Allocation].A)
                .Concat(actual.Foreign.Where(target => target.Field < 0 && target.Region.Layout >= 0).Select(target => target.Region.Layout)).ToHashSet();
            if (producers.Count != 0 && producers.All(actual.Layouts.Contains)) return producers;
        }
        return actual.Layouts;
    }

    private IEnumerable<(int Register, NativeInstruction? Instruction)> Producers(int register)
    {
        HashSet<int> seen = [];
        Stack<int> pending = new([register]);
        while (pending.TryPop(out int current))
        {
            if (!seen.Add(current)) continue;
            if (definitions[current] is not { Count: > 0 } producers) { yield return (current, null); continue; }
            foreach (NativeInstruction instruction in producers)
                if (instruction.Op is NativeOp.Copy or NativeOp.Move) pending.Push(instruction.A);
                else yield return (current, instruction);
        }
    }

    private bool ContractLayouts(int register, HashSet<int> layouts)
    {
        NativeCodeType type = program.Types[function.Slots[register].Type];
        if (type.Kind != TypeKind.Scope || type.Layouts is not { Count: > 0 } alternatives) return false;
        foreach (int layout in alternatives) layouts.Add(layout);
        return true;
    }
}
