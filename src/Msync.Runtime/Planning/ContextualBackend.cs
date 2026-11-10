using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed record ContextualPreparation(Module Module, int? ContinueAt,
    IReadOnlyList<PlanResource> AwaitResources, IReadOnlyList<int>? SourcePositions = null);

public static partial class ContextualBackend
{
    private sealed record Applicability(bool Required, IReadOnlySet<int> LoopHeaders);
    private static readonly ConditionalWeakTable<FunctionCode, Applicability> applicability = new();

    public static bool NeedsPreparation(FunctionCode function) => Applicable(function).Required;

    private static Applicability Applicable(FunctionCode function) => applicability.GetValue(function, static code =>
    {
        HashSet<int> headers = [];
        bool required = false;
        for (int index = 0; index < code.Instructions.Count; index++)
        {
            Instruction instruction = code.Instructions[index];
            required |= instruction.Op is OpCode.Transfer or OpCode.LocalTransfer or OpCode.SelectPath
                or OpCode.Call
                || instruction.Op == OpCode.NativeCall && IsMetadata(instruction.Text);
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse && instruction.A <= index)
                headers.Add(instruction.A);
        }
        return new(required, headers);
    });

    public static Module Prepare(Module module, ExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (module.ExecutionPlans is not null) return module;
        Module prepared = PrepareContinuation(module, module.EntryPoint, 0, options: options,
            cancellationToken: cancellationToken);
        if (prepared.Blocks is not null) return prepared;
        return BlockBuilder.Form(prepared);
    }

    internal static Module Prepare(Module module, ExecutionOptions? options, ContextualResources resources,
        CancellationToken cancellationToken = default)
    {
        if (module.ExecutionPlans is not null) return module;
        Module prepared = PrepareNext(module, module.EntryPoint, 0, null, null, options, resources, cancellationToken).Module;
        return prepared.Blocks is null ? BlockBuilder.Form(prepared) : prepared;
    }

    public static Module PrepareContinuation(Module module, int function, int start,
        IReadOnlyList<Value>? locals = null, IReadOnlyList<Value>? stack = null,
        ExecutionOptions? options = null, CancellationToken cancellationToken = default) =>
        PrepareNext(module, function, start, locals, stack, options, cancellationToken).Module;

    public static ContextualPreparation PrepareNext(Module module, int function, int start,
        IReadOnlyList<Value>? locals = null, IReadOnlyList<Value>? stack = null,
        ExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (module.Context is null) throw new MsyncException("Contextual planning requires a bound execution context.");
        if (!NeedsPreparation(module.Functions[function]))
            return new(module.ExecutionPlans is null ? module with { ExecutionPlans = [] } : module, null, []);
        cancellationToken.ThrowIfCancellationRequested();
        var resources = new ContextualResources(cancellationToken);
        try { return new Preparation(module, function, start, locals, stack, options, resources, cancellationToken).Run(); }
        finally { resources.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    internal static ContextualPreparation PrepareNext(Module module, int function, int start,
        IReadOnlyList<Value>? locals, IReadOnlyList<Value>? stack, ExecutionOptions? options,
        ContextualResources resources, CancellationToken cancellationToken = default) =>
        new Preparation(module, function, start, locals, stack, options, resources, cancellationToken).Run();

    private static bool IsMetadata(string? name) => name is not null && name.StartsWith("fs.", StringComparison.Ordinal)
        && !NativeEvaluation.DefersFileRead(name)
        && NativeSignatures.TryGet(name, out var signature) && signature.Effect == NativeEffect.Read
        && (signature.Behavior.GetValueOrDefault() & NativeBehavior.Invokes) == 0;

    private sealed partial class Preparation
    {
        private sealed record Site(int Instruction, IReadOnlyList<KnownValue> Inputs, bool Observed,
            int? LoopEnd = null, IReadOnlyList<Instruction>? Restores = null);
        private readonly Module module;
        private readonly int function;
        private readonly int start;
        private readonly RuntimeContext context;
        private readonly PlanFileSystem filesystem;
        private readonly CancellationToken cancellationToken;
        private readonly IReadOnlySet<int> loopHeaders;
        private readonly IReadOnlyList<Instruction> code;
        private readonly PreparationTable<Value> constants;
        private readonly PreparationTable<ExecutionPlan> plans;
        private readonly KnownValue[] locals;
        private readonly IReadOnlyList<Value>? incomingLocals;
        private readonly List<KnownValue> stack;
        private readonly Dictionary<int, Instruction[]> replacements = [];
        private readonly Dictionary<ScopeValue, Dictionary<string, Value?>> fields = new(ReferenceEqualityComparer.Instance);
        private Dictionary<CellValue, Value?>? cellValues;
        private HashSet<int>? freshLocals;
        private readonly List<Site> sites = [];
        private readonly List<int> markers = [];
        private readonly HashSet<int> visited = [];
        private TransferGraph graph = new();
        private int? continuation;
        private IReadOnlyList<PlanResource> awaitResources = [];
        private int pc;

        internal Preparation(Module module, int function, int start, IReadOnlyList<Value>? locals,
            IReadOnlyList<Value>? stack, ExecutionOptions? options, ContextualResources resources,
            CancellationToken cancellationToken)
        {
            this.module = module;
            this.function = function;
            this.start = start;
            context = new(options, module.Context);
            this.cancellationToken = cancellationToken;
            loopHeaders = Applicable(module.Functions[function]).LoopHeaders;
            filesystem = new(context, resources, cancellationToken);
            code = module.Functions[function].Instructions;
            constants = new(module.Constants);
            plans = new(module.ExecutionPlans ?? []);
            this.locals = new KnownValue[module.Functions[function].LocalCount];
            incomingLocals = locals;
            if (locals is not null)
                for (int index = 0; index < this.locals.Length && index < locals.Count; index++)
                    this.locals[index] = new(Read(locals[index]));
            this.stack = stack?.Select(value => new KnownValue(Read(value))).ToList() ?? [];
        }

        internal ContextualPreparation Run()
        {
            pc = start;
            while (pc < code.Count && visited.Add(pc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (loopHeaders.Contains(pc)) { Stop(); break; }
                if (!Step()) break;
                pc++;
            }
            Flush();
            return Rewrite();
        }

        private bool Step()
        {
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: return true;
                case OpCode.Constant: Push(new(constants[instruction.A], pc, pc)); return true;
                case OpCode.Null: Push(new(Value.Null, pc, pc)); return true;
                case OpCode.True: Push(new(Value.True, pc, pc)); return true;
                case OpCode.False: Push(new(Value.False, pc, pc)); return true;
                case OpCode.LoadLocal:
                case OpCode.LoadStorage:
                    Push((IncomingCell(instruction.A) is { } readCell
                        ? new KnownValue(ResolveKnownCapture(readCell)) : locals[instruction.A]).At(pc));
                    return true;
                case OpCode.InitializeLocal:
                    SetKnownLocal(instruction.A, Pop().Value, initialize: true);
                    return true;
                case OpCode.StoreLocal:
                    SetKnownLocal(instruction.A, Pop().Value, initialize: false);
                    return true;
                case OpCode.Pop: Pop(); return true;
                case OpCode.Dup:
                    {
                        KnownValue value = Pop();
                        Push(new(value.Value)); Push(new(value.Value)); return true;
                    }
                case OpCode.LoadBuiltin: Push(new(Builtin(instruction.Text), pc, pc)); return true;
                case OpCode.MakeClosure: return KnownClosure(instruction);
                case OpCode.Call: return KnownCall(instruction);
                case OpCode.Convert:
                    {
                        KnownValue value = Pop();
                        return PushComputed(value.Known ? KnownEvaluation.Convert(value.Value!, instruction.Text, context) : null, [value]);
                    }
                case OpCode.Qualify:
                    {
                        KnownValue item = Pop(), root = Pop();
                        Value? value = root.Value is ConnectionValue connection && item.Known
                            ? KnownEvaluation.Qualify(connection, item.Value!, context) : null;
                        return PushComputed(value, [root, item]);
                    }
                case OpCode.PathJoin:
                    {
                        KnownValue right = Pop(), left = Pop();
                        Value? value = left.Value is PathValue or StringValue && right.Value is PathValue or StringValue
                            ? PathSemantics.JoinValue(context.AsPath(left.Value), right.Value) : null;
                        return PushComputed(value, [left, right]);
                    }
                case OpCode.MakeRange:
                    {
                        KnownValue[] inputs = PopMany(instruction.A);
                        Value? result = inputs.All(value => value.Known)
                            ? new RangeValue(inputs.Select(value => value.Value!).ToArray(),
                                inputs.Aggregate(new TypeRef(TypeKind.Never), (type, item) => TypeRef.Union(type, item.Value!.Type))) : null;
                        return PushComputed(result, inputs);
                    }
                case OpCode.MakeScope:
                    {
                        KnownValue[] inputs = PopMany(instruction.A);
                        string[] names = (instruction.Text ?? "").Split('\0');
                        Value? value = inputs.All(item => item.Known)
                            ? new ScopeValue(names.Zip(inputs, (name, item) => KeyValuePair.Create(name, item.Value!)).ToDictionary()) : null;
                        Push(new(value)); return true;
                    }
                case OpCode.GetMember:
                    {
                        KnownValue receiver = Pop();
                        Value? value = receiver.Value is ScopeValue scope ? ReadKnownMember(scope, instruction.Text!) : null;
                        return PushComputed(value, [receiver]);
                    }
                case OpCode.SetMember:
                    {
                        KnownValue value = Pop(), receiver = Pop();
                        if (receiver.Value is not ScopeValue scope) return Stop();
                        if (scope.TryGetFieldOffset(instruction.Text!, out int offset) && scope.TryGetFieldCell(offset, out var cell))
                            SetKnownCell(cell, value.Value);
                        else
                        {
                            if (!fields.TryGetValue(scope, out var current)) fields.Add(scope, current = new(StringComparer.Ordinal));
                            current[instruction.Text!] = value.Value;
                        }
                        Push(new(Value.Null)); return true;
                    }
                case OpCode.SelectPath: return Select(instruction);
                case OpCode.NativeCall: return Native(instruction);
                case OpCode.Transfer:
                case OpCode.LocalTransfer: return Transfer(instruction);
                case OpCode.Jump:
                    if (instruction.A <= pc) return Stop();
                    pc = instruction.A - 1; return true;
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                    {
                        KnownValue condition = Pop();
                        if (condition.Value is not BoolValue boolean) return Stop();
                        if (instruction.A <= pc) return Stop();
                        int pops = RemoveInputs([condition]);
                        bool selected = boolean.Data == (instruction.Op == OpCode.JumpIfTrue);
                        var output = Enumerable.Repeat(new Instruction(OpCode.Pop), pops).ToList();
                        if (selected) output.Add(instruction with { Op = OpCode.Jump });
                        Replace(pc, output.Count == 0 ? [new(OpCode.Nop)] : output.ToArray());
                        if (selected)
                        {
                            if (instruction.A <= pc) return Stop();
                            pc = instruction.A - 1;
                        }
                        return true;
                    }
                case OpCode.Barrier when instruction.B == 0 && instruction.Text is null:
                case OpCode.Acquire when instruction.A == 0:
                    markers.Add(pc); return true;
                case OpCode.Not:
                case OpCode.IsNull:
                case OpCode.Negate:
                    {
                        KnownValue value = Pop();
                        return PushComputed(value.Known ? KnownEvaluation.Unary(instruction.Op, value.Value!) : null, [value]);
                    }
                case OpCode.Equal:
                case OpCode.NotEqual:
                case OpCode.Less:
                case OpCode.LessEqual:
                case OpCode.Greater:
                case OpCode.GreaterEqual:
                case OpCode.Add:
                case OpCode.Subtract:
                case OpCode.Multiply:
                case OpCode.Divide:
                case OpCode.Remainder:
                case OpCode.IsType:
                    {
                        KnownValue right = Pop(), left = Pop();
                        return PushComputed(left.Known && right.Known
                            ? KnownEvaluation.Binary(instruction.Op, left.Value!, right.Value!) : null, [left, right]);
                    }
                case OpCode.RegexMatch:
                    {
                        KnownValue pattern = instruction.Text is null ? Pop() : new(new StringValue(instruction.Text));
                        KnownValue value = Pop();
                        return PushComputed(value.Known && pattern.Value is StringValue expression
                            ? KnownEvaluation.Regex(value.Value!, expression.Data) : null,
                            instruction.Text is null ? [value, pattern] : [value]);
                    }
                case OpCode.GetIterator: return ExpandLoop();
                default: return Stop();
            }
        }

        private bool PushComputed(Value? value, IReadOnlyList<KnownValue> inputs)
        {
            if (value is null) return Stop();
            if (!KnownEvaluation.Portable(value)) { Push(new(value)); return true; }
            int pops = RemoveInputs(inputs);
            List<Instruction> output = Enumerable.Repeat(new Instruction(OpCode.Pop), pops).ToList();
            int index = constants.Count;
            constants.Add(value);
            output.Add(new(OpCode.Constant, index, Span: code[pc].Span));
            Replace(pc, output.ToArray());
            Push(pops == 0 ? new(value, pc, pc) : new(value));
            return true;
        }

        private bool Native(Instruction instruction)
        {
            if (!NativeSignatures.TryGet(instruction.Text!, out var signature)) return Stop();
            if (NativeEvaluation.DefersFileRead(instruction.Text))
            {
                if (!instruction.Text!.StartsWith("fs.", StringComparison.Ordinal)) return Stop();
                KnownValue path = Peek();
                if (path.Value is not (PathValue or StringValue)) return Stop();
                Pop();
                var selector = new RangeValue(Array.Empty<Value>(), TypeRef.Path)
                { Selection = new(context.AsPath(path.Value), ".*", Recursive: instruction.Text == "fs.recursive_directory_iterator") };
                Push(new(selector, path.Start >= start && path.End == pc - 1 ? path.Start : -1, pc));
                return true;
            }
            bool metadata = IsMetadata(instruction.Text);
            bool pure = signature.Effect == NativeEffect.Pure
                && (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.LocalMemory)) == 0;
            if (!pure && !metadata) return Stop();
            KnownValue[] inputs = PeekMany(instruction.A);
            if (inputs.Any(value => !value.Known)) return Stop();
            Value[] arguments = inputs.Select(value => value.Value!).ToArray();
            if (!NativeEvaluation.CanEnumerate(signature, arguments)) return Stop();
            if (metadata)
            {
                bool follows = instruction.Text is not ("fs.symlink_status" or "fs.is_symlink" or "fs.read_symlink");
                PlanResource[] paths = inputs.Where(value => value.Value is PathValue or StringValue)
                    .SelectMany(value => filesystem.ObservationResources(context.AsPath(value.Value!), follows)).ToArray();
                if (paths.Any(graph.HasWrites)) return Stop(paths);
            }
            using var selectedSession = NativeEvaluation.RequiresPathSession(instruction.Text)
                && inputs.Select(value => value.Value).OfType<PathValue>()
                .FirstOrDefault(path => path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl) is { } selected
                    ? filesystem.SelectSession(selected) : null;
            Value result;
            try { result = filesystem.Invoke(instruction.Text!, arguments); }
            catch (Exception error) when (error is MsyncException or IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            { return Stop(); }
            PopMany(instruction.A);
            return PushComputed(result, inputs);
        }

        private bool Select(Instruction instruction)
        {
            KnownValue input = Peek();
            if (input.Value is not (PathValue or StringValue)) return Stop();
            PathValue path = context.AsPath(input.Value);
            Pop();
            var selector = new RangeValue(Array.Empty<Value>(), TypeRef.Path)
            { Selection = new(path, instruction.Text!) };
            Push(new(selector, input.Start >= start && input.End == pc - 1 ? input.Start : -1, pc));
            return true;
        }

        private bool Transfer(Instruction instruction)
        {
            KnownValue destination = Peek(), source = Peek(1);
            if (source.Value is RangeValue { Selection: { Apply: null } selection })
            {
                var resources = filesystem.ObservationResources(selection.Directory);
                RangeValue snapshot;
                if (resources.Any(graph.HasWrites))
                {
                    if (!PlanNamespaceProjection.TrySelect(graph, selection.Directory, selection.Pattern, filesystem, out snapshot!))
                        return Stop(resources);
                }
                else snapshot = filesystem.Select(selection.Directory, selection.Pattern, selection.Recursive);
                source = source with { Value = snapshot };
            }
            if (destination.Value is not (PathValue or StringValue) || source.Value is not
                (PathValue or StringValue or RangeValue { SnapshotValues: not null })) return Stop();
            PathValue target = context.AsPath(destination.Value);
            bool observed = pc + 1 >= code.Count || code[pc + 1].Op != OpCode.Pop;
            if (source.Value is RangeValue { SnapshotValues.Count: 0 })
                return RegisterTransfer(source, destination, observed);
            PlanResource targetResource = filesystem.Resource(target);
            if (graph.NamespaceMayChange(targetResource) && !filesystem.IsProjected(target)) return Stop([targetResource]);
            if (source.Value is PathValue sourcePath && graph.NamespaceMayChange(filesystem.Resource(sourcePath)))
                if (!filesystem.IsProjected(sourcePath)) return Stop([filesystem.Resource(sourcePath)]);
            if (!AddTransfer(source.Value, target, instruction.Span, observed, pc)) return Stop();
            return RegisterTransfer(source, destination, observed);
        }

        private bool RegisterTransfer(KnownValue source, KnownValue destination, bool observed)
        {
            Pop(); Pop();
            sites.Add(new(pc, [source, destination], observed));
            if (observed)
            {
                continuation = pc + 1;
                Flush();
                return false;
            }
            pc++;
            return true;
        }

        private bool ExpandLoop()
        {
            KnownValue input = Peek();
            if (input.Value is not RangeValue range) return Stop();
            RangeValue snapshot = range;
            if (range.Selection is { Apply: null } selection)
            {
                var resources = filesystem.ObservationResources(selection.Directory);
                if (resources.Any(graph.HasWrites))
                {
                    if (!PlanNamespaceProjection.TrySelect(graph, selection.Directory, selection.Pattern, filesystem, out snapshot!))
                        return Stop(resources);
                }
                else snapshot = filesystem.Select(selection.Directory, selection.Pattern, selection.Recursive);
            }
            if (snapshot.SnapshotValues is not { } values) return Stop();
            if (TryExpandLoop(input, values)) return true;
            if (ReferenceEquals(range, snapshot)) return Stop();
            Pop();
            int pops = RemoveInputs([input]);
            int index = constants.Count;
            constants.Add(snapshot);
            List<Instruction> output = Enumerable.Repeat(new Instruction(OpCode.Pop), pops).ToList();
            output.Add(new(OpCode.Constant, index, Span: code[pc].Span));
            output.Add(code[pc]);
            Replace(pc, output.ToArray());
            return Stop();
        }

        private bool TryExpandLoop(KnownValue input, IReadOnlyList<Value> values)
        {
            if (pc + 2 >= code.Count || code[pc + 1].Op is not (OpCode.StoreLocal or OpCode.InitializeLocal)
                || code[pc + 2].Op != OpCode.IteratorNext) return false;
            int header = pc + 2, exit = code[header].B, iterator = code[header].A;
            if (exit <= header || exit > code.Count || code[exit - 1] is not { Op: OpCode.Jump } back
                || back.A != header) return false;
            KnownLoop.Result? evaluated = new KnownLoop(module, code, constants, locals, context, filesystem,
                ResolveKnownCapture, ReadKnownMember,
                header + 1, header, exit, iterator).Run(values, cancellationToken);
            if (evaluated is null) return false;
            filesystem.Prefetch(evaluated.Copies.SelectMany(copy => SourcePaths(copy.Source)), includeKinds: true);
            filesystem.Prefetch(evaluated.Copies.SelectMany(copy => FinalTargets(copy.Source, copy.Destination)), includeKinds: false);
            var loopGraph = new TransferGraph();
            foreach (var copy in evaluated.Copies)
            {
                PlanResource target = filesystem.Resource(copy.Destination);
                if (loopGraph.NamespaceMayChange(target) || graph.NamespaceMayChange(target)
                    && !filesystem.IsProjected(copy.Destination)) return false;
                if (copy.Source is PathValue path && (loopGraph.NamespaceMayChange(filesystem.Resource(path))
                    || graph.NamespaceMayChange(filesystem.Resource(path)) && !filesystem.IsProjected(path))) return false;
                if (copy.Source is PathValue directory && filesystem.Kind(directory) == "directory"
                    && loopGraph.HasWrites(filesystem.Resource(directory, true))) return false;
                if (!AddTransfer(copy.Source, copy.Destination, copy.Span, false, copy.Instruction, loopGraph)) return false;
            }
            List<Instruction> restores = [];
            foreach (var store in evaluated.Stores.OrderBy(pair => pair.Key))
            {
                Value value = evaluated.Locals[store.Key].Value!;
                int index = constants.Count; constants.Add(value);
                restores.Add(new(OpCode.Constant, index));
                restores.Add(new(store.Value ? OpCode.InitializeLocal : OpCode.StoreLocal, store.Key));
                SetKnownLocal(store.Key, value, store.Value);
            }
            Pop();
            foreach (var node in loopGraph.Nodes) graph.Add(node);
            if (loopGraph.Count != 0)
                sites.Add(new(pc, [input], false, exit, restores));
            else
            {
                int pops = RemoveInputs([input]);
                List<Instruction> output = Enumerable.Repeat(new Instruction(OpCode.Pop), pops).ToList();
                output.AddRange(restores);
                Replace(pc, output.Count == 0 ? [new(OpCode.Nop)] : output.ToArray());
            }
            for (int index = pc + 1; index < exit; index++) Replace(index, [new(OpCode.Nop)]);
            pc = exit - 1;
            return true;
        }

        private bool AddTransfer(Value source, PathValue destination, SourceSpan? span, bool observed, int instruction,
            TransferGraph? outputGraph = null)
        {
            TransferGraph targetGraph = outputGraph ?? graph;
            bool cohort = source is RangeValue;
            IReadOnlyList<Value> values = source is RangeValue { SnapshotValues: { } snapshot } ? snapshot : [source];
            if (values.Count == 0) return true;
            bool container = cohort || destination.Raw.EndsWith('/')
                || PathSemantics.UsesWindowsPaths(destination) && destination.Raw.EndsWith('\\');
            bool replace = destination.Raw.EndsWith("/.", StringComparison.Ordinal)
                || PathSemantics.UsesWindowsPaths(destination) && destination.Raw.EndsWith("\\.", StringComparison.Ordinal);
            if (container && values.Any(value => value is not PathValue)) return false;
            if (cohort && values.OfType<PathValue>().Select(PathSemantics.FileName).Distinct(StringComparer.Ordinal).Count() != values.Count)
                return false;
            PathValue root = replace ? ReplacementRoot(destination) : destination;
            filesystem.Prefetch(values.OfType<PathValue>(), includeKinds: true);
            filesystem.Prefetch(FinalTargets(source, destination), includeKinds: false);
            if (cohort && replace)
            {
                if (AddDirectoryCohortReplacement((RangeValue)source, root, span, observed, instruction, targetGraph)) return true;
                PlanResource[] reads = values.Cast<PathValue>().SelectMany(path => filesystem.ObservationResources(path)).ToArray();
                targetGraph.Add(new(source, destination, PlannedTransferKind.GenericCopy, reads,
                    [filesystem.Resource(root, true)], span, true, observed, instruction, TransferDestinationMode.Replace));
                return true;
            }
            foreach (Value value in values)
            {
                PathValue target = filesystem.Target(root, value, container && !replace);
                string kind = value is PathValue path
                    ? targetGraph.ProducedKind(filesystem.Resource(path)) ?? graph.ProducedKind(filesystem.Resource(path))
                        ?? filesystem.Kind(path) : "literal";
                PlannedTransferKind operation = kind switch
                {
                    "literal" => PlannedTransferKind.WriteText,
                    "regular" or "not_found" => PlannedTransferKind.CopyFile,
                    "directory" => PlannedTransferKind.CopyDirectory,
                    "symlink" => PlannedTransferKind.CopyLink,
                    _ => PlannedTransferKind.GenericCopy
                };
                var mode = replace ? TransferDestinationMode.Replace
                    : container ? TransferDestinationMode.Merge : TransferDestinationMode.Exact;
                if (operation == PlannedTransferKind.CopyDirectory && value is PathValue directory
                    && AddDirectory(directory, target, mode, span, observed, instruction, targetGraph)) continue;
                if (operation == PlannedTransferKind.CopyDirectory) operation = PlannedTransferKind.GenericCopy;
                bool subtree = operation is PlannedTransferKind.CopyDirectory or PlannedTransferKind.GenericCopy;
                PlanResource write = filesystem.Resource(target, subtree,
                    operation == PlannedTransferKind.CopyDirectory && !replace);
                List<PlanResource> reads = value is PathValue original
                    ? [filesystem.Resource(original, subtree), .. filesystem.NamespaceResources(original)] : [];
                reads.AddRange(filesystem.NamespaceResources(target));
                IReadOnlyList<PlanResource> writes = subtree && value is PathValue tree
                    ? filesystem.DirectoryWrites(tree, target, mode) : [write];
                targetGraph.Add(new(value, target, operation, reads.Distinct(ResourceIdentity.Instance).ToArray(), writes,
                    span, replace || !subtree, observed, instruction, mode));
            }
            return true;
        }

        private static PathValue ReplacementRoot(PathValue path)
        {
            string root = path.Raw[..^2];
            if (root.Length == 0) root = "/";
            if (PathSemantics.UsesWindowsPaths(path) && root.Length == 2 && root[1] == ':') root += "\\";
            return path.WithRaw(root);
        }

        private IReadOnlyList<RemotePathMetadata> PreparedTree(PathValue source)
        {
            var observations = filesystem.ObservationResources(source);
            if (!observations.Any(graph.HasWrites)) return filesystem.Tree(source);
            if (PlanNamespaceProjection.TryTree(graph, source, filesystem, out var tree)) return tree!;
            throw new IOException($"The source tree '{source}' depends on a namespace operation that has not executed yet.");
        }

        private static IEnumerable<PathValue> SourcePaths(Value source) => source is PathValue path ? [path]
            : source is RangeValue { SnapshotValues: { } values } ? values.OfType<PathValue>() : [];

        private IEnumerable<PathValue> FinalTargets(Value source, PathValue destination)
        {
            bool container = source is RangeValue || destination.Raw.EndsWith('/')
                || PathSemantics.UsesWindowsPaths(destination) && destination.Raw.EndsWith('\\');
            bool replace = destination.Raw.EndsWith("/.", StringComparison.Ordinal)
                || PathSemantics.UsesWindowsPaths(destination) && destination.Raw.EndsWith("\\.", StringComparison.Ordinal);
            if (replace) return [ReplacementRoot(destination)];
            return container ? SourcePaths(source).Select(value => filesystem.Target(destination, value, true)) : [destination];
        }

        private void Flush()
        {
            if (sites.Count == 0) return;
            int index = plans.Count;
            int begin = sites[0].Instruction, end = sites[^1].LoopEnd
                ?? sites[^1].Instruction + (sites[^1].Observed ? 1 : 2);
            graph = FileVersionForwarding.Apply(graph, filesystem);
            ExecutionPlan plan = graph.Build(function, begin, end);
            if (sites.Any(site => site.Observed) && plan.ResultTransfers is null) plan = plan with { ResultTransfers = [] };
            plans.Add(plan);
            for (int item = 0; item < sites.Count; item++)
            {
                Site site = sites[item];
                int pops = RemoveInputs(site.Inputs);
                List<Instruction> output = [];
                if (item == 0) output.Add(new(OpCode.ExecutePlan, index, pops, Span: code[site.Instruction].Span));
                else if (!site.Observed) output.AddRange(Enumerable.Repeat(new Instruction(OpCode.Pop), pops));
                if (site.Observed) output.Add(new(OpCode.PlanResult, index, item == 0 ? 0 : pops, Span: code[site.Instruction].Span));
                if (site.Restores is not null) output.AddRange(site.Restores);
                Replace(site.Instruction, output.Count == 0 ? [new(OpCode.Nop)] : output.ToArray());
                if (!site.Observed && site.LoopEnd is null) Replace(site.Instruction + 1, [new(OpCode.Nop)]);
            }
            foreach (int marker in markers)
                if (marker > begin && marker < end) Replace(marker, [new(OpCode.Nop)]);
            sites.Clear(); markers.Clear(); graph = new();
        }

        private ContextualPreparation Rewrite()
        {
            if (replacements.Count == 0)
                return new(module.ExecutionPlans is null ? module with { ExecutionPlans = [] } : module, continuation, awaitResources);
            int[] positions = new int[code.Count + 1];
            List<Instruction> output = [];
            List<int> sources = [];
            for (int index = 0; index < code.Count; index++)
            {
                positions[index] = output.Count;
                Instruction[] rewritten = (replacements.GetValueOrDefault(index) ?? [code[index]])
                    .Where(instruction => instruction.Op != OpCode.Nop).ToArray();
                output.AddRange(rewritten);
                sources.AddRange(Enumerable.Repeat(index, rewritten.Length));
            }
            positions[^1] = output.Count;
            sources.Add(code.Count);
            for (int index = 0; index < output.Count; index++)
            {
                Instruction instruction = output[index];
                output[index] = instruction.Op switch
                {
                    OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue => instruction with { A = positions[instruction.A] },
                    OpCode.IteratorNext => instruction with { B = positions[instruction.B] },
                    _ => instruction
                };
            }
            for (int index = module.ExecutionPlans?.Count ?? 0; index < plans.Count; index++)
                plans[index] = plans[index] with { Start = positions[plans[index].Start], End = positions[plans[index].End] };
            var functions = new PreparationFunctions(module.Functions, function,
                module.Functions[function] with { Instructions = output });
            Module prepared = module with
            {
                Constants = constants,
                Functions = functions,
                ExecutionPlans = plans,
                Blocks = null
            };
            bool identity = sources.Count == code.Count + 1;
            for (int index = 0; identity && index < sources.Count; index++) identity &= sources[index] == index;
            return new(prepared, continuation is { } next ? positions[next] : null, awaitResources,
                identity ? null : sources);
        }

        private bool Stop(IReadOnlyList<PlanResource>? resources = null)
        {
            continuation = pc;
            awaitResources = resources ?? [];
            return false;
        }

        private int RemoveInputs(IReadOnlyList<KnownValue> inputs)
        {
            int pops = 0;
            foreach (KnownValue input in inputs)
            {
                bool removable = input.Start >= start && input.End >= input.Start
                    && !stack.Any(value => value.Start >= 0 && value.Start <= input.End && value.End >= input.Start);
                if (!removable) { pops++; continue; }
                for (int index = input.Start; index <= input.End; index++) Replace(index, [new(OpCode.Nop)]);
            }
            return pops;
        }

        private void Replace(int instruction, Instruction[] output) => replacements[instruction] = output;
        private void Push(KnownValue value) => stack.Add(value);
        private KnownValue Peek(int distance = 0) => stack.Count > distance ? stack[^(distance + 1)] : KnownValue.Unknown;
        private KnownValue Pop()
        {
            if (stack.Count == 0) return KnownValue.Unknown;
            KnownValue result = stack[^1]; stack.RemoveAt(stack.Count - 1); return result;
        }
        private KnownValue[] PopMany(int count)
        {
            var result = PeekMany(count);
            for (int index = 0; index < count; index++) Pop();
            return result;
        }
        private KnownValue[] PeekMany(int count)
        {
            var result = new KnownValue[count];
            for (int index = 0; index < count; index++) result[index] = Peek(count - index - 1);
            return result;
        }
        private static Value Read(Value value) => value is CellValue cell ? cell.Data : value;
        private Value? ReadKnownMember(ScopeValue scope, string name)
        {
            if (!scope.TryGetFieldOffset(name, out int offset)) return null;
            if (scope.TryGetFieldCell(offset, out var cell)) return ResolveKnownCapture(cell);
            return fields.TryGetValue(scope, out var current) && current.TryGetValue(name, out var changed)
                ? changed : scope.GetField(offset);
        }
        private Value? Builtin(string? name) => name?.TrimStart('$') switch
        {
            "local" => new ConnectionValue(ConnectionKind.Local),
            "pwd" => new ConnectionValue(ConnectionKind.Local, "pwd"),
            "file" => new ConnectionValue(ConnectionKind.Local, "file"),
            "wsl" => new ConnectionValue(ConnectionKind.Wsl),
            "env" => new ConnectionValue(ConnectionKind.Environment),
            "path" => new ConnectionValue(ConnectionKind.SearchPath),
            _ => null
        };
        private static Value[] Materialize(RangeValue range)
        {
            List<Value> values = [];
            var iterator = range.Items.GetAsyncEnumerator();
            try
            {
                while (iterator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) values.Add(iterator.Current);
            }
            finally { iterator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return values.ToArray();
        }
    }
}
