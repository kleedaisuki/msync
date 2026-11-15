namespace Msync.Core.Native;

internal sealed partial class NativeAddressAnalysis
{
    internal readonly record struct Target(int Allocation, int Slot, int Field = -1);
    internal interface ISymbolic { }

    internal struct CompactSet<T> : IReadOnlyCollection<T> where T : notnull
    {
        private const int HashThreshold = 8;
        private T[]? items;
        private int count;
        private HashSet<T>? lookup;

        public readonly int Count => count;
        public readonly T this[int index] => items![index];

        public bool Add(T value)
        {
            if (lookup is not null)
            {
                if (!lookup.Add(value)) return false;
            }
            else
            {
                EqualityComparer<T> comparer = EqualityComparer<T>.Default;
                for (int i = 0; i < count; i++)
                    if (comparer.Equals(items![i], value)) return false;
                if (count == HashThreshold)
                {
                    lookup = new HashSet<T>(HashThreshold * 2, comparer);
                    for (int i = 0; i < count; i++) lookup.Add(items![i]);
                    lookup.Add(value);
                }
            }
            if (items is null) items = new T[2];
            else if (count == items.Length) Array.Resize(ref items, items.Length * 2);
            items[count++] = value;
            return true;
        }

        public readonly bool Contains(T value)
        {
            if (count == 0) return false;
            if (lookup is not null) return lookup.Contains(value);
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < count; i++)
                if (comparer.Equals(items![i], value)) return true;
            return false;
        }

        public void UnionWith(in CompactSet<T> other)
        {
            for (int i = 0; i < other.count; i++) Add(other.items![i]);
        }

        public void UnionWith(IEnumerable<T> values)
        {
            foreach (T value in values) Add(value);
        }

        public bool Merge(in CompactSet<T> other)
        {
            if (other.count == 0) return false;
            bool changed = false;
            for (int i = 0; i < other.count; i++) changed |= Add(other.items![i]);
            return changed;
        }

        public readonly bool SetEquals(IEnumerable<T> other)
        {
            if (count == 0) return !other.Any();
            HashSet<T> expected = new(other);
            if (expected.Count != count) return false;
            foreach (T item in expected)
                if (!Contains(item)) return false;
            return true;
        }

        public readonly Enumerator GetEnumerator() => new(items, count);
        readonly IEnumerator<T> IEnumerable<T>.GetEnumerator() => count == 0 ? Enumerable.Empty<T>().GetEnumerator() : GetEnumerator();
        readonly System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

        public struct Enumerator(T[]? items, int count) : IEnumerator<T>
        {
            private int index = -1;
            public readonly T Current => items![index];
            readonly object System.Collections.IEnumerator.Current => Current!;
            public bool MoveNext() => ++index < count;
            public void Reset() => index = -1;
            public readonly void Dispose() { }
        }
    }

    internal sealed class Points : ISymbolic
    {
        private sealed class SymbolicBank
        {
            internal CompactSet<Formal> Formals;
            internal CompactSet<Formal> LayoutFormals;
            internal CompactSet<Application> Applications;
            internal CompactSet<Application> LayoutApplications;
            internal CompactSet<Adaptation> Adaptations;
            internal CompactSet<Projection> Projections;
            internal CompactSet<ReadExpression> Reads;
            internal CompactSet<ReadExpression> LayoutReads;
            internal CompactSet<Points> ElementSources;
            internal CompactSet<Points> LayoutElements;
            internal CompactSet<Points> RangeSources;
        }

        private static readonly SymbolicBank emptyBank = new();
        private SymbolicBank? symbolic;

        private CompactSet<Target> targets;
        internal ref CompactSet<Target> Targets => ref targets;
        private CompactSet<ForeignTarget> foreign;
        internal ref CompactSet<ForeignTarget> Foreign => ref foreign;
        private CompactSet<NativeCallableOrigin> callables;
        internal ref CompactSet<NativeCallableOrigin> Callables => ref callables;
        private CompactSet<Points> elements;
        internal ref CompactSet<Points> Elements => ref elements;
        private CompactSet<Points> retained;
        internal ref CompactSet<Points> Retained => ref retained;
        private CompactSet<int> layouts;
        internal ref CompactSet<int> Layouts => ref layouts;

        internal ref readonly CompactSet<Formal> Formals => ref (symbolic ?? emptyBank).Formals;
        internal ref readonly CompactSet<Formal> LayoutFormals => ref (symbolic ?? emptyBank).LayoutFormals;
        internal ref readonly CompactSet<Application> Applications => ref (symbolic ?? emptyBank).Applications;
        internal ref readonly CompactSet<Application> LayoutApplications => ref (symbolic ?? emptyBank).LayoutApplications;
        internal ref readonly CompactSet<Adaptation> Adaptations => ref (symbolic ?? emptyBank).Adaptations;
        internal ref readonly CompactSet<Projection> Projections => ref (symbolic ?? emptyBank).Projections;
        internal ref readonly CompactSet<ReadExpression> Reads => ref (symbolic ?? emptyBank).Reads;
        internal ref readonly CompactSet<ReadExpression> LayoutReads => ref (symbolic ?? emptyBank).LayoutReads;
        internal ref readonly CompactSet<Points> ElementSources => ref (symbolic ?? emptyBank).ElementSources;
        internal ref readonly CompactSet<Points> LayoutElements => ref (symbolic ?? emptyBank).LayoutElements;
        internal ref readonly CompactSet<Points> RangeSources => ref (symbolic ?? emptyBank).RangeSources;

        internal ref CompactSet<Formal> MutFormals => ref (symbolic ??= new()).Formals;
        internal ref CompactSet<Formal> MutLayoutFormals => ref (symbolic ??= new()).LayoutFormals;
        internal ref CompactSet<Application> MutApplications => ref (symbolic ??= new()).Applications;
        internal ref CompactSet<Application> MutLayoutApplications => ref (symbolic ??= new()).LayoutApplications;
        internal ref CompactSet<Adaptation> MutAdaptations => ref (symbolic ??= new()).Adaptations;
        internal ref CompactSet<Projection> MutProjections => ref (symbolic ??= new()).Projections;
        internal ref CompactSet<ReadExpression> MutReads => ref (symbolic ??= new()).Reads;
        internal ref CompactSet<ReadExpression> MutLayoutReads => ref (symbolic ??= new()).LayoutReads;
        internal ref CompactSet<Points> MutElementSources => ref (symbolic ??= new()).ElementSources;
        internal ref CompactSet<Points> MutLayoutElements => ref (symbolic ??= new()).LayoutElements;
        internal ref CompactSet<Points> MutRangeSources => ref (symbolic ??= new()).RangeSources;

        internal bool LayoutUnknown { get; private set; }
        internal bool Top { get; private set; }
        internal bool ElementsUnknown { get; private set; }
        private bool HasUnresolvedAddress => symbolic is { } b && (
            b.Formals.Count > 0 || b.Applications.Count > 0 || b.Adaptations.Count > 0
            || b.Projections.Count > 0 || b.Reads.Count > 0 || b.ElementSources.Count > 0);
        internal bool Unknown => Top || HasUnresolvedAddress || foreign.Count > 0;
        internal bool MayAliasCurrentFrame => MayAliasFrame([], allowFormal: true);
        internal bool MayAliasCurrentFrameAllocation => MayAliasFrame([], allowFormal: false);
        internal bool MayAliasFrameSlot(HashSet<int>? bound, NativeFunctionCode function, bool frameEscaped)
            => MayAliasFrame([], allowFormal: frameEscaped, allowExternal: frameEscaped, bound, function);
        private bool MayAliasFrame(HashSet<Points> active, bool allowFormal,
            bool allowExternal = true, HashSet<int>? bound = null, NativeFunctionCode? function = null)
        {
            if (!active.Add(this)) return false;
            if (Top) return true;
            for (int i = 0; i < targets.Count; i++)
            {
                int slot = targets[i].Slot;
                if (targets[i].Allocation < 0 && (bound is null || (!bound.Contains(slot) && function!.Slots[slot].Role != NativeSlotRole.Capture)))
                    return true;
            }
            if (bound is not null && allowExternal && foreign.Count > 0) return true;
            if (symbolic is { } b)
            {
                if (allowExternal && (b.Applications.Count > 0 || b.Adaptations.Count > 0 || b.Reads.Count > 0
                    || b.ElementSources.Count > 0 || (allowFormal && b.Formals.Count > 0)))
                    return true;
                for (int i = 0; i < b.Projections.Count; i++)
                    if (b.Projections[i].Source.MayAliasFrame(active, allowFormal, allowExternal, bound, function)) return true;
            }
            active.Remove(this);
            return false;
        }
        internal bool CollectFrameTargets(HashSet<int> slots, bool allowFormal) =>
            CollectFrameTargets([], slots, allowFormal);
        private bool CollectFrameTargets(HashSet<Points> active, HashSet<int> slots, bool allowFormal)
        {
            if (!active.Add(this)) return true;
            if (Top) return false;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i].Allocation < 0) slots.Add(targets[i].Slot);
            if (symbolic is { } b)
            {
                if (b.Applications.Count > 0 || b.Adaptations.Count > 0 || b.Reads.Count > 0
                    || b.ElementSources.Count > 0 || (allowFormal && b.Formals.Count > 0))
                    return false;
                for (int i = 0; i < b.Projections.Count; i++)
                    if (!b.Projections[i].Source.CollectFrameTargets(active, slots, allowFormal)) return false;
            }
            active.Remove(this);
            return true;
        }
        internal Formal? PureFormal
        {
            get
            {
                if (Top || LayoutUnknown || ElementsUnknown || targets.Count > 0 || foreign.Count > 0
                    || callables.Count > 0 || elements.Count > 0 || retained.Count > 0 || layouts.Count > 0
                    || symbolic is not { } b) return null;
                if (b.Formals.Count != 1 || b.Applications.Count > 0 || b.Adaptations.Count > 0
                    || b.Projections.Count > 0 || b.Reads.Count > 0 || b.ElementSources.Count > 0
                    || b.RangeSources.Count > 0 || b.LayoutApplications.Count > 0 || b.LayoutReads.Count > 0
                    || b.LayoutElements.Count > 0) return null;
                Formal formal = b.Formals[0];
                return b.LayoutFormals.Count == 0 || (b.LayoutFormals.Count == 1 && b.LayoutFormals[0] == formal) ? formal : null;
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
        internal long MonotonicStamp()
        {
            long stamp = (Top ? 1L : 0L) | (LayoutUnknown ? 2L : 0L) | (ElementsUnknown ? 4L : 0L);
            stamp += ((long)targets.Count + foreign.Count + callables.Count + elements.Count + retained.Count + layouts.Count) << 3;
            if (symbolic is { } s)
                stamp += ((long)s.Formals.Count + s.LayoutFormals.Count + s.Applications.Count + s.LayoutApplications.Count
                    + s.Adaptations.Count + s.Projections.Count + s.Reads.Count + s.LayoutReads.Count
                    + s.ElementSources.Count + s.LayoutElements.Count + s.RangeSources.Count) << 3;
            return stamp;
        }
        internal bool Merge(Points other, bool layout = true, bool local = true)
        {
            if (ReferenceEquals(this, other)) return false;
            bool changed = other.Top && !Top || other.ElementsUnknown && !ElementsUnknown;
            Top |= other.Top; ElementsUnknown |= other.ElementsUnknown;
            if (local) changed |= targets.Merge(other.targets) | foreign.Merge(other.foreign);
            changed |= callables.Merge(other.callables) | elements.Merge(other.elements) | retained.Merge(other.retained);
            if (other.symbolic is { } src)
            {
                SymbolicBank dst = symbolic ??= new();
                changed |= dst.Formals.Merge(src.Formals)
                    | dst.Applications.Merge(src.Applications)
                    | dst.Adaptations.Merge(src.Adaptations)
                    | dst.Projections.Merge(src.Projections)
                    | dst.Reads.Merge(src.Reads)
                    | dst.ElementSources.Merge(src.ElementSources)
                    | dst.RangeSources.Merge(src.RangeSources);
                if (layout)
                    changed |= dst.LayoutFormals.Merge(src.LayoutFormals)
                        | dst.LayoutApplications.Merge(src.LayoutApplications)
                        | dst.LayoutReads.Merge(src.LayoutReads)
                        | dst.LayoutElements.Merge(src.LayoutElements);
            }
            if (layout)
            {
                changed |= other.LayoutUnknown && !LayoutUnknown;
                LayoutUnknown |= other.LayoutUnknown;
                changed |= layouts.Merge(other.layouts);
            }
            return changed;
        }
    }

    private NativeProgram program => owner.Program;
    private readonly NativeFunctionCode function;
    private readonly ProgramFacts owner;
    private readonly int functionIndex;
    private readonly Points returns = new();
    private readonly Points writes = new(), effects = new(), externalWrites = new();
    private readonly Points[] values;
    private readonly Points[] bindings;
    private readonly Points[] cellWrites;
    private readonly bool[] addressSlots;
    private readonly bool[] hasCellWrite;
    private readonly Points?[] nodes;
    private readonly int[] escapingSlots;
    private readonly int[] sessionRoots;
    private readonly HashSet<NativeAddressAnalysis> callers = [];
    private bool queued;
    private long stateVersion;
    private long writeEpoch;
    internal long SummaryVersion { get; private set; }
    private Points Node(int id) => nodes[id] ??= new();
    private Points? NodeIfCreated(int id) => nodes[id];
    private NativeValueFlow? valueFlow;
    private NativeValueFlow Flow => valueFlow ??= new(function);
    private readonly Dictionary<(int Pc, int Slot, bool Location), Points> inputs = [];
    private readonly Dictionary<int, Points>?[] memory;
    private readonly List<NativeInstruction>?[] definitions;

    internal IEnumerable<Points> Contents(Target target) => target.Allocation < 0
        ? [values[target.Slot], bindings[target.Slot]]
        : memory[target.Allocation]?.Values ?? Enumerable.Empty<Points>();

    internal NativeAddressAnalysis(NativeProgram program, NativeFunctionCode function) : this(program, function, new(program)) { }
    internal NativeAddressAnalysis(NativeProgram program, int index) : this(program, program.Functions[index], new(program), index) { }

    private NativeAddressAnalysis(NativeProgram program, NativeFunctionCode function, ProgramFacts facts, int? index = null)
    {
        this.function = function;
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
        addressSlots = new bool[function.Slots.Count];
        for (int slot = 0; slot < addressSlots.Length; slot++) addressSlots[slot] = ComputeAddressType(slot);
        hasCellWrite = new bool[function.Slots.Count];
        nodes = new Points?[2 * function.Slots.Count + function.Instructions.Count];
        for (int slot = 0; slot < values.Length; slot++)
            if (function.Slots[slot].Role == NativeSlotRole.Capture
                && (slot >= function.Signature.Captures.Count || function.Signature.Captures[slot].Mode == NativeCaptureMode.Location))
                Node(values.Length + slot).MutFormals.Add(new(FormalKind.CaptureAddress, slot));
            else Node(values.Length + slot).Targets.Add(new(-1, slot));
        for (int slot = 0; slot < values.Length; slot++)
        {
            if (!addressSlots[slot] || function.Slots[slot].Role is not (NativeSlotRole.Parameter or NativeSlotRole.Capture)) continue;
            Node(slot).MutFormals.Add(function.Slots[slot].Role == NativeSlotRole.Parameter
                ? new(FormalKind.Argument, slot - function.CaptureCount)
                : new(function.Signature.Captures[slot].Mode == NativeCaptureMode.Location ? FormalKind.CaptureContents : FormalKind.CaptureValue, slot));
            Node(slot).MutLayoutFormals.UnionWith(Node(slot).Formals);
            values[slot].Merge(Node(slot));
        }
        escapingSlots = Enumerable.Range(0, function.Slots.Count).Where(slot => function.Slots[slot].Escapes).ToArray();
        sessionRoots = program.PreserveLocals && functionIndex == program.EntryPoint
            ? (program.SessionRoots?.ToArray() ?? Enumerable.Range(0, function.Slots.Count)
                .Where(slot => function.Slots[slot].Role != NativeSlotRole.Temporary).ToArray())
            : [];
    }

    private bool frozen;
    private bool inputChanged;
    private readonly Points escaped = new();
    private readonly HashSet<Target> clobbered = [];
    private readonly HashSet<Points> clobberSeen = [];
    private readonly Queue<Points> clobberPending = [];
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
            {
                NativeOperands fields = NativeInstructionFacts.ValueOperands(instruction);
                if ((fields & NativeOperands.A) != 0 && instruction.A >= 0 && AddressType(instruction.A))
                    changed |= escaped.Merge(Input(pc, instruction.A));
                if ((fields & NativeOperands.B) != 0 && instruction.B >= 0 && AddressType(instruction.B))
                    changed |= escaped.Merge(Input(pc, instruction.B));
                if ((fields & NativeOperands.C) != 0 && instruction.C >= 0 && AddressType(instruction.C))
                    changed |= escaped.Merge(Input(pc, instruction.C));
                if (instruction.Inputs is { } insInputs)
                    for (int i = 0; i < insInputs.Count; i++)
                    {
                        int source = insInputs[i];
                        if (source >= 0 && AddressType(source)) changed |= escaped.Merge(Input(pc, source));
                    }
            }
            if (Observe(pc, instruction)) { changed = true; writeEpoch++; }
            int defined = instruction.Result >= 0 ? instruction.Result : instruction.Op == NativeOp.BindLocation ? instruction.A : -1;
            if (defined >= 0 && AddressType(defined))
            {
                Points defNode = Node(Flow.Definition(pc));
                if (values[defined].Merge(defNode)) { changed = true; writeEpoch++; }
                IReadOnlyList<int> locs = Flow.Locations(pc, defined);
                if (locs.Count != 1 || locs[0] != values.Length + defined || !IsPureOwnSlot(NodeIfCreated(locs[0]), defined))
                {
                    if (Write(Input(pc, defined, location: true), defNode, function.Slots[defined].Type, defined))
                    { changed = true; writeEpoch++; }
                }
            }
            if (instruction.Op == NativeOp.Return) changed |= returns.Merge(Input(pc, instruction.A));
        }
        for (int slot = 0; slot < values.Length; slot++)
            if (hasCellWrite[slot] && values[slot].Merge(cellWrites[slot]))
            { changed = true; writeEpoch++; }
        foreach (int slot in escapingSlots) changed |= escaped.Merge(values[slot]);
        foreach (int slot in sessionRoots) changed |= escaped.Merge(values[slot]);
        changed |= writes.Merge(effects);
        changed |= externalWrites.Merge(writes, layout: false, local: false);
        for (int i = 0; i < writes.Foreign.Count; i++)
        {
            ForeignTarget target = writes.Foreign[i];
            if (target.Region.Allocation <= -2) changed |= externalWrites.Foreign.Add(target);
        }
        changed |= ClobberEscaped();
        bool any = changed | inputChanged;
        if (any) stateVersion++;
        return any;
    }

    private bool IsPureOwnSlot(Points? node, int slot) => node is not null
        && !node.Top
        && node.Formals.Count == 0
        && node.Projections.Count == 0
        && node.Foreign.Count == 0
        && node.Targets.Count == 1
        && node.Targets[0].Allocation < 0
        && node.Targets[0].Slot == slot;

    internal Points this[int register] { get { lock (owner.Gate) { Solve(); return values[register]; } } }
    internal NativeControlFlow ControlFlow { get { lock (owner.Gate) return Flow.ControlFlow; } }
    internal Points Binding(int slot) { lock (owner.Gate) { Solve(); return bindings[slot]; } }
    internal Points At(int pc, int slot) { lock (owner.Gate) { Solve(); return Input(pc, slot); } }

    private Points Input(int pc, int slot, bool location = false)
    {
        if (!inputs.TryGetValue((pc, slot, location), out Points? result)) inputs.Add((pc, slot, location), result = new());
        foreach (int id in location ? Flow.Locations(pc, slot) : Flow.Values(pc, slot))
            if (NodeIfCreated(id) is { } node) inputChanged |= result.Merge(node);
        if (!location)
        {
            if (hasCellWrite[slot]) inputChanged |= result.Merge(cellWrites[slot]);
            foreach (int id in Flow.Locations(pc, slot))
                if (id != values.Length + slot && NodeIfCreated(id) is { } node)
                    inputChanged |= Read(result, node, function.Slots[slot].Type);
        }
        return result;
    }

    internal IEnumerable<Target> MemoryTargets(NativeInstruction instruction)
    {
        foreach (Target target in this[instruction.A].Targets) yield return Normalize(target, instruction.Type);
    }

    private bool AddressType(int slot) => addressSlots[slot];
    private bool ComputeAddressType(int slot)
    {
        NativeCodeType type = program.Types[function.Slots[slot].Type];
        return type.Pointee >= 0 || (type.Kind & (TypeKind.Scope | TypeKind.Function | TypeKind.Range)) != 0;
    }

    private bool Observe(int pc, NativeInstruction instruction)
    {
        int result = instruction.Result;
        if (instruction.Op == NativeOp.Store)
        {
            Points address = Input(pc, instruction.A);
            bool changed = writes.Merge(address);
            if (AddressType(instruction.B) && (address.Top || address.Formals.Count != 0))
            {
                changed |= !unknownWrite;
                unknownWrite = true;
                changed |= escaped.Merge(Input(pc, instruction.B));
            }
            return (AddressType(instruction.B) && Write(address, Input(pc, instruction.B), instruction.Type)) | changed;
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
            return destination.MutProjections.Add(ProjectionAt(source, instruction)) | destination.Retained.Add(source) | destination.Layouts.Add(instruction.C);
        bool changed = source.Top && destination.MakeUnknown();
        if (source.Formals.Count != 0 || source.Applications.Count != 0 || source.Reads.Count != 0 || source.Projections.Count != 0 || source.ElementSources.Count != 0)
        {
            changed |= destination.MutProjections.Add(ProjectionAt(source, instruction));
            changed |= destination.Retained.Add(source);
        }
        for (int i = 0; i < source.Targets.Count; i++)
        {
            Target target = source.Targets[i];
            int field = Field(target, instruction);
            if (field >= 0) changed |= destination.Targets.Add(target with { Field = field });
            else changed |= destination.MakeUnknown();
        }
        for (int i = 0; i < source.Foreign.Count; i++)
        {
            ForeignTarget target = source.Foreign[i];
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
        for (int i = 0; i < address.Targets.Count; i++)
        {
            Target target = Normalize(address.Targets[i], type);
            if (target.Allocation < 0)
            {
                if (target.Slot == ownSlot) continue;
                hasCellWrite[target.Slot] = true;
                changed |= cellWrites[target.Slot].Merge(source);
                continue;
            }
            Dictionary<int, Points> fields = memory[target.Allocation] ??= [];
            if (!fields.TryGetValue(target.Field, out Points? contents)) fields.Add(target.Field, contents = new());
            changed |= contents.Merge(source);
            if (Clobbered(target)) changed |= contents.MakeUnknown();
        }
        for (int i = 0; i < address.Formals.Count; i++) changed |= mutated.Add(address.Formals[i]);
        for (int i = 0; i < address.Projections.Count; i++)
        {
            Projection projection = address.Projections[i];
            if (projection.Instruction.Op == NativeOp.IndexAddress) changed |= effects.Retained.Add(projection.Source);
        }
        for (int i = 0; i < address.Foreign.Count; i++)
        {
            ForeignTarget target = address.Foreign[i];
            if (!target.Region.Fields.TryGetValue(target.Field, out Points? contents)) target.Region.Fields.Add(target.Field, contents = new());
            changed |= contents.Merge(source);
            if (target.Region.Clobbered) changed |= contents.MakeUnknown();
        }
        return changed;
    }

    private bool Read(Points destination, Points address, int type)
    {
        bool changed = address.Top && destination.MakeUnknown();
        for (int i = 0; i < address.Formals.Count; i++)
        {
            Formal formal = address.Formals[i];
            if (formal.Kind == FormalKind.CaptureAddress && !mutated.Contains(formal))
            {
                Formal contents = formal with { Kind = FormalKind.CaptureContents };
                changed |= destination.MutFormals.Add(contents);
                changed |= destination.MutLayoutFormals.Add(contents);
            }
            else
            {
                ReadExpression read = ReadAt(address, type);
                changed |= destination.MutReads.Add(read) | destination.MutLayoutReads.Add(read);
                changed |= destination.Retained.Add(address);
            }
        }
        if (address.Applications.Count != 0 || address.Projections.Count != 0 || address.Reads.Count != 0 || address.ElementSources.Count != 0)
        {
            ReadExpression read = ReadAt(address, type);
            changed |= destination.MutReads.Add(read) | destination.MutLayoutReads.Add(read);
            changed |= destination.Retained.Add(address);
        }
        for (int i = 0; i < address.Foreign.Count; i++)
        {
            ForeignTarget target = address.Foreign[i];
            if (target.Region.Fields.TryGetValue(target.Field, out Points? contents)) changed |= destination.Merge(contents);
            else changed |= destination.MakeUnknown();
        }
        for (int i = 0; i < address.Targets.Count; i++)
        {
            Target target = Normalize(address.Targets[i], type);
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
        clobberSeen.Clear();
        clobberPending.Clear();
        clobberPending.Enqueue(effects);
        if (effects.Top || unknownWrite) clobberPending.Enqueue(escaped);
        while (clobberPending.TryDequeue(out Points? points))
        {
            if (!clobberSeen.Add(points)) continue;
            for (int i = 0; i < points.Retained.Count; i++) clobberPending.Enqueue(points.Retained[i]);
            for (int i = 0; i < points.Elements.Count; i++) clobberPending.Enqueue(points.Elements[i]);
            for (int i = 0; i < points.Projections.Count; i++) clobberPending.Enqueue(points.Projections[i].Source);
            for (int i = 0; i < points.Reads.Count; i++) clobberPending.Enqueue(points.Reads[i].Address);
            for (int i = 0; i < points.Applications.Count; i++)
            {
                Application application = points.Applications[i];
                for (int j = 0; j < application.Arguments.Length; j++) clobberPending.Enqueue(application.Arguments[j]);
                clobberPending.Enqueue(application.Callee);
            }
            for (int i = 0; i < points.Callables.Count; i++)
            {
                NativeCallableOrigin callable = points.Callables[i];
                for (int j = 0; j < callable.Captures.Length; j++) clobberPending.Enqueue(callable.Captures[j]);
            }
            for (int i = 0; i < points.Targets.Count; i++)
            {
                Target target = points.Targets[i];
                if (target.Allocation < 0)
                {
                    clobberPending.Enqueue(values[target.Slot]); clobberPending.Enqueue(bindings[target.Slot]);
                    if (AddressType(target.Slot)) { hasCellWrite[target.Slot] = true; changed |= cellWrites[target.Slot].MakeUnknown(); }
                    continue;
                }
                changed |= clobbered.Add(target);
                if (memory[target.Allocation] is not { } fields) continue;
                foreach (var field in fields)
                {
                    if (target.Field >= 0 && field.Key >= 0 && !Compatible(target, target with { Field = field.Key })) continue;
                    clobberPending.Enqueue(field.Value);
                    changed |= field.Value.MakeUnknown();
                }
            }
            for (int i = 0; i < points.Foreign.Count; i++)
            {
                ForeignTarget target = points.Foreign[i];
                if (!target.Region.Clobbered) { target.Region.Clobbered = true; changed = true; }
                foreach (Points contents in target.Region.Fields.Values)
                { clobberPending.Enqueue(contents); changed |= contents.MakeUnknown(); }
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
        return actual.Layouts.ToHashSet();
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
