namespace Msync.Core.Native;

internal sealed class NativeCallableOrigin(int function, int environment, bool plain, int creator, int pc, string? native, int captures)
{
    internal int Function { get; } = function;
    internal int Environment { get; } = environment;
    internal bool Plain { get; } = plain;
    internal int Creator { get; } = creator;
    internal int Pc { get; } = pc;
    internal string? NativeName { get; } = native;
    internal NativeAddressAnalysis.Points[] Captures { get; } = Enumerable.Range(0, captures).Select(_ => new NativeAddressAnalysis.Points()).ToArray();
}

internal sealed partial class NativeAddressAnalysis
{
    internal enum FormalKind : byte { Argument, CaptureValue, CaptureAddress, CaptureContents }
    internal readonly record struct Formal(FormalKind Kind, int Index) : ISymbolic;
    internal sealed class ForeignRegion(int function, int allocation, int slot, int layout)
    {
        internal int Function { get; } = function;
        internal int Allocation { get; } = allocation;
        internal int Slot { get; } = slot;
        internal int Layout { get; } = layout;
        internal Dictionary<int, Points> Fields { get; } = [];
        internal bool Clobbered { get; set; }
    }
    internal readonly record struct ForeignTarget(ForeignRegion Region, int Field = -1);

    internal sealed class Application(int arguments, NativeInstruction? dispatch, bool effect, int originFunction, int originPc, int role) : ISymbolic
    {
        internal Points Callee { get; } = new();
        internal int OriginFunction { get; } = originFunction;
        internal int OriginPc { get; } = originPc;
        internal int Role { get; } = role;
        internal Points[] Arguments { get; } = Enumerable.Range(0, arguments).Select(_ => new Points()).ToArray();
        internal NativeInstruction? Dispatch { get; } = dispatch;
        internal bool Effect { get; } = effect;
    }
    internal sealed record Adaptation(Points Source, NativeInstruction Instruction) : ISymbolic;
    private sealed class ApplicationValues(int arguments)
    {
        internal Points Callee { get; } = new();
        internal Points[] Arguments { get; } = Enumerable.Range(0, arguments).Select(_ => new Points()).ToArray();
        internal Points Result { get; } = new();
        internal Points Discarded { get; } = new();
    }
    private readonly Dictionary<(int Pc, int OriginFunction, int OriginPc, int Role, int Arguments, bool Effect), Application> applicationNodes = [];
    private readonly Dictionary<(int Pc, Points Source), Adaptation> adaptationNodes = [];
    private Points OperandIdentity(Points source)
    {
        // Import wrappers are not new value definitions. Normalize a pure formal
        // to its defining entry node; actual operand bindings remain distinct.
        if (source.PureFormal is not { } formal) return source;
        Points entry = formal.Kind == FormalKind.Argument ? Node(function.CaptureCount + formal.Index)
            : Node((formal.Kind == FormalKind.CaptureAddress ? values.Length : 0) + formal.Index);
        return entry.PureFormal == formal && source.LayoutFormals.Contains(formal) == entry.LayoutFormals.Contains(formal) ? entry : source;
    }

    private Application ApplicationAt(int pc, Points[] arguments, NativeInstruction? instruction, bool effect, Application? origin, int role)
    {
        int originFunction = origin?.OriginFunction ?? functionIndex, originPc = origin?.OriginPc ?? pc;
        if (role == 0) role = origin?.Role ?? 0;
        var key = (pc, originFunction, originPc, role, arguments.Length, effect);
        return applicationNodes.TryGetValue(key, out Application? value) ? value
            : applicationNodes[key] = new(arguments.Length, instruction, effect, originFunction, originPc, role);
    }
    private Adaptation AdaptationAt(int pc, NativeInstruction instruction, Points source)
    {
        source = OperandIdentity(source);
        var key = (pc, source);
        return adaptationNodes.TryGetValue(key, out Adaptation? value) ? value : adaptationNodes[key] = new(source, instruction);
    }

    internal sealed record Projection(Points Source, NativeInstruction Instruction) : ISymbolic;
    internal sealed record ReadExpression(Points Address, int Type) : ISymbolic;
    private readonly Dictionary<(Points Source, NativeOp Op, int B, int C), Projection> projections = [];
    private readonly Dictionary<(Points Address, int Type), ReadExpression> reads = [];
    private Projection ProjectionAt(Points source, NativeInstruction instruction)
    {
        source = OperandIdentity(source);
        var key = (source, instruction.Op, instruction.B, instruction.C);
        return projections.TryGetValue(key, out Projection? value) ? value : projections[key] = new(source, instruction);
    }
    private ReadExpression ReadAt(Points address, int type)
    {
        address = OperandIdentity(address);
        var key = (address, type);
        return reads.TryGetValue(key, out ReadExpression? value) ? value : reads[key] = new(OperandIdentity(address), type);
    }

    internal sealed class ProgramFacts(NativeProgram program)
    {
        private readonly Dictionary<int, NativeAddressAnalysis> functions = [];
        private readonly Stack<NativeAddressAnalysis> newlyRegistered = [];
        private readonly Queue<NativeAddressAnalysis> dirty = [];
        private bool running, complete;
        internal NativeProgram Program { get; private set; } = program;
        internal object Gate { get; } = new();
        internal void Register(int index, NativeAddressAnalysis facts)
        {
            lock (Gate)
            {
                if (functions.TryAdd(index, facts))
                {
                    complete = false;
                    facts.queued = true;
                    newlyRegistered.Push(facts);
                }
            }
        }
        internal NativeAddressAnalysis Function(int index)
        {
            lock (Gate) return functions.TryGetValue(index, out NativeAddressAnalysis? facts)
                ? facts : new(Program, Program.Functions[index], this, index);
        }
        internal void Invalidate(NativeProgram updated, params ReadOnlySpan<int> changedFunctions)
        {
            lock (Gate)
            {
                Program = updated;
                if (changedFunctions.Length == 0 || functions.Count == 0) return;
                HashSet<int> affected = [];
                Queue<int> pending = new();
                foreach (int fn in changedFunctions)
                    if (functions.ContainsKey(fn) && affected.Add(fn)) pending.Enqueue(fn);
                while (pending.TryDequeue(out int current))
                {
                    if (!functions.TryGetValue(current, out NativeAddressAnalysis? ana)) continue;
                    foreach (NativeAddressAnalysis caller in ana.callers)
                        if (affected.Add(caller.functionIndex)) pending.Enqueue(caller.functionIndex);
                }
                if (affected.Count == 0) return;
                foreach (int id in affected)
                {
                    if (!functions.Remove(id, out NativeAddressAnalysis? ana)) continue;
                    foreach (Invocation inv in ana.invocations.Values) inv.Callee.callers.Remove(ana);
                    ana.frozen = true;
                    ana.queued = false;
                }
                if (newlyRegistered.Count > 0)
                {
                    List<NativeAddressAnalysis> kept = new(newlyRegistered.Count);
                    while (newlyRegistered.Count > 0)
                    {
                        NativeAddressAnalysis item = newlyRegistered.Pop();
                        if (functions.ContainsKey(item.functionIndex)) kept.Add(item);
                    }
                    for (int i = kept.Count - 1; i >= 0; i--) newlyRegistered.Push(kept[i]);
                }
                if (dirty.Count > 0)
                {
                    int count = dirty.Count;
                    for (int i = 0; i < count; i++)
                    {
                        NativeAddressAnalysis item = dirty.Dequeue();
                        if (functions.ContainsKey(item.functionIndex)) dirty.Enqueue(item);
                    }
                }
                complete = newlyRegistered.Count == 0 && dirty.Count == 0 && functions.Values.All(item => item.frozen);
            }
        }
        internal void Solve() { lock (Gate) SolveLocked(); }
        private void SolveLocked()
        {
            if (running || complete) return;
            running = true;
            try
            {
                int deferrals = 0;
                while (newlyRegistered.Count > 0 || dirty.Count > 0)
                {
                    NativeAddressAnalysis facts;
                    if (newlyRegistered.Count > 0)
                    {
                        facts = newlyRegistered.Pop();
                        deferrals = 0;
                    }
                    else
                    {
                        facts = dirty.Dequeue();
                        if (deferrals < dirty.Count && facts.HasQueuedCallee())
                        {
                            dirty.Enqueue(facts);
                            deferrals++;
                            continue;
                        }
                        deferrals = 0;
                    }
                    facts.queued = false;
                    if (facts.frozen || !facts.Step()) continue;
                    facts.SummaryVersion++;
                    if (!facts.queued) { facts.queued = true; dirty.Enqueue(facts); }
                    foreach (NativeAddressAnalysis caller in facts.callers)
                        if (!caller.frozen && !caller.queued) { caller.queued = true; dirty.Enqueue(caller); }
                }
                foreach (NativeAddressAnalysis facts in functions.Values) facts.frozen = true;
                complete = true;
            }
            finally { running = false; }
        }
    }

    private bool HasQueuedCallee()
    {
        foreach (Invocation invocation in invocations.Values)
        {
            NativeAddressAnalysis callee = invocation.Callee;
            if (!ReferenceEquals(callee, this) && callee.queued && !callee.frozen) return true;
        }
        return false;
    }

    private readonly Dictionary<(int Pc, int Creator, int Creation, int Target, int Environment, string? Native), NativeCallableOrigin> callableOrigins = [];
    private readonly Dictionary<(int Pc, NativeCallableOrigin Callable), Invocation> invocations = [];
    private readonly Dictionary<(int Pc, int Item), Points> itemValues = [];
    private readonly HashSet<Formal> mutated = [];
    private Points ElementValue(int pc, int item) => itemValues.TryGetValue((pc, item), out Points? value)
        ? value : itemValues[(pc, item)] = new();

    internal IReadOnlyCollection<NativeCallableOrigin>? CallablesAt(int pc, int slot)
    {
        Points value = At(pc, slot);
        return value.Top || value.Formals.Count != 0 || value.Applications.Count != 0 || value.Adaptations.Count != 0 || value.Reads.Count != 0 || value.Projections.Count != 0 ? null : value.Callables;
    }

    private NativeCallableOrigin Callable(int pc, int creator, int creation, int target, int environment, bool plain, string? native, int captures)
    {
        var key = (pc, creator, creation, target, environment, native);
        if (!callableOrigins.TryGetValue(key, out NativeCallableOrigin? origin))
            callableOrigins.Add(key, origin = new(target, environment, plain, creator, creation, native, captures));
        return origin;
    }

    private bool Function(int pc, NativeInstruction instruction, Points destination)
    {
        int count = instruction.C == 0 ? 0 : instruction.Inputs?.Count ?? 0;
        NativeCallableOrigin callable = Callable(pc, functionIndex, pc, instruction.A, instruction.B, instruction.C == 0, instruction.A < 0 ? instruction.Symbol : null, count);
        bool changed = destination.Callables.Add(callable);
        for (int index = 0; index < count; index++)
        {
            Points capture = Input(pc, instruction.Inputs![index]);
            changed |= callable.Captures[index].Merge(capture);
            changed |= destination.Retained.Add(capture);
        }
        return changed;
    }

    private int ResolveTarget(NativeCallableOrigin callable, NativeInstruction? instruction)
    {
        if (instruction is null) return callable.Function;
        if (instruction.C >= 0) return program.SessionDispatches![instruction.C].Targets.GetValueOrDefault(callable.Function, -1);
        if (instruction.B >= 0) return instruction.B;
        return instruction.Symbol is null ? callable.Function : FunctionDispatch.Targets(instruction.Symbol).GetValueOrDefault(callable.Function, -1);
    }

    private bool Adapt(int pc, NativeInstruction instruction, Points destination) => AdaptValue(pc, instruction, Input(pc, instruction.A), destination);
    private bool AdaptValue(int pc, NativeInstruction instruction, Points source, Points destination)
    {
        bool changed = source.Top ? destination.MakeUnknown() : false;
        if (source.Formals.Count != 0 || source.Applications.Count != 0 || source.Adaptations.Count != 0 || source.Reads.Count != 0 || source.Projections.Count != 0)
            changed |= destination.MutAdaptations.Add(AdaptationAt(pc, instruction, source));
        for (int i = 0; i < source.Callables.Count; i++)
        {
            NativeCallableOrigin original = source.Callables[i];
            int target = original.NativeName is null ? ResolveTarget(original, instruction) : original.Function;
            if (target < 0 && original.NativeName is null) continue; // This alternative traps before producing a value.
            int environment = original.Environment;
            if (target >= 0 && target != original.Function) environment = -1;
            NativeCallableOrigin adapted = Callable(pc, original.Creator, original.Pc, target, environment, original.Plain,
                original.NativeName, original.Captures.Length);
            changed |= destination.Callables.Add(adapted);
            for (int index = 0; index < adapted.Captures.Length; index++) changed |= adapted.Captures[index].Merge(original.Captures[index]);
            changed |= destination.Retained.Add(source);
        }
        return changed;
    }

    private readonly Dictionary<int, Points[]> argumentBuffers = [];
    private Points[] Arguments(int pc, NativeInstruction instruction)
    {
        if (instruction.Inputs is not { Count: > 0 } inputs) return [];
        if (argumentBuffers.TryGetValue(pc, out Points[]? cached) && cached.Length == inputs.Count)
        {
            for (int i = 0; i < inputs.Count; i++) cached[i] = Input(pc, inputs[i]);
            return cached;
        }
        Points[] result = new Points[inputs.Count];
        for (int i = 0; i < inputs.Count; i++) result[i] = Input(pc, inputs[i]);
        argumentBuffers[pc] = result;
        return result;
    }

    private bool Invoke(int pc, Points callee, Points[] arguments, Points destination, NativeInstruction? instruction = null, Points? effectSink = null, Application? origin = null, int role = 0)
    {
        effectSink ??= effects;
        bool changed = callee.Top ? destination.MakeUnknown() | effectSink.MakeUnknown() : false;
        if (callee.Formals.Count != 0 || callee.Applications.Count != 0 || callee.Adaptations.Count != 0 || callee.Reads.Count != 0 || callee.Projections.Count != 0)
        {
            Application application = ApplicationAt(pc, arguments, instruction, false, origin, role);
            changed |= destination.MutApplications.Add(application) | destination.MutLayoutApplications.Add(application);
            Application effect = ApplicationAt(pc, arguments, instruction, true, origin, role);
            changed |= application.Callee.Merge(callee) | effect.Callee.Merge(callee);
            changed |= effectSink.MutApplications.Add(effect);
            for (int index = 0; index < arguments.Length; index++)
                changed |= application.Arguments[index].Merge(arguments[index]) | effect.Arguments[index].Merge(arguments[index]);
        }
        for (int i = 0; i < callee.Callables.Count; i++)
        {
            NativeCallableOrigin callable = callee.Callables[i];
            // Runtime checks NativeName before Call.B/session/source dispatch.
            if (callable.NativeName is { } native) { changed |= Native(pc, native, arguments, destination, effectSink, origin); continue; }
            int target = ResolveTarget(callable, instruction);
            if (target < 0) continue;
            var key = (pc, callable);
            if (!invocations.TryGetValue(key, out Invocation? invocation))
            {
                NativeAddressAnalysis targetFacts = owner.Function(target);
                targetFacts.callers.Add(this);
                invocations.Add(key, invocation = new(this, targetFacts, callable, pc, arguments.Length));
            }
            bool argsChanged = false;
            for (int index = 0; index < arguments.Length; index++) argsChanged |= invocation.Arguments[index].Merge(arguments[index]);
            changed |= argsChanged;
            if (argsChanged || !invocation.IsCurrent(destination, effectSink))
            {
                changed |= invocation.Import(invocation.Callee.returns, destination);
                changed |= invocation.Import(invocation.Callee.externalWrites, effectSink);
                invocation.MarkCurrent(destination, effectSink);
            }
        }
        return changed;
    }

    private readonly Dictionary<int, ForeignRegion> producedRegions = [];
    private bool Environment(int pc, NativeInstruction instruction, Points destination)
    {
        if (!producedRegions.TryGetValue(pc, out ForeignRegion? region))
            producedRegions.Add(pc, region = new(functionIndex, -2, pc, instruction.B));
        Points source = Input(pc, instruction.A);
        bool changed = destination.Foreign.Add(new(region)) | destination.Layouts.Add(instruction.B);
        foreach (NativeCallableOrigin callable in source.Callables)
            for (int index = 0; index < callable.Captures.Length; index++)
            {
                if (!region.Fields.TryGetValue(index, out Points? field)) region.Fields.Add(index, field = new());
                changed |= field.Merge(callable.Captures[index]);
            }
        if (source.Top || source.Formals.Count != 0)
            for (int index = 0; index < program.Layouts[instruction.B].Fields.Count; index++)
            {
                if (!region.Fields.TryGetValue(index, out Points? field)) region.Fields.Add(index, field = new());
                changed |= field.MakeUnknown();
            }
        return changed;
    }

    private bool Static(int pc, NativeInstruction instruction, Points destination)
    {
        if (!producedRegions.TryGetValue(pc, out ForeignRegion? region))
        {
            producedRegions.Add(pc, region = new(functionIndex, -3, instruction.A, program.Statics[instruction.A].Layout));
            for (int index = 0; index < program.Layouts[region.Layout].Fields.Count; index++)
            { Points field = new(); field.MakeUnknown(); region.Fields.Add(index, field); }
        }
        return destination.Foreign.Add(new(region)) | destination.Layouts.Add(region.Layout);
    }

    private bool Narrow(int pc, NativeInstruction instruction, Points destination)
    {
        bool changed = destination.Merge(Input(pc, instruction.A), layout: false);
        NativeCodeType type = program.Types[instruction.Type];
        if ((type.Kind & TypeKind.Scope) != 0 && type.Layouts is { } layouts)
        {
            int previous = destination.Layouts.Count; destination.Layouts.UnionWith(layouts);
            return changed | previous != destination.Layouts.Count;
        }
        return changed | destination.UnknownLayout();
    }

    private bool Range(int pc, NativeInstruction instruction, Points destination)
    {
        bool changed = false;
        IReadOnlyList<int> items = instruction.Inputs ?? [];
        for (int index = 0; index < items.Count; index++)
        {
            Points value = Input(pc, items[index]);
            changed |= destination.Retained.Add(value);
            changed |= destination.Elements.Add(value);
        }
        return changed;
    }

    private bool Next(int pc, NativeInstruction instruction, Points destination)
    {
        bool changed = false;
        foreach (int id in Flow.Iterators(pc, instruction.A)) changed |= ReadElements(Node(id), destination);
        return changed;
    }

    private static bool CopyElements(Points source, Points destination)
    {
        int previous = destination.Elements.Count + destination.RangeSources.Count;
        destination.Elements.UnionWith(in source.Elements);
        if (source.RangeSources.Count != 0) destination.MutRangeSources.UnionWith(in source.RangeSources);
        if (source.Formals.Count != 0 || source.Applications.Count != 0) destination.MutRangeSources.Add(source);
        bool changed = previous != destination.Elements.Count + destination.RangeSources.Count;
        if (source.ElementsUnknown || source.Top) changed |= destination.UnknownElements();
        return changed;
    }

    private static bool ReadElements(Points source, Points destination, HashSet<Points>? active = null)
    {
        if (active is null)
        {
            if (source.RangeSources.Count == 0)
            {
                bool directChanged = source.Top || source.ElementsUnknown ? destination.MakeUnknown() : false;
                if (source.Formals.Count != 0 || source.Applications.Count != 0)
                    directChanged |= destination.MutElementSources.Add(source) | destination.MutLayoutElements.Add(source);
                for (int i = 0; i < source.Elements.Count; i++) directChanged |= destination.Merge(source.Elements[i]);
                return directChanged;
            }
            active = [];
        }
        if (!active.Add(source)) return false;
        bool changed = source.Top || source.ElementsUnknown ? destination.MakeUnknown() : false;
        if (source.Formals.Count != 0 || source.Applications.Count != 0)
            changed |= destination.MutElementSources.Add(source) | destination.MutLayoutElements.Add(source);
        for (int i = 0; i < source.Elements.Count; i++) changed |= destination.Merge(source.Elements[i]);
        for (int i = 0; i < source.RangeSources.Count; i++) changed |= ReadElements(source.RangeSources[i], destination, active);
        active.Remove(source);
        return changed;
    }

    private bool Native(int pc, string name, Points[] arguments, Points destination, Points? effectSink = null, Application? origin = null)
    {
        Msync.Core.NativeSignature signature = NativeSignatures.Get(name);
        effectSink ??= effects;
        bool changed = false;
        if (((signature.Behavior ?? NativeBehavior.None) & NativeBehavior.LocalMemory) != 0)
            foreach (Points argument in arguments) changed |= effectSink.Retained.Add(argument);
        if (signature.ElementCallback is { } callback && signature.ResultRule is not (NativeResultRule.Transform or NativeResultRule.RangeScan))
        {
            Points item = ElementValue(pc, -9), discarded = ElementValue(pc, -10);
            for (int index = 0; index < arguments.Length; index++)
                if ((signature.Parameters[Math.Min(index, signature.Parameters.Count - 1)].Kind & TypeKind.Range) != 0)
                    changed |= ReadElements(arguments[index], item);
            Points[] callbackArgs = callback.Arity == 1 ? [item] : new Points[callback.Arity];
            if (callback.Arity > 1) Array.Fill(callbackArgs, item);
            changed |= Invoke(pc, arguments[callback.FunctionArgument], callbackArgs, discarded, effectSink: effectSink, origin: origin, role: 1);
        }
        switch (signature.ResultRule)
        {
            case NativeResultRule.FirstArgument when (signature.Result.Kind & TypeKind.Range) != 0:
                return CopyElements(arguments[0], destination) | destination.Retained.Add(arguments[0]) | changed;
            case NativeResultRule.RangeOfArguments:
                foreach (Points item in arguments) changed |= destination.Elements.Add(item);
                return changed;
            case NativeResultRule.RangeRepeat: return destination.Elements.Add(arguments[0]);
            case NativeResultRule.RangeConcat:
            case NativeResultRule.RangeMerge:
                return CopyElements(arguments[0], destination) | CopyElements(arguments[1], destination);
            case NativeResultRule.RangeMergeLeft: return CopyElements(arguments[0], destination);
            case NativeResultRule.RangeItem:
                return ReadElements(arguments[0], destination) | changed;
            case NativeResultRule.Transform:
                Points input = ElementValue(pc, -2), output = ElementValue(pc, -1);
                changed |= ReadElements(arguments[0], input);
                changed |= Invoke(pc, arguments[1], [input], output, effectSink: effectSink, origin: origin, role: 2);
                return destination.Elements.Add(output) | changed;
            case NativeResultRule.RangeScan:
                Points accumulator = ElementValue(pc, -4), itemInput = ElementValue(pc, -5), scanned = ElementValue(pc, -6);
                changed |= accumulator.Merge(arguments[1]) | accumulator.Merge(scanned);
                changed |= ReadElements(arguments[0], itemInput);
                changed |= Invoke(pc, arguments[2], [accumulator, itemInput], scanned, effectSink: effectSink, origin: origin, role: 3);
                return destination.Elements.Add(scanned) | changed;
            case NativeResultRule.RangeOfType:
                return false; // This producer is empty; it does not manufacture values from the type descriptor.
            case NativeResultRule.RangeFlatten:
                if (arguments[0].Top || arguments[0].ElementsUnknown || arguments[0].Formals.Count != 0) changed |= destination.UnknownElements();
                for (int i = 0; i < arguments[0].Elements.Count; i++) changed |= CopyElements(arguments[0].Elements[i], destination);
                return changed;
            case NativeResultRule.RangeNested:
                return destination.Elements.Add(arguments[0]);
            default:
                return destination.MakeUnknown();
        }
    }

    private sealed class Invocation(NativeAddressAnalysis caller, NativeAddressAnalysis callee, NativeCallableOrigin callable, int pc, int parameters)
    {
        internal NativeAddressAnalysis Callee { get; } = callee;
        internal Points[] Arguments { get; } = Enumerable.Range(0, parameters).Select(_ => new Points()).ToArray();
        private readonly Dictionary<Points, Points> imported = [];
        private readonly Dictionary<Application, ApplicationValues> applications = [];
        private readonly Dictionary<Adaptation, Points> adaptations = [];
        private readonly Dictionary<Projection, Points> projections = [];
        private readonly Dictionary<ReadExpression, Points> reads = [];
        private readonly Dictionary<Points, Points> projected = [];
        private readonly Dictionary<(int Function, int Allocation, int Slot), ForeignRegion> regions = [];
        private readonly HashSet<(Points, Points)> visited = [];
        private long lastCalleeVersion = -1;
        private long lastCallerStateVersion = -1;
        private long lastCallerWriteEpoch = -1;
        private bool usesCaptures;
        private long[]? lastCaptureStamps;
        private Points?[]? captureContentsCache;
        private readonly List<KeyValuePair<int, Points>> foreignFieldsScratch = [];
        private Points? lastDestination;
        private Points? lastEffectSink;

        internal bool IsCurrent(Points destination, Points effectSink)
            => lastCalleeVersion == Callee.SummaryVersion
            && ReferenceEquals(lastDestination, destination)
            && ReferenceEquals(lastEffectSink, effectSink)
            && (!usesCaptures || (lastCallerStateVersion == caller.stateVersion
                && lastCallerWriteEpoch == caller.writeEpoch
                && CapturesUnchanged()));

        private bool CapturesUnchanged()
        {
            if (callable.Captures.Length == 0) return true;
            if (lastCaptureStamps is null) return false;
            for (int i = 0; i < callable.Captures.Length; i++)
                if (callable.Captures[i].MonotonicStamp() != lastCaptureStamps[i]) return false;
            return true;
        }

        internal void MarkCurrent(Points destination, Points effectSink)
        {
            lastCalleeVersion = Callee.SummaryVersion;
            lastDestination = destination;
            lastEffectSink = effectSink;
            if (usesCaptures)
            {
                lastCallerStateVersion = caller.stateVersion;
                lastCallerWriteEpoch = caller.writeEpoch;
                if (callable.Captures.Length != 0)
                {
                    lastCaptureStamps ??= new long[callable.Captures.Length];
                    for (int i = 0; i < callable.Captures.Length; i++)
                        lastCaptureStamps[i] = callable.Captures[i].MonotonicStamp();
                }
            }
        }

        private bool importing, fieldChanged;
        internal bool Import(Points source, Points destination)
        {
            // A recursive summary is a dependency, not a request to replay its call stack.
            // The owner's existing fixed point revisits this edge after its producers grow.
            if (importing) return false;
            importing = true;
            fieldChanged = false;
            try { return Import(source, destination, visited) | fieldChanged; }
            finally { visited.Clear(); importing = false; }
        }
        private bool Import(Points source, Points destination, HashSet<(Points, Points)> visited)
        {
            // A summary is a graph, not a tree of paths. A later producer change
            // schedules another owner iteration; diamonds need no repeated replay.
            if (!visited.Add((source, destination))) return false;
            bool changed = false;
            if (source.Top) changed |= destination.UnknownAliases();
            if (source.LayoutUnknown) changed |= destination.UnknownLayout();
            if (source.ElementsUnknown) changed |= destination.UnknownElements();
            int previous = destination.Layouts.Count; destination.Layouts.UnionWith(in source.Layouts); changed |= previous != destination.Layouts.Count;
            for (int i = 0; i < source.Formals.Count; i++)
            {
                Formal formal = source.Formals[i];
                if (formal.Kind == FormalKind.Argument)
                    changed |= formal.Index < Arguments.Length ? destination.Merge(Arguments[formal.Index], source.LayoutFormals.Contains(formal)) : destination.MakeUnknown();
                else if (formal.Index >= callable.Captures.Length) { usesCaptures = true; changed |= destination.MakeUnknown(); }
                else if (formal.Kind == FormalKind.CaptureContents)
                {
                    usesCaptures = true;
                    Points contents = (captureContentsCache ??= new Points?[callable.Captures.Length])[formal.Index] ??= new();
                    caller.Read(contents, callable.Captures[formal.Index], Callee.function.Signature.Captures[formal.Index].Type);
                    changed |= destination.Merge(contents, source.LayoutFormals.Contains(formal));
                }
                else { usesCaptures = true; changed |= destination.Merge(callable.Captures[formal.Index], source.LayoutFormals.Contains(formal)); }
            }
            for (int i = 0; i < source.Projections.Count; i++)
            {
                Projection expression = source.Projections[i];
                Points input = Imported(expression.Source);
                changed |= Import(expression.Source, input, visited);
                if (!projections.TryGetValue(expression, out Points? result)) projections.Add(expression, result = new());
                changed |= caller.ProjectValue(expression.Instruction, input, result) | destination.Merge(result);
            }
            for (int i = 0; i < source.Reads.Count; i++)
            {
                ReadExpression expression = source.Reads[i];
                Points input = Imported(expression.Address);
                changed |= Import(expression.Address, input, visited);
                if (!reads.TryGetValue(expression, out Points? result)) reads.Add(expression, result = new());
                changed |= caller.Read(result, input, expression.Type) | destination.Merge(result, source.LayoutReads.Contains(expression));
            }
            for (int i = 0; i < source.Applications.Count; i++)
            {
                Application expression = source.Applications[i];
                if (!applications.TryGetValue(expression, out ApplicationValues? values))
                    applications.Add(expression, values = new(expression.Arguments.Length));
                values.Callee.Canonical = expression.Callee.Canonical;
                values.Result.Canonical = source.Canonical;
                for (int index = 0; index < values.Arguments.Length; index++) values.Arguments[index].Canonical = expression.Arguments[index].Canonical;
                changed |= Import(expression.Callee, values.Callee, visited);
                for (int index = 0; index < values.Arguments.Length; index++) changed |= Import(expression.Arguments[index], values.Arguments[index], visited);
                changed |= expression.Effect
                    ? caller.Invoke(pc, values.Callee, values.Arguments, values.Discarded, expression.Dispatch, values.Result, expression)
                    : caller.Invoke(pc, values.Callee, values.Arguments, values.Result, expression.Dispatch, origin: expression);
                changed |= destination.Merge(values.Result, source.LayoutApplications.Contains(expression));
            }
            for (int i = 0; i < source.Adaptations.Count; i++)
            {
                Adaptation expression = source.Adaptations[i];
                Points input = Imported(expression.Source);
                changed |= Import(expression.Source, input, visited);
                if (!adaptations.TryGetValue(expression, out Points? result)) adaptations.Add(expression, result = new());
                changed |= caller.AdaptValue(pc, expression.Instruction, input, result) | destination.Merge(result);
            }
            for (int i = 0; i < source.ElementSources.Count; i++)
            {
                Points expression = source.ElementSources[i];
                Points input = Imported(expression);
                changed |= Import(expression, input, visited);
                if (!projected.TryGetValue(expression.Canonical, out Points? result)) projected.Add(expression.Canonical, result = new());
                changed |= ReadElements(input, result) | destination.Merge(result, source.LayoutElements.Contains(expression));
            }
            for (int i = 0; i < source.RangeSources.Count; i++)
            {
                Points expression = source.RangeSources[i];
                Points input = Imported(expression);
                changed |= Import(expression, input, visited);
                changed |= CopyElements(input, destination);
            }
            for (int i = 0; i < source.Targets.Count; i++)
            {
                Target target = source.Targets[i];
                int layout = target.Allocation >= 0 ? Callee.function.Instructions[target.Allocation].A : -1;
                ForeignRegion region = Region(Callee.functionIndex, target.Allocation, target.Slot, layout);
                changed |= destination.Foreign.Add(new(region, target.Field));
                if (target.Allocation >= 0 && Callee.memory[target.Allocation] is { } fields)
                    foreach (var field in fields) changed |= Import(field.Value, Field(region, field.Key), visited);
                if (target.Allocation < 0) changed |= Import(Callee.values[target.Slot], Field(region, -1), visited);
            }
            for (int i = 0; i < source.Foreign.Count; i++)
            {
                ForeignTarget target = source.Foreign[i];
                ForeignRegion region = Region(target.Region.Function, target.Region.Allocation, target.Region.Slot, target.Region.Layout);
                changed |= destination.Foreign.Add(new(region, target.Field));
                if (ReferenceEquals(target.Region, region))
                {
                    foreignFieldsScratch.Clear();
                    foreach (var field in target.Region.Fields) foreignFieldsScratch.Add(field);
                    for (int j = 0; j < foreignFieldsScratch.Count; j++)
                        changed |= Import(foreignFieldsScratch[j].Value, Field(region, foreignFieldsScratch[j].Key), visited);
                }
                else
                {
                    foreach (var field in target.Region.Fields) changed |= Import(field.Value, Field(region, field.Key), visited);
                }
            }
            for (int i = 0; i < source.Callables.Count; i++)
            {
                NativeCallableOrigin original = source.Callables[i];
                NativeCallableOrigin value = caller.Callable(pc, original.Creator, original.Pc, original.Function, original.Environment,
                    original.Plain, original.NativeName, original.Captures.Length);
                changed |= destination.Callables.Add(value);
                for (int index = 0; index < value.Captures.Length; index++) changed |= Import(original.Captures[index], value.Captures[index], visited);
            }
            for (int i = 0; i < source.Elements.Count; i++)
            {
                Points element = source.Elements[i];
                Points value = Imported(element);
                changed |= destination.Elements.Add(value) | Import(element, value, visited);
            }
            for (int i = 0; i < source.Retained.Count; i++)
            {
                Points retained = source.Retained[i];
                Points value = Imported(retained);
                changed |= destination.Retained.Add(value) | Import(retained, value, visited);
            }
            return changed;
        }
        private Points Imported(Points source) => imported.TryGetValue(source.Canonical, out Points? value)
            ? value : imported[source.Canonical] = new() { Canonical = source.Canonical };
        private ForeignRegion Region(int function, int allocation, int slot, int layout)
        {
            var key = (function, allocation, slot);
            return regions.TryGetValue(key, out ForeignRegion? region) ? region : regions[key] = new(function, allocation, slot, layout);
        }
        private Points Field(ForeignRegion region, int index)
        {
            if (!region.Fields.TryGetValue(index, out Points? value))
            { region.Fields.Add(index, value = new()); fieldChanged = true; }
            if (region.Clobbered) fieldChanged |= value.MakeUnknown();
            return value;
        }
    }
}
