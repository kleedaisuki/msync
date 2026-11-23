using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    public static HpirProgram Lower(LlvmHirLoweringResult graph, CompilationTrace? trace = null)
        => Select(graph, trace).Program;

    internal static HpirSessionSubmission LowerSession(LlvmHirLoweringResult graph, CompilationTrace? trace) =>
        Select(graph, trace).Session ?? throw new ArgumentException("Expected a physical session graph.", nameof(graph));

    private static (HpirProgram Program, HpirSessionSubmission? Session) Select(LlvmHirLoweringResult graph, CompilationTrace? trace)
    {
        if (trace?.Detail is not { } detail) return LowerCore(graph, null);
        return detail.MeasureDetailed("compiler.hpir.select", (graph, detail),
            static state => LowerCore(state.graph, state.detail), metrics: static result => new Dictionary<string, ulong>
            {
                ["functions"] = (ulong)result.Program.Functions.Count,
                ["instructions"] = (ulong)result.Program.Functions.Sum(function => function.Instructions.Length),
                ["frame.bytes"] = (ulong)result.Program.Functions.Sum(function => (long)function.FrameBytes),
                ["session.cells"] = (ulong)(result.Session?.CellRelocations.Count ?? 0)
            });
    }

    private static (HpirProgram Program, HpirSessionSubmission? Session) LowerCore(LlvmHirLoweringResult graph, CompilationTrace? trace)
    {
        var entries = graph.Module.Functions.Where(function => !function.IsDeclaration).ToArray();
        var ids = new Dictionary<LlvmFunction, int>(entries.Length);
        for (int index = 0; index < entries.Length; ++index) ids.Add(entries[index], index);
        var geometry = new Geometry(graph, ids);
        geometry.PrepareSessionText(trace);
        geometry.PrepareTextResults(trace);
        var functions = new HpirFunction[entries.Length];
        for (int index = 0; index < functions.Length; ++index)
        {
            var function = entries[index];
            var lowerer = new Body(function, geometry, ids);
            if (trace is null) functions[index] = lowerer.Run(null);
            else
            {
                var attributes = new Dictionary<string, string>
                {
                    ["function.id"] = index.ToString(CultureInfo.InvariantCulture),
                    ["function.name"] = function.Name
                };
                if (function.BasicBlocks[0].FirstInstruction?.Source is { } source)
                    attributes["source.file"] = source.Source;
                functions[index] = trace.MeasureDetailed("compiler.hpir.select.function", (lowerer, trace),
                    static state => state.lowerer.Run(state.trace), attributes: attributes);
            }
        }
        if (trace is not null) geometry.ReportRangeInputs(trace);
        geometry.FinishTypeNames(trace);
        int textLayout = geometry.SessionTextLayout();
        HpirProgram program = new(geometry.PathBatchWorkers.Count == 0 ? functions : [.. functions, .. geometry.PathBatchWorkers], ids[graph.Entry])
        {
            Stage = HpirStage.Relocatable,
            Layouts = geometry.Layouts.ToArray(),
            Globals = geometry.Globals.ToArray(),
            Imports = geometry.Imports.ToArray(),
            TextLiterals = geometry.TextLiterals.ToArray(),
            TypeLiterals = geometry.TypeLiterals.ToArray(),
            TypeCells = geometry.TypeCells.ToArray(),
            TextLayout = textLayout,
            PathAnchors = geometry.PathAnchorsNeeded,
            ContextChecks = geometry.ContextChecks.ToArray(),
            TransferInputs = geometry.TransferInputs.ToArray(),
            IteratorSelections = geometry.IteratorSelections.ToArray(),
            SessionAllocations = geometry.SessionAllocations.ToArray(),
            SessionRangeItems = geometry.SessionRangeItems.ToArray()
        };
        return (program, geometry.FinishSession(program, trace));
    }

    private sealed partial class Body(LlvmFunction function, Geometry geometry, Dictionary<LlvmFunction, int> ids)
    {
        private readonly List<HpirInstruction> code = [];
        private readonly List<HpirSource> sources = [];
        private readonly List<HpirCall> calls = [];
        private readonly Dictionary<LlvmValue, int> operands = [];
        private readonly Dictionary<LlvmAllocaInstruction, int> storage = [];
        private readonly Dictionary<LlvmBasicBlock, int> blocks = [];
        private readonly Dictionary<(LlvmBasicBlock From, LlvmBasicBlock To), int> edges = [];
        private readonly List<EdgeFixup> branches = [];
        private int extent;
        private int constants, staticAddresses, indirectAccesses, phiMoves, phiCycles, omittedAssumptions;

        private LlvmDataLayout Layout => geometry.Data;

        internal HpirFunction Run(CompilationTrace? trace)
        {
            PrepareEffectInputs(trace);
            PrepareContextChecks(trace);
            PrepareIteratorSelection(trace);
            PrepareFutureCarriers(trace);
            HpirParameter[] parameters = Parameters();
            PrepareFrame();
            PrepareSelfTails(parameters, trace);
            PrepareTemporaries(trace);
            PrepareConstants();
            foreach (var block in function.BasicBlocks)
            {
                blocks.Add(block, code.Count);
                foreach (var instruction in block.Instructions)
                {
                    Emit(instruction, block);
                    if (IsSelfTail(instruction)) break;
                }
            }
            LinkEdges();
            HpirRootMap[] roots = Roots(trace);
            int frameBytes = Align(extent, HpirAbi.FrameAlignment);
            if (frameBytes > int.MaxValue - HpirAbi.FrameHeaderBytes) throw Error("frame exceeds the physical ABI extent");
            trace?.Metric("frame.bytes", frameBytes);
            trace?.Metric("frame.locals", storage.Count);
            trace?.Metric("instructions", code.Count);
            trace?.Metric("calls.direct", guestCalls);
            ReportSelfTails(trace);
            trace?.Metric("calls.imports", importCalls);
            ReportRegex(trace);
            ReportLiveSelections(trace);
            trace?.Metric("calls.guest_number_continuations", guestNumberSites);
            trace?.Metric("calls.finite_sites", dynamicSites);
            trace?.Metric("calls.finite_arms", dynamicArms);
            trace?.Metric("scalar.former_lifts_ready", readyFormerLifts);
            trace?.Metric("future.stored_text_demands", storedTextDemands);
            trace?.Metric("session.prompt_input_stores", promptInputStores);
            ReportTextResults(trace);
            trace?.Metric("future.ready_demands_elided", readyDemands);
            trace?.Metric("calls.fixed_callable_sites", fixedCallableSites);
            RecordNumericRange(trace);
            ReportTransforms(trace);
            ReportCountedRanges(trace);
            ReportZipRanges(trace);
            ReportConcats(trace);
            ReportTextPipelines(trace);
            ReportLiveTextRange(trace);
            ReportDirectories(trace);
            ReportTextJoins(trace);
            trace?.Metric("vectors.text_order_sites", textVectorOrders);
            ReportTextFormats(trace);
            ReportVectors(trace);
            ReportVectorWindows(trace);
            ReportSlides(trace);
            ReportVectorFilters(trace);
            ReportVectorAccess(trace);
            ReportVectorQuantifiers(trace);
            ReportMetaTypes(trace);
            ReportScalarCasts(trace);
            ReportPaths(trace);
            ReportPathBatches(trace);
            ReportConnections(trace);
            trace?.Metric("future.folded_field_lanes", foldedFutureFields);
            ReportFileTextWrites(trace);
            ReportFileByteWrites(trace);
            ReportFileRenames(trace);
            ReportProcesses(trace);
            ReportWork(trace);
            trace?.Metric("union.packs", unionPacks);
            trace?.Metric("union.projections", unionProjections);
            trace?.Metric("union.repacks", unionRepacks);
            trace?.Metric("union.tag_reads", unionTagReads);
            trace?.Metric("types.literal_loads", typeLiteralLoads);
            trace?.Metric("types.static_predicates", staticTypePredicates);
            trace?.Metric("types.dynamic_predicates", dynamicTypePredicates);
            trace?.Metric("control.block_edges", edges.Count);
            trace?.Metric("control.edge_moves", phiMoves);
            trace?.Metric("control.edge_cycles", phiCycles);
            trace?.Metric("control.assumptions_omitted", omittedAssumptions);
            trace?.Metric("constants.materialized", constants);
            trace?.Metric("memory.direct_addresses", staticAddresses);
            trace?.Metric("memory.global_relocations", globalAddresses);
            trace?.Metric("memory.session_alias_bindings", sessionAliasBindings);
            trace?.Metric("memory.indirect_accesses", indirectAccesses);
            trace?.Metric("memory.uniform_projections", uniformProjectionCount);
            trace?.Metric("memory.finite_projections", finiteProjectionCount);
            if (trace is not null)
            {
                foreach (var group in calls.Where(call => call.Target < 0).GroupBy(call => geometry.Imports[~call.Target].Service))
                    trace.Metric($"calls.import.{group.Key}", group.Count());
            }
            return new(function.Name, frameBytes, Result, parameters, code.ToArray(), calls.ToArray(), sources.ToArray()) { Safepoints = roots };
        }

        private void PrepareConstants()
        {
            // Literals are initialized before the first block so they also
            // dominate PHI transfers on backedges and untaken branches.
            foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
            {
                if (IsSlicedEffectOperand(instruction) || instruction is LlvmLoadInstruction { IsSimple: true,
                    PointerOperand: LlvmGlobalVariable global }
                    && (geometry.TryTextGlobal(global, out _) || geometry.TypeLiteral(global) is not null
                        || geometry.RawPathLiteral(global) is not null || geometry.ConnectionLiteral(global) is not null)) continue;
                if (instruction is LlvmCallInstruction invocation && geometry.SessionAliasLink(invocation) is { } alias)
                    PrepareConstant(alias);
                int count = instruction is LlvmCallInstruction call ? call.ArgumentCount : instruction.OperandCount;
                for (int index = 0; index < count; ++index)
                    if (instruction.GetOperand(index) is LlvmConstant constant)
                    {
                        if (constant is LlvmGlobalVariable path && !operands.ContainsKey(path) && PreparePathCell(path, instruction)) continue;
                        PrepareLiteral(constant);
                    }
            }
        }

        private readonly Dictionary<(HpirWidth, long), int> literals = [];

        private void PrepareLiteral(LlvmConstant value)
        {
            if (value.Type is LlvmStructType or LlvmArrayType)
            {
                foreach (var leaf in geometry.Leaves(value.Type))
                    PrepareConstant(LiteralField(value, leaf.Offset));
            }
            else PrepareConstant(value);
        }

        private void PrepareConstant(LlvmConstant value)
        {
            if (operands.ContainsKey(value)) return;
            if (value is LlvmGlobalVariable variable && (PrepareGlobal(variable) || PrepareConnectionCell(variable))) return;
            if (value is LlvmGlobalVariable type && geometry.TypeLiteral(type) is { } witness)
            {
                int cell = Reserve(8, 8);
                Add(new(HpirOp.AddressOfTypeCell, cell, Immediate: geometry.TypeCell(witness)));
                operands.Add(value, cell);
                ++constants;
                return;
            }
            if (value is LlvmConstantExpr address && PrepareConstantAddress(address)) return;
            if (value is LlvmGlobalVariable global && geometry.TryTextGlobal(global, out string text))
            {
                int cell = Reserve(8, 8);
                Add(new(HpirOp.AddressOfStaticCell, cell, Immediate: geometry.TextLiteral(text)));
                operands.Add(value, cell);
                ++constants;
                return;
            }
            if (!TryLiteral(value, out long bits)) return;
            var width = Width(value.Type);
            if (width == HpirWidth.Void) return;
            if (!literals.TryGetValue((width, bits), out int offset))
            {
                offset = Reserve((int)width, (int)width);
                literals.Add((width, bits), offset);
                Add(new(ConstOp(width), offset, Immediate: bits));
                ++constants;
            }
            operands.Add(value, offset);
        }

        private int Location(LlvmValue value)
        {
            if (operands.TryGetValue(value, out int offset) || temporaries.TryGetValue(value, out offset)) return offset;
            if (value is LlvmConstant) throw Error($"materializing {value.GetType().Name} '{value.Name}'");
            var width = ValueWidth(value);
            if (width == HpirWidth.Void) throw Error("materializing a void value");
            offset = Reserve((int)width, (int)width);
            operands.Add(value, offset);
            return offset;
        }

        private int Reserve(int bytes, int alignment)
        {
            int offset = Align(extent, alignment);
            extent = checked(offset + bytes);
            return offset;
        }

        private static int Align(int value, int alignment) => checked(value + alignment - 1) & -alignment;

        private void Add(HpirInstruction instruction, SourceSpan? source = null)
        {
            if (source is { } span && (sources.Count == 0 || sources[^1].Source != span))
                sources.Add(new(code.Count, span));
            code.Add(instruction);
        }

        private MsyncException Error(string reason, LlvmInstruction? instruction = null) =>
            new($"HPIR selection in '{function.Name}': {reason}", instruction?.Source);

        private HpirWidth Width(LlvmType type) => type switch
        {
            LlvmIntegerType { BitWidth: 1 or 8 } => HpirWidth.U8,
            LlvmIntegerType { BitWidth: 16 } => HpirWidth.U16,
            LlvmIntegerType { BitWidth: 32 } => HpirWidth.U32,
            LlvmIntegerType { BitWidth: 64 } or LlvmPointerType => HpirWidth.U64,
            LlvmStructType { IsOpaque: false, Elements.Count: 0 } => HpirWidth.Void,
            _ when type.IsVoidTy() => HpirWidth.Void,
            _ when type.IsDoubleTy() => HpirWidth.U64,
            _ => throw Error($"lowering carrier {type.Id}")
        };

        private bool TryLiteral(LlvmConstant value, out long bits)
        {
            switch (value)
            {
                case LlvmConstantInt integer when integer.Value.TryZExtValue() is { } raw:
                    bits = unchecked((long)raw); return true;
                case LlvmConstantFP floating when floating.Type.IsDoubleTy():
                    bits = unchecked((long)floating.Value.BitcastToAPInt().GetZExtValue()); return true;
                case LlvmFunction target:
                    bits = geometry.CodeIdentity(target); return true;
                case LlvmConstantPointerNull:
                    bits = 0; return true;
                case LlvmConstantExpr { Opcode: LlvmOpcode.IntToPtr } code
                    when code.Type is LlvmPointerType pointer && pointer.AddressSpace == Layout.GetProgramAddressSpace()
                        && code.GetOperand(0) is LlvmConstantInt identity && identity.Value.TryZExtValue() is { } encoded:
                    bits = unchecked((long)encoded); return true;
                default:
                    bits = 0; return false;
            }
        }

        private static HpirOp ConstOp(HpirWidth width) => width switch
        {
            HpirWidth.U8 => HpirOp.ConstU8, HpirWidth.U16 => HpirOp.ConstU16,
            HpirWidth.U32 => HpirOp.ConstU32, HpirWidth.U64 => HpirOp.ConstU64,
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        };

        private static HpirOp CopyOp(HpirWidth width) => width switch
        {
            HpirWidth.U8 => HpirOp.CopyU8, HpirWidth.U16 => HpirOp.CopyU16,
            HpirWidth.U32 => HpirOp.CopyU32, HpirWidth.U64 => HpirOp.CopyU64,
            _ => throw new ArgumentOutOfRangeException(nameof(width))
        };
    }
}
