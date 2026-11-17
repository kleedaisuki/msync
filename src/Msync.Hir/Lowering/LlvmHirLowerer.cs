using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public sealed record LlvmHirLoweringResult(LlvmModule Module,
    IReadOnlyDictionary<LlvmGlobalVariable, NativeConstant> Constants,
    IReadOnlyDictionary<LlvmFunction, NativeBinary> RuntimeCalls,
    LlvmFunction Entry,
    IReadOnlyList<NativeCodeType> Types,
    IReadOnlyList<NativeCodeLayout> Layouts,
    IReadOnlyDictionary<LlvmStructType, int> StructLayouts,
    IReadOnlyDictionary<LlvmFunction, LlvmIteratorOperation> IteratorCalls,
    IReadOnlyDictionary<LlvmFunction, LlvmSourceFunctionInfo> SourceFunctions,
    IReadOnlyDictionary<LlvmFunction, LlvmTemplateFunctionInfo> TemplateFunctions,
    IReadOnlyDictionary<LlvmFunction, string> NativeCalls,
    IReadOnlyDictionary<LlvmFunction, NativeKind> Constructors,
    IReadOnlyDictionary<LlvmFunction, LlvmCallableRuntimeBinding> CallableCalls)
{
    public IReadOnlyList<LlvmStaticScopeInfo> StaticScopes { get; init; } = [];
    public IReadOnlyList<LlvmSessionCellInfo> SessionCells { get; init; } = [];
    public IReadOnlyList<LlvmSessionDispatchInfo> SessionDispatches { get; init; } = [];
    internal LlvmHirLoweringSession.Generation? SessionGeneration { get; init; }
    public IReadOnlyDictionary<LlvmFunction, LlvmRuntimeValueBinding> ValueOperations { get; init; } =
        new Dictionary<LlvmFunction, LlvmRuntimeValueBinding>();
    public IReadOnlyDictionary<LlvmFunction, LlvmRuntimeConversionBinding> RuntimeConversions { get; init; } =
        new Dictionary<LlvmFunction, LlvmRuntimeConversionBinding>();
}

public sealed record LlvmStaticScopeInfo(LlvmGlobalVariable Object, LlvmGlobalVariable Initialized,
    int Layout, int Declaration);

public enum LlvmCallableRuntimeOperation : byte { Invoke, Adapt }
public sealed record LlvmCallableRuntimeBinding(LlvmCallableRuntimeOperation Operation,
    IReadOnlyDictionary<LlvmFunction, LlvmFunction> Targets)
{
    public int SessionSlot { get; init; } = -1;
}

public sealed record LlvmSourceFunctionInfo(int CaptureCount, int ResultType, bool IsTemplate = false)
{
    public HirFunctionId? SourceFunction { get; init; }
    public string? SourceName { get; init; }
}
public sealed record LlvmTemplateFunctionInfo(IReadOnlyList<int> Parameters, IReadOnlyList<int> Captures)
{
    public HirTemplateId? SourceTemplate { get; init; }
    public string? SourceName { get; init; }
}

public sealed class LlvmHirLoweringException(HirSourceRange source, string feature)
    : NotSupportedException($"LLVM HIR lowering does not yet implement {feature} at source {source.Source.Value}:{source.Start}.")
{
    public HirSourceRange SourceRange { get; } = source;
}

public static partial class LlvmHirLowerer
{
    public static LlvmHirLoweringResult Lower(HirModule module, CompilationTrace? trace = null)
    {
        var lowering = new Lowering(module, trace);
        return trace is null ? lowering.Run() : trace.Measure("compiler.llvm.lower", lowering, static state => state.Run());
    }

    private sealed partial class Lowering(HirModule hir, CompilationTrace? trace, LlvmHirLoweringSession.Generation? generation = null)
    {
        internal readonly HirModule hir = hir;
        internal readonly LlvmNativeMetadata metadata = generation?.Metadata ?? new(hir);
        internal readonly LlvmHirLoweringSession.Generation? session = generation;
        internal readonly Dictionary<LlvmStructType, int> structLayouts = [];
        internal readonly CompilationTrace? trace = trace;
        internal readonly LlvmContext context = new();
        internal readonly Dictionary<LlvmGlobalVariable, NativeConstant> constants = [];
        internal readonly Dictionary<LlvmFunction, NativeBinary> runtimeCalls = [];
        internal readonly Dictionary<LlvmFunction, LlvmIteratorOperation> iteratorCalls = [];
        private readonly Dictionary<int, LlvmStructType> layouts = [];
        internal readonly Dictionary<int, LlvmFunction> functions = [];
        internal readonly Dictionary<LlvmFunction, LlvmSourceFunctionInfo> sourceFunctions = [];
        internal readonly Dictionary<LlvmFunction, LlvmTemplateFunctionInfo> templateFunctions = [];
        private readonly List<int> pendingFunctions = [];
        internal readonly HashSet<HirLocationId> captured = Captured(hir);
        internal readonly HashSet<HirLocationId> fields = hir.Scopes.SelectMany(scope => scope.Fields).ToHashSet();
        internal LlvmModule module = null!;
        private LlvmStructType NullType => LlvmStructType.Get(context);
        internal LlvmConstant Null => LlvmConstantAggregateZero.Get(NullType);
        private LlvmIntegerType I64 => LlvmIntegerType.Get(context, 64);
        internal LlvmPointerType CodePointer => LlvmPointerType.Get(context, module.DataLayout.GetProgramAddressSpace());
        internal LlvmPointerType DataPointer => LlvmPointerType.Get(context, module.DataLayout.GetDefaultGlobalsAddressSpace());
        internal LlvmConstantInt Int(ulong value) => LlvmConstantInt.Get(I64, value);

        internal LlvmHirLoweringResult Run()
        {
            module = new("msync.hir", context)
            {
                DataLayout = new("e-p:64:64:64:64-peu1:64:64:64:64-A1-G1")
            };
            trace?.Detail?.Metric("layout.code_address_space", module.DataLayout.GetProgramAddressSpace());
            trace?.Detail?.Metric("layout.managed_address_space", DataPointer.AddressSpace);
            trace?.Detail?.Metric("layout.managed_external_state", module.DataLayout.HasExternalState(DataPointer) ? 1 : 0);
            trace?.Detail?.Metric("layout.managed_unstable_representation", module.DataLayout.HasUnstableRepresentation(DataPointer) ? 1 : 0);
            // Only reachable bodies are materialized; generic template bodies are not executable HIR.
            var entry = Reserve(hir.Entry);
            var sessionDispatches = PrepareSession();
            for (int index = 0; index < pendingFunctions.Count; index++) LowerFunction(pendingFunctions[index]);
            return new(module, constants, runtimeCalls, entry, metadata.Types, metadata.Layouts, structLayouts, iteratorCalls, sourceFunctions, templateFunctions, nativeCalls, constructors, callableCalls) { RuntimeConversions = runtimeConversions, ValueOperations = valueOperations, StaticScopes = staticScopes.Values.ToArray(), SessionGeneration = session,
                SessionCells = sessionCells.Values.Select(cell => cell.Binding).ToArray(), SessionDispatches = sessionDispatches };
        }

        internal LlvmFunction Reserve(HirFunctionId id)
        {
            if (functions.TryGetValue(id.Value, out var existing)) return existing;
            var function = hir.Functions[id.Value];
            var signature = LlvmFunctionType.Get(Type(function.Result),
                function.Captures.Select(_ => (LlvmType)DataPointer)
                    .Concat(function.Parameters.Select((location, index) => Type(function.ParameterEntryTypes.IsDefaultOrEmpty
                        ? hir.Locations[location.Value].StorageType : function.ParameterEntryTypes[index],
                        function.ParameterEntryTypes.IsDefaultOrEmpty && hir.Locations[location.Value].AdmitsNull))).ToArray());
            string symbol = function.Name.StartsWith("llvm.", StringComparison.Ordinal)
                || function.Name.StartsWith("__llvm_", StringComparison.Ordinal)
                ? "msync.source." + function.Name : function.Name;
            var result = LlvmFunction.Create(signature, LlvmLinkage.InternalLinkage, symbol, module);
            functions.Add(id.Value, result);
            pendingFunctions.Add(id.Value);
            sourceFunctions.Add(result, new(function.Captures.Length, metadata.Type(function.Result)) { SourceFunction = id, SourceName = function.Name });
            return result;
        }

        internal LlvmStructType FunctionType() => LlvmStructType.GetTypeByName(context, "msync.function") ??
            LlvmStructType.Create(context, [CodePointer, DataPointer, DataPointer], "msync.function");

        private LlvmStructType PathType() => LlvmStructType.GetTypeByName(context, "msync.path") ??
            LlvmStructType.CreateNativeRecord(context, [LlvmIntegerType.Get(context, 8), LlvmIntegerType.Get(context, 8),
                DataPointer, DataPointer, DataPointer, DataPointer, DataPointer], "msync.path");

        internal LlvmStructType Layout(int index)
        {
            if (layouts.TryGetValue(index, out var result)) return result;
            result = LlvmStructType.Create(context, "msync.scope." + index);
            layouts.Add(index, result);
            result.SetBody(hir.ScopeLayouts[index].Fields.Select(field => Type(field.StorageType, field.AdmitsNull)).ToArray());
            int physical = metadata.Layout(index);
            structLayouts.Add(result, physical);
            scopeLayoutsByPhysicalId.TryAdd(physical, result);
            return result;
        }

        private void LowerFunction(int index)
        {
            var body = new Body(this, hir.Functions[index], functions[index]);
            if (trace is null) body.Run();
            else trace.Run("compiler.llvm.lower.function", body, static state => state.Run(),
                new Dictionary<string, string> { ["function"] = hir.Functions[index].Name });
        }
    }

    private sealed partial class Body(Lowering owner, HirFunction source, LlvmFunction function)
    {
        private readonly Dictionary<HirLocationId, LlvmValue> locations = [];
        private LlvmBasicBlock block = null!;
        private HirModule Hir => owner.hir;
        private HirNode current;
        private LlvmBasicBlock entry = null!;
        private readonly Dictionary<LlvmOpcode, long>? opcodeCounts = owner.trace?.Detail is null ? null : [];

        internal void Run()
        {
            block = new(owner.context, "entry", function);
            entry = block;
            current = Hir.Node(source.Body);
            for (int index = 0; index < source.Captures.Length; index++)
            {
                var capture = source.Captures[index];
                var argument = function.Arguments[index];
                argument.LogicalType = owner.metadata.Pointer(owner.metadata.Type(Hir.Locations[capture.Target.Value].StorageType, Hir.Locations[capture.Target.Value].AdmitsNull));
                locations.Add(capture.Target, argument);
            }
            for (int index = 0; index < source.Parameters.Length; index++)
            {
                var location = source.Parameters[index];
                var address = Address(location);
                var argument = function.Arguments[source.Captures.Length + index];
                argument.LogicalType = owner.metadata.Type(source.ParameterEntryTypes.IsDefaultOrEmpty
                    ? Hir.Locations[location.Value].StorageType : source.ParameterEntryTypes[index],
                    source.ParameterEntryTypes.IsDefaultOrEmpty && Hir.Locations[location.Value].AdmitsNull);
                Add(new LlvmStoreInstruction(Represent(argument, Hir.Locations[location.Value].StorageType,
                    Hir.Locations[location.Value].AdmitsNull), address));
            }
            var value = Emit(source.Body);
            if (!block.HasTerminator) Add(new LlvmReturnInstruction(owner.context, Represent(value, source.Result)));
            owner.trace?.Metric("instructions", function.BasicBlocks.Sum(item => item.Instructions.Count));
            owner.trace?.Metric("blocks", function.BasicBlocks.Count);
            owner.trace?.Metric("locations", locations.Count);
            owner.trace?.Metric("control.loops", loopCount);
            owner.trace?.Metric("control.breaks", breakCount);
            owner.trace?.Metric("control.continues", continueCount);
            owner.trace?.Metric("control.fors", forCount);
            owner.trace?.Metric("control.iterator_closes", iteratorCloseCount);
            owner.trace?.Metric("closures", closureCount);
            owner.trace?.Metric("closure.cells", captureCellCount);
            owner.trace?.Metric("statics.access_sites", staticAccessCount);
            owner.trace?.Metric("statics.initializer_paths", staticInitializerCount);
            owner.trace?.Metric("members.direct", directMemberCount);
            owner.trace?.Metric("members.dispatched", dispatchedMemberCount);
            owner.trace?.Metric("members.layout_tests", memberLayoutTestCount);
            owner.trace?.Metric("members.process", processMemberCount);
            owner.trace?.Metric("calls.source", sourceCallCount);
            owner.trace?.Metric("calls.dynamic", dynamicCallCount);
            owner.trace?.Metric("calls.adapt", adaptationCount);
            owner.trace?.Metric("calls.callee_bundles", sourceCallCount);
            owner.trace?.Metric("control.max_loop_depth", loopDepth);
            if (conversionCounts is not null)
                foreach (var (operation, count) in conversionCounts) owner.trace!.Metric("representation." + operation.ToString().ToLowerInvariant(), count);
            if (opcodeCounts is not null)
                foreach (var (opcode, count) in opcodeCounts) owner.trace!.Metric("emit." + opcode, count);
        }

        private T Add<T>(T instruction) where T : LlvmInstruction
        {
            foreach (var use in instruction.OperandUses)
                if (use.Value is { LogicalType: >= 0 } value) use.LogicalType = value.LogicalType;
            if (instruction is LlvmStoreInstruction store && store.PointerOperand.LogicalType >= 0)
                instruction.GetOperandUse(0).LogicalType = owner.metadata.Types[store.PointerOperand.LogicalType].Pointee;
            if (instruction is LlvmReturnInstruction { ReturnValue: not null })
                instruction.GetOperandUse(0).LogicalType = owner.metadata.Type(source.Result);
            block.Append(instruction);
            Record(instruction);
            return instruction;
        }

        private void Record(LlvmInstruction instruction)
        {
            instruction.Source = current.Source.Source.Value < 0 ? null :
                new SourceSpan(Hir.Sources[current.Source.Source.Value].Name, current.Source.Start, current.Source.Length);
            if (conversionCounts is not null)
                foreach (var (operation, count) in conversionCounts) owner.trace!.Metric("representation." + operation.ToString().ToLowerInvariant(), count);
            if (opcodeCounts is not null)
                opcodeCounts[instruction.Opcode] = opcodeCounts.GetValueOrDefault(instruction.Opcode) + 1;
        }

        private LlvmValue Address(HirLocationId location)
        {
            if (locations.TryGetValue(location, out var address)) return address;
            if (SessionAddress(location) is { } persistent) return persistent;
            if (owner.captured.Contains(location)) return FreshLocation(location);
            address = StackCell(Hir.Locations[location.Value].StorageType, Hir.Locations[location.Value].AdmitsNull);
            locations.Add(location, address);
            return address;
        }

        private LlvmAllocaInstruction StackCell(HirTypeId type, bool admitsNull = false)
            => StackCell(owner.Type(type, admitsNull), owner.metadata.Type(type, admitsNull));

        private LlvmAllocaInstruction StackCell(LlvmType type, int logicalType)
        {
            var address = new LlvmAllocaInstruction(type, owner.Int(1),
                owner.module.DataLayout.GetAllocaAddressSpace())
                { LogicalType = owner.metadata.Pointer(logicalType), AllocatedLogicalType = logicalType };
            entry.InsertBefore(address, entry.GetTerminatorOrNull());
            Record(address);
            return address;
        }

        private LlvmValue Child(HirNode node, int index) => Emit(Hir.Children(node)[index]);
        private LlvmValue Emit(HirNodeId id)
        {
            if (id.IsNone) return owner.Null;
            var node = Hir.Node(id);
            var parent = current;
            current = node;
            try
            {
            var value = node.Kind switch
            {
                HirKind.Literal => Literal(node),
                HirKind.PatternRegion => PatternRegion(node),
                HirKind.PatternDemand => PatternDemand(node),
                HirKind.PatternRead => ReadPatternItem(node),
                HirKind.ReadLocation or HirKind.ReadStorage => ReadLocation(node),
                HirKind.Declare or HirKind.Assign => Store(node),
                HirKind.Discard => Discard(node),
                HirKind.Block => Block(node),
                HirKind.Unary => Unary(node),
                HirKind.Binary => Binary(node),
                HirKind.If => If(node),
                HirKind.Match => Match(node),
                HirKind.RegexMatch => Regex(node),
                HirKind.Qualify or HirKind.QualifyOn => ValuePair(node),
                HirKind.SelectPath => SelectPath(node),
                HirKind.Barrier => Barrier(node),
                HirKind.Return => Return(node),
                HirKind.Loop => Loop(node),
                HirKind.Break => Break(node),
                HirKind.Continue => Continue(node),
                HirKind.For => For(node),
                HirKind.Scope or HirKind.AllocateScope => Scope(node),
                HirKind.MemberRead or HirKind.MemberAssign => Member(node),
                HirKind.Coerce or HirKind.Convert => Coerce(node),
                HirKind.IsNull or HirKind.IsType => TypePredicate(node),
                HirKind.TypeValue => TypeValue(new(node.Data)),
                HirKind.TypeOf => TypeValue(Hir.Node(Hir.Children(node)[0]).Type),
                HirKind.Call => Call(node),
                HirKind.Transfer => Transfer(node),
                HirKind.NativeFunction => NativeFunction(node),
                HirKind.Builtin => Builtin(node),
                HirKind.PathJoin => PathJoin(node),
                HirKind.Closure => Closure(node),
                HirKind.AdaptFunction => Adapt(node),
                HirKind.TemplateClosure => TemplateClosure(node),
                HirKind.Range => Range(node),
                _ => throw new LlvmHirLoweringException(node.Source, node.Kind.ToString())
            };
            if (!block.HasTerminator && (value is not LlvmConstant || value is LlvmGlobalValue))
                value.LogicalType = owner.metadata.Type(node.Type);
            return value;
            }
            finally { current = parent; }
        }

        private LlvmValue Store(HirNode node)
        {
            var location = new HirLocationId(node.Data);
            if (node.Kind == HirKind.Declare && owner.captured.Contains(location) && !owner.fields.Contains(location))
                FreshLocation(location);
            var value = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            var storage = Hir.Locations[location.Value];
            Add(new LlvmStoreInstruction(Represent(value, storage.StorageType, storage.AdmitsNull), Address(location)));
            return owner.Null;
        }
        private LlvmValue Discard(HirNode node) { Child(node, 0); return owner.Null; }
        private LlvmValue Block(HirNode node)
        {
            LlvmValue value = owner.Null;
            foreach (var child in Hir.Children(node))
            {
                if (block.HasTerminator) break;
                value = Emit(child);
            }
            return value;
        }
        private LlvmValue Return(HirNode node)
        {
            var value = node.Children.Count == 0 ? owner.Null : Child(node, 0);
            current = node;
            if (block.HasTerminator) return value;
            CloseIterators(0);
            CloseAcquire(0);
            value = Represent(value, source.Result);
            Add(new LlvmReturnInstruction(owner.context, value));
            return value;
        }

    }
}
