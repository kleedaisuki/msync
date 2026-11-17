using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    public static NativeProgram Lower(LlvmHirLoweringResult input, CompilationTrace? trace = null)
    {
        var lowering = new Lowering(input, trace);
        return trace is null ? lowering.Run() : trace.Measure("compiler.llvm.target", lowering, static item => item.Run());
    }

    private static Dictionary<string, string> FunctionAttributes(string name, int index, SourceSpan? source)
    {
        var attributes = new Dictionary<string, string>
        {
            ["function.name"] = name,
            ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (source is { } span)
        {
            attributes["source.file"] = span.Source;
            attributes["source.offset"] = span.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }

    private sealed partial class Lowering(LlvmHirLoweringResult input, CompilationTrace? trace)
    {
        internal readonly LlvmHirLoweringResult Input = input;
        internal readonly List<NativeCodeType> Types = input.Types.ToList();
        internal readonly List<NativeCodeLayout> Layouts = input.Layouts.ToList();
        internal readonly List<NativeConstant> Constants = input.SessionGeneration?.Program?.Constants.ToList() ?? [];
        internal readonly List<NativeAllocationPlan> Allocations = input.SessionGeneration?.Program?.Allocations.ToList() ?? [];
        internal readonly List<NativeProjection> Projections = input.SessionGeneration?.Program?.Projections.ToList() ?? [];
        internal readonly Dictionary<NativeProjection, int> ProjectionIndices = [];
        internal readonly Dictionary<LlvmFunction, int> Functions = [];
        internal readonly Dictionary<LlvmGlobalVariable, int> StaticObjects = [];
        internal readonly Dictionary<LlvmGlobalVariable, int> StaticFlags = [];
        private readonly Dictionary<TypeKind, int> primitiveTypes = [];
        private Dictionary<int, int>? pointerTypes;

        internal NativeProgram Run()
        {
            var statics = PrepareStatics();
            LlvmFunction[] bodies = Input.Module.Functions.Where(function => !function.IsDeclaration || Input.TemplateFunctions.ContainsKey(function)).ToArray();
            for (int i = 0; i < bodies.Length; ++i) Functions.Add(bodies[i], CallableIndex(bodies[i], i));
            var functions = Session?.Program?.Functions.ToArray() ?? [];
            Array.Resize(ref functions, Session?.Map.NextCallable ?? bodies.Length);
            layoutVariantBase = functions.Length;
            if (trace is null) PrepareLayoutArguments();
            else trace.Run("compiler.llvm.target.layout_arguments", this, static owner => owner.PrepareLayoutArguments());
            var replaced = Functions.Values.ToHashSet();
            Allocations.RemoveAll(plan => replaced.Contains(plan.Function));
            for (int i = 0; i < bodies.Length; ++i)
            {
                int index = Functions[bodies[i]];
                if (Input.TemplateFunctions.TryGetValue(bodies[i], out var template))
                {
                    functions[index] = trace is null ? Template(bodies[i], template, null)
                        : trace.MeasureDetailed("compiler.llvm.target.function", (owner: this, function: bodies[i], template, detail: trace.Detail),
                            static state => state.owner.Template(state.function, state.template, state.detail),
                            attributes: FunctionAttributes(bodies[i].Name, index, null));
                    continue;
                }
                var body = new Body(this, bodies[i], index, trace?.Detail);
                functions[index] = trace is null ? body.Run() : trace.MeasureDetailed("compiler.llvm.target.function", body,
                    static state => state.Run(), attributes: FunctionAttributes(bodies[i].Name, index, bodies[i].BasicBlocks.FirstOrDefault()?.FirstInstruction?.Source));
                if (Session is null && SelectSsa(bodies[i], functions[index], index) is { } ssa)
                    functions[index] = functions[index] with { SsaBody = ssa };
            }
            functions = EmitLayoutVariants(functions);
            for (int i = 0; Session is null && i < functions.Length; i++)
            {
                if (functions[i].SsaBody is not { } plan) continue;
                if (plan.Calls.Any(call => functions[call.Target] is not { CaptureCount: 0, SsaBody.CanBeCalled: true }))
                {
                    functions[i] = functions[i] with { SsaBody = null };
                    trace?.Metric("ssa.calls.unlinked_functions", 1);
                }
                else trace?.Metric("ssa.calls.linked_sites", plan.Calls.Count());
            }
            trace?.Metric("functions", functions.Length);
            trace?.Metric("constants", Constants.Count);
            trace?.Metric("types", Types.Count);
            trace?.Metric("allocations", Allocations.Count);
            trace?.Metric("statics", statics.Length);
            NativeProgram program = new(Constants.ToArray(), functions, Types.ToArray(), Layouts.ToArray(), Functions[Input.Entry])
            {
                Allocations = Allocations.ToArray(), Statics = statics, Projections = Projections.ToArray(),
                PreserveLocals = Session is not null,
                SessionRoots = Session is { } active
                    ? active.Exports.Select(location => active.Roots[location].Slot).Concat(active.ImportSlots).Distinct().ToArray()
                    : null,
                SessionShadowPaths = Session?.ShadowPaths,
                SessionDispatches = DispatchTables()
            };
            return Session is null ? program : program with { Backend = Backend(program, trace) };
        }

        private NativeSsaBody? SelectSsa(LlvmFunction function, NativeFunctionCode linked, int index)
        {
            if (trace?.Detail is not { } detail) return LlvmSsaBody.Select(Input, function, linked, Types, Functions, null);
            return detail.MeasureDetailed("compiler.llvm.target.whole_ssa", (Owner: this, function, linked, detail),
                static state => LlvmSsaBody.Select(state.Owner.Input, state.function, state.linked, state.Owner.Types, state.Owner.Functions, state.detail),
                attributes: FunctionAttributes(linked.Name, index, function.BasicBlocks.FirstOrDefault()?.FirstInstruction?.Source));
        }

        private NativeFunctionCode Template(LlvmFunction function, LlvmTemplateFunctionInfo template, CompilationTrace? detail)
        {
            var slots = template.Captures.Select(type => new NativeSlot(type, NativeSlotRole.Capture))
                .Concat(template.Parameters.Select(type => new NativeSlot(type, NativeSlotRole.Parameter))).ToList();
            int result = slots.Count;
            int nullType = Primitive(TypeKind.Null);
            slots.Add(new(nullType));
            NativeInstruction[] instructions =
            [new(NativeOp.Constant, result, Constant(new(NativeKind.Null))), new(NativeOp.Return, A: result)];
            detail?.Metric("function.template", 1);
            detail?.Metric("captures.locations", template.Captures.Count);
            detail?.Metric("instructions", instructions.Length);
            detail?.Metric("slots", slots.Count);
            return new(template.SourceName ?? function.Name, slots, instructions,
                new(template.Parameters, nullType, template.Captures.Select(type => new NativeCaptureContract(type, NativeCaptureMode.Location)).ToArray()),
                template.Parameters.Count, template.Captures.Count, IsTemplate: true)
            { Storage = new(new NativeSlotStorage[slots.Count], new NativeInstructionStorage[instructions.Length]) };
        }

        internal int Pointer(int pointee)
        {
            if (pointerTypes is null)
            {
                pointerTypes = [];
                for (int i = 0; i < Types.Count; ++i)
                    if (Types[i].Pointee >= 0) pointerTypes.TryAdd(Types[i].Pointee, i);
            }
            if (pointerTypes.TryGetValue(pointee, out int type)) return type;
            type = Types.Count;
            Types.Add(new(TypeKind.Never, Pointee: pointee));
            pointerTypes.Add(pointee, type);
            return type;
        }

        internal int Primitive(TypeKind kind)
        {
            if (primitiveTypes.TryGetValue(kind, out int index)) return index;
            index = Types.FindIndex(type => !type.IsMachine && type.Kind == kind && type.Pointee < 0 && type.Element < 0 && type.Layouts is null);
            if (index < 0) { index = Types.Count; Types.Add(new(kind)); }
            primitiveTypes.Add(kind, index);
            return index;
        }
        internal int Type(LlvmValue value) => value.LogicalType >= 0 ? value.LogicalType : Type(value.Type);
        internal int Type(LlvmType type) => type switch
        {
            LlvmIntegerType integer when integer.BitWidth == 1 => Primitive(TypeKind.Bool),
            LlvmIntegerType => MachineScalarType(type),
            LlvmStructType structure when !structure.IsOpaque && structure.Elements.Count == 0 => Primitive(TypeKind.Null),
            _ when type.IsDoubleTy() => Primitive(TypeKind.Number),
            _ when type.IsFloatingPointTy() => MachineScalarType(type),
            _ => throw new NotSupportedException($"LLVM target requires producer logical type for {type.Id}.")
        };
        internal int Constant(NativeConstant value) { int index = Constants.Count; Constants.Add(value); return index; }
    }

    private sealed partial class Body(Lowering owner, LlvmFunction function, int functionIndex, CompilationTrace? trace,
        LayoutVariant? layoutVariant = null)
    {
        private readonly List<NativeSlot> slots = [];
        private readonly List<NativeInstruction> code = [];
        private readonly List<NativeInstruction> prelude = [];
        private readonly Dictionary<LlvmValue, int> values = [];
        private readonly Dictionary<LlvmBasicBlock, int> positions = [];
        private readonly Dictionary<LlvmPhiInstruction, int> incoming = [];
        private readonly Dictionary<LlvmBasicBlock, List<(int Target, LlvmUse Value, SourceSpan? Source)>> phiSources = [];
        private readonly List<(int PC, LlvmBasicBlock True, LlvmBasicBlock? False)> branches = [];
        private readonly List<(int PC, int Layout)> allocations = [];
        private readonly List<(int PC, int Target, bool NextIterator)> localBranches = [];
        private readonly Dictionary<LlvmValue, int> iterators = [];
        private readonly List<NativeCallStorage> calls = [];
        private readonly List<NativeCaptureContract> captureContracts = [];
        private int captureCount;
        private int directCallCount, dynamicCallCount, adaptationCount;
        private int resultLogicalType = -1;
        private SourceSpan? instructionSource;

        internal NativeFunctionCode Run()
        {
            if (trace is null) PlanCallCapturePreparation();
            else trace.Run("calls.capture_abi", this, static body => body.PlanCallCapturePreparation());
            PrepareSessionRoots();
            PrepareArguments();
            PrepareAggregateArguments();
            FindLayoutReachability();
            PreparePhis();
            IReadOnlyList<LlvmBasicBlock> blocks = layoutBlockOrder is { } reachable ? reachable : function.BasicBlocks;
            for (int index = 0; index < blocks.Count; index++)
            {
                var block = blocks[index];
                fallthrough = layoutBlockOrder is not null && index + 1 < blocks.Count ? blocks[index + 1] : null;
                positions.Add(block, code.Count);
                for (var instruction = block.FirstInstruction; instruction is not null; instruction = instruction.Next)
                    if (erasedCapturePreparation?.Contains(instruction) != true) Emit(instruction, block);
            }
            ApplyFunctionContextMarkers();
            int prefix = prelude.Count;
            foreach (int pc in boolDemandContinuations)
                code[pc] = code[pc] with { B = prefix + pc + 1, C = prefix + pc + 1 };
            foreach (var branch in branches)
            {
                var instruction = code[branch.PC];
                code[branch.PC] = branch.False is null
                    ? instruction with { A = prefix + positions[branch.True] }
                    : instruction with { B = prefix + positions[branch.True], C = prefix + positions[branch.False] };
            }
            foreach (var branch in localBranches)
                code[branch.PC] = branch.NextIterator
                    ? code[branch.PC] with { B = prefix + branch.Target }
                    : code[branch.PC] with { A = prefix + branch.Target };
            foreach (var allocation in allocations)
                owner.Allocations.Add(new(prefix + allocation.PC, allocation.Layout, Function: functionIndex));
            NativeInstruction[] instructions = prelude.Concat(code).ToArray();
            int resultType = ResultType();
            int[] parameters = function.Arguments.Skip(captureCount).Select(owner.Type).ToArray();
            var signature = new Msync.Core.Native.NativeSignature(parameters, resultType, captureContracts.ToArray());
            trace?.Metric("instructions", instructions.Length);
            trace?.Metric("slots", slots.Count);
            trace?.Metric("constants.materializations", constantSlots.Count);
            trace?.Metric("constants.materialization_hits", constantSlotHits);
            trace?.Metric("constants.contextual_uses", contextualConstantUses);
            trace?.Metric("constants.function_materializations", constantFunctionMaterializations);
            trace?.Metric("constants.leaf_materializations", constantValueSlots.Count);
            trace?.Metric("constants.leaf_materialization_hits", constantValueSlotHits);
            trace?.Metric("addresses.operand_views", addressViews);
            trace?.Metric("addresses.constant_gep_relocations", constantGepRelocations);
            trace?.Metric("addresses.constant_gep_materializations", constantGepMaterializations);
            trace?.Metric("addresses.constant_gep_expression_uses", constantGepExpressionUses);
            trace?.Metric("addresses.zero_gep_aliases", zeroGepAliases);
            trace?.Metric("functions.component_memory_loads", functionComponentLoads);
            trace?.Metric("functions.memory_load_group_candidates", functionMemoryLoadGroupCandidates);
            trace?.Metric("functions.memory_load_groups", functionMemoryLoadGroupsEmitted);
            trace?.Metric("functions.memory_group_components_omitted", functionMemoryGroupComponentsOmitted);
            trace?.Metric("functions.memory_reassemblies_reused", functionMemoryReassembliesReused);
            trace?.Metric("functions.component_memory_stores", functionMemoryComponentStores);
            trace?.Metric("functions.memory_assignments_packed", functionMemoryAssignmentsPacked);
            trace?.Metric("aggregates.leaf_copies", aggregateLeafCopies);
            trace?.Metric("aggregates.leaf_selects", aggregateLeafSelects);
            trace?.Metric("functions.component_projections", functionProjections);
            trace?.Metric("functions.component_exact_addresses", functionProjectionExactAddresses);
            trace?.Metric("functions.component_nullable_addresses", functionProjectionNullableAddresses);
            trace?.Metric("functions.component_abi_hint.value_queries", functionProjectionValueHints);
            trace?.Metric("functions.component_abi_hint.use_queries", functionProjectionUseHints);
            trace?.Metric("functions.component_abi_hint.canonical_queries", functionProjectionCanonicalHints);
            trace?.Metric("functions.component_carrier_copies", functionProjectionCarrierCopies);
            trace?.Metric("functions.boundary_compositions", functionCompositions);
            trace?.Metric("phi.incoming_registers", incoming.Count);
            trace?.Metric("phi.source_copies", phiSources.Values.Sum(list => list.Count));
            trace?.Metric("phi.carrier_adaptations", phiCarrierAdaptations);
            trace?.Metric("phi.literal_value_merges", literalPhis.Count);
            trace?.Metric("phi.literal_loads_elided", literalPhiLoads);
            trace?.Metric("allocations", allocations.Count);
            if (trace is not null)
                foreach (var group in instructions.GroupBy(instruction => instruction.Op))
                    trace.Metric("emit." + group.Key, group.Count());
            trace?.Metric("captures.locations", captureCount);
            trace?.Metric("closure.abi_environment_containers", 0);
            trace?.Metric("calls.total", calls.Count);
            trace?.Metric("calls.direct", directCallCount);
            trace?.Metric("calls.dynamic", dynamicCallCount);
            trace?.Metric("functions.adapt", adaptationCount);
            trace?.Metric("statics.tests", staticTests);
            trace?.Metric("statics.addresses", staticAddresses);
            trace?.Metric("statics.publications", staticPublications);
            trace?.Metric("session.cell_addresses", sessionCellAddresses);
            string name = owner.Input.SourceFunctions.GetValueOrDefault(function)?.SourceName ?? function.Name;
            if (layoutVariant is { } variant) name += $"$layout{variant.Argument}_{variant.Layout}";
            return new(name, slots.ToArray(), instructions, signature, parameters.Length, captureCount, IteratorCount: iterators.Count)
            { Storage = new(new NativeSlotStorage[slots.Count], new NativeInstructionStorage[instructions.Length])
                { Calls = calls.Select(call => call with { Instruction = call.Instruction + prefix }).ToArray() } };
        }

        private void PrepareArguments()
        {
            captureCount = owner.Input.SourceFunctions.TryGetValue(function, out var info) ? info.CaptureCount : 0;
            for (int i = 0; i < captureCount; ++i)
            {
                var argument = function.Arguments[i];
                int pointee = owner.Types[owner.Type(argument)].Pointee;
                Register(pointee, NativeSlotRole.Capture, argument.Name);
                captureContracts.Add(new(pointee, NativeCaptureMode.Location));
            }
            foreach (var argument in function.Arguments.Skip(captureCount))
                values.Add(argument, Register(owner.Type(argument), NativeSlotRole.Parameter, argument.Name));
            for (int i = 0; i < captureCount; ++i)
            {
                var argument = function.Arguments[i];
                int address = Register(owner.Type(argument));
                values.Add(argument, address);
                prelude.Add(new(NativeOp.FrameAddress, address, i));
            }
        }

        private int ResultType()
        {
            if (resultLogicalType >= 0) return resultLogicalType;
            if (owner.Input.SourceFunctions.TryGetValue(function, out var info)) return resultLogicalType = info.ResultType;
            foreach (var block in function.BasicBlocks)
                if (block.GetTerminatorOrNull() is LlvmReturnInstruction { ReturnValue: not null } result)
                    return resultLogicalType = OperandType(result.GetOperandUse(0));
            return resultLogicalType = owner.Type(function.ReturnType);
        }
        private int Register(int type, NativeSlotRole role = NativeSlotRole.Temporary, string? name = null)
        { int slot = slots.Count; slots.Add(new(type, role, Name: name)); return slot; }
        private static NotSupportedException Unsupported(LlvmValue value, string feature) =>
            new($"LLVM target emission does not implement {feature} at {(value as LlvmInstruction)?.Source}.");
        private void Add(NativeInstruction instruction) => code.Add(instruction);
    }
}
