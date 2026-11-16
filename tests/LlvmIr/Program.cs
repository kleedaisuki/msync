using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;

internal static class Program
{
    private const string ScopeSource = """
        let item = scope { left: 1, right: 2 };
        let alias = item;
        alias::left = 4;
        item::left + item::right
        """;

    private const string IoSource = """
        let text = $std::io::read_text(./not-created-input);
        let lines = $std::io::read_lines(./not-created-lines);
        let bytes = $std::io::read_bytes(./not-created-bytes);
        $std::io::write_text(./not-created-output, text);
        $std::io::append_text(./not-created-append, text);
        $std::io::write_bytes(./not-created-byte-output, bytes);
        $std::io::append_bytes(./not-created-byte-append, bytes);
        text -> ./not-created-transfer;
        """;

    private const string PathTransferSource = "./not-created-source -> ./not-created-destination";

    public static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--o2-address-cost-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                pointerWalks = PointerWalkSmallSetProperties.Run(), addresses = DseAddressReuseProperties.Run(),
                declaredStorage = await DeclaredStorageUseJourney.RunAsync() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--freeze-lowering")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                executions = await FreezeLoweringChecks.RunAsync() }));
            return;
        }
        if (args.Length == 1 && args[0] == "--builtin-record-layouts")
        {
            Console.WriteLine($"PASS {await ScopeLayoutAliasChecks.RunBuiltinRecordsAsync()} builtin record layout properties");
            return;
        }
        if (args.Length == 1 && args[0] == "--zero-sized-scope-fields")
        {
            Console.WriteLine($"PASS {await ScopeLayoutAliasChecks.RunZeroSizeAsync()} zero-sized scope field properties");
            return;
        }
        if (args.Length == 1 && args[0] == "--scope-layout-aliases")
        {
            Console.WriteLine($"PASS {await ScopeLayoutAliasChecks.RunAsync()} formal scope layout properties");
            return;
        }
        if (args.Length == 3 && args[0] == "--optimized-source-dump")
        {
            OptimizedSourceDump.Run(args[1], args[2]);
            return;
        }
        if (args.Length > 0 && args[0] == "--instcombine-source-journey")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                executions = await InstCombineMemoryJourney.RunAsync(args.Length == 3 && args[1] == "--output" ? args[2] : null) }));
            return;
        }
        if (args.Length > 0 && args[0] == "--instcombine-gep-journey")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                executions = await InstCombineGepJourney.RunAsync(args.Length == 3 && args[1] == "--output" ? args[2] : null) }));
            return;
        }
        if (args.Length > 0 && args[0] == "--instcombine-vector-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                vectors = InstCombineVectorProperties.Run() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--instcombine-only")
        {
            Dictionary<string, object> groups = [];
            List<Exception> failures = [];
            RunGroup("booleanCompares", () =>
            {
                var comparisons = BooleanCompareProperties.Run();
                return new { comparisons.ComparisonCodes, comparisons.FloatingPredicates };
            });
            RunGroup("sharedLogical", () =>
            {
                var logical = SharedLogicalProperties.Run();
                return new { logical.OverflowMatches, logical.TruthTables };
            });
            RunGroup("controller", InstCombineControllerProperties.Run);
            RunGroup("addSub", InstCombineAddSubProperties.Run);
            RunGroup("exactNoWrap", ExactNoWrapProperties.Run);
            RunGroup("floatingPatterns", FloatingSelectPatternChecks.Run);
            RunGroup("integerCompares", InstCombineICmpConstantProperties.Run);
            RunGroup("floatingCompares", InstCombineFCmpChecks.Run);
            RunGroup("integerCasts", InstCombineIntegerCastProperties.Run);
            RunGroup("shifts", InstCombineShiftProperties.Run);
            RunGroup("memory", InstCombineMemoryProperties.Run);
            RunGroup("vectors", InstCombineVectorProperties.Run);
            RunGroup("geps", InstCombineGepProperties.Run);
            RunGroup("sharedArithmetic", InstCombineSharedArithmeticProperties.Run);
            RunGroup("bitOrder", InstCombineBitOrderProperties.Run);
            RunGroup("integerCompareBitcasts", InstCombineICmpBitCastJourney.Run);
            RunGroup("floatingCalls", InstCombineFloatingCallProperties.Run);
            RunGroup("floatingIntrinsics", FloatingIntrinsicProperties.Run);
            Console.WriteLine(JsonSerializer.Serialize(new { status = failures.Count == 0 ? "passed" : "failed", groups }));
            if (failures.Count != 0)
                throw new AggregateException("InstCombine property groups failed; see per-group results.", failures);
            return;

            void RunGroup<T>(string name, Func<T> run)
            {
                try { groups.Add(name, new { status = "passed", result = run() }); }
                catch (Exception error)
                {
                    groups.Add(name, new { status = "failed", error = error.ToString() });
                    failures.Add(new InvalidOperationException($"InstCombine property group '{name}' failed.", error));
                }
            }
        }
        if (args.Length > 0 && args[0] == "--sroa-source-journey")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", executions = await SroaSourceJourney.RunAsync() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--function-ssa-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                environments = FunctionEnvironmentProperties.Run(), functions = await FunctionSsaJourney.RunAsync(),
                layout = ManagedLayoutProperties.Run(), sourceExecutions = await ManagedLayoutProperties.RunAsync() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--machine-scalars-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                scalars = NativeMachineScalarProperties.Run(),
                artifacts = await NativeMachineScalarProperties.RunArtifactAsync("artifacts/diagnostics/llvm-foundations/captured-O2.mso"),
                operations = await MachineExecutionChecks.RunAsync(),
                target = await MachineLoweringJourney.RunAsync(args.Length == 3 && args[1] == "--output" ? args[2] : null) }));
            return;
        }
        if (args.Length > 0 && args[0] == "--sroa-source-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                pointerUses = PtrUseVisitorProperties.Run(),
                aggregates = SroaAggregateSpeculationProperties.Run(), driver = SroaDriverChecks.Run(),
                partitions = SroaPartitionChecks.Run(), rewriter = SroaRewriterProperties.Run(),
                earlyMemory = EarlyCseInvariantProperties.Run(), floatingVectors = FloatingVectorIntrinsicProperties.Run() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--foundations-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                attributes = CoreAttributeProperties.Run(), conditions = CondContextChecks.Run(),
                dominanceConditions = DomConditionCacheProperties.Run(), layout = SroaLayoutProperties.Run(),
                alignment = LocalAlignmentProperties.Run(),
                floats = ApFloatFormatProperties.Run(), vectors = VectorIrProperties.Run(),
                constantElements = ConstantElementQueryProperties.Run(), availableLoads = AvailableLoadProperties.Run(),
                callEffects = CallMemoryEffectsProperties.Run(),
                memoryIntrinsics = MemoryIntrinsicChecks.Run(), targetFolder = TargetFolderProperties.Run(),
                earlyCseMemory = EarlyCseMemorySsaProperties.Run() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--ssa-abi-only")
        {
            RauwAbiChecks.Run();
            string fixtures = Path.Combine("tests", "LlvmIr", "Fixtures");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed",
                pointers = await PointerArithmeticJourney.RunAsync(),
                components = await SinkOperandAbiChecks.RunAsync(),
                capturedExecutions = await SinkOperandAbiChecks.RunSourceAsync(Path.Combine(fixtures, "CapturedOperandPhi.msync")),
                callableExecutions = await RauwAbiChecks.RunSourceAsync(Path.Combine(fixtures, "DynamicchooseFns.msync")) }));
            return;
        }
        if (args.Length > 0 && args[0] == "--core-attributes-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", properties = CoreAttributeProperties.Run() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--simplify-cfg-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", comparisons = EquivalentIcmpProperties.Run(),
                conditionals = SimplifyCfgConditionalProperties.Run(), core = SimplifyCfgCoreProperties.Run(),
                speculation = SimplifyCfgSpeculationProperties.Run(), queries = SimplifyQueryAssumptionProperties.Run(),
                switches = SimplifyCfgSwitchChecks.Run(), assumptions = AssumptionFactProperties.Run(),
                fpAssumptions = KnownFPClassAssumptionProperties.Run(), pointers = await PointerArithmeticJourney.RunAsync() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--scalar-intrinsics-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", scalarIntrinsics = ScalarIntrinsicChecks.Run(),
                executions = await ScalarIntrinsicExecution.RunAsync() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--target-costs-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", targetCosts = TargetCostProperties.Run(),
                instructionCosts = InstructionCostProperties.Run(), branchProbabilities = BranchProbabilityProperties.Run() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--cfg-components-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", cycles = CycleInfoChecks.Run(),
                dominatorUpdater = DomTreeUpdaterProperties.Run(), codeMetrics = CodeMetricsProperties.Run(),
                valueHandles = ValueHandleProperties.Run(), localCfg = LocalCfgProperties.Run(),
                unreachable = UnreachableBlockProperties.Run(), builder = TransformBuilderProperties.Run(),
                cloning = await CloningChecks.RunAsync(), blockLifetimes = BlockLifetimeProperties.Run() }));
            return;
        }
        if (args.Length > 0 && args[0] == "--optimizer-components-only")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", components = OptimizerComponentChecks.Run() }));
            return;
        }
        if (args.Length == 4 && args[0] == "--diagnose-callable" && args[2] == "--output")
        {
            await CallableBaselineDiagnostic.RunAsync(args[1], args[3]);
            return;
        }
        bool dseRotationOnly = args.Length != 0 && args[0] == "--dse-rotation-only";
        bool adceOnly = args.Length != 0 && args[0] == "--adce-only";
        bool earlyLoopsOnly = args.Length != 0 && args[0] == "--early-loops-only";
        bool loopsOnly = args.Length != 0 && args[0] == "--loops-only";
        bool typesOnly = args.Length != 0 && args[0] == "--types-only";
        bool sccpOnly = args.Length != 0 && args[0] == "--sccp-only";
        if (typesOnly || sccpOnly || loopsOnly || earlyLoopsOnly || adceOnly || dseRotationOnly) args = args[1..];
        string? output = args.Length == 2 && args[0] == "--output" ? Path.GetFullPath(args[1]) : null;
        if (output is not null) Directory.CreateDirectory(output);
        if (dseRotationOnly)
        {
            DseRotationReceipt receipt = await DseRotationJourney.RunAsync(output);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", dseRotation = receipt }));
            return;
        }
        if (adceOnly)
        {
            AdceReceipt receipt = await AdceJourney.RunAsync(output);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", adce = receipt }));
            return;
        }
        if (earlyLoopsOnly)
        {
            EarlyLoopReceipt receipt = await EarlyLoopJourney.RunAsync(output);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", earlyLoops = receipt }));
            return;
        }
        if (loopsOnly)
        {
            LoopPrerequisiteReceipt receipt = await LoopPrerequisiteJourney.RunAsync(output);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", loopPrerequisites = receipt }));
            return;
        }
        if (typesOnly)
        {
            TypeOperationReceipt receipt = await TypeOperationJourney.RunAsync(output);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", typesOperations = receipt }));
            return;
        }
        if (sccpOnly)
        {
            SccpReceipt receipt = await SccpExecutionJourney.RunAsync(output);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", sccp = receipt }));
            return;
        }
        ExtensionUses.Run();
        int apIntPairs = ApIntProperties.Run();
        int apFloatPairs = ApFloatProperties.Run();
        int fpRangeChecks = ConstantFPRangeChecks.Run();
        long integerRangeChecks = ConstantRangeProbe.Run();
        int knownFpClassChecks = KnownFPClassProperties.Run();
        long knownBitsChecks = KnownBitsProperties.Run();
        int integerSimplifyChecks = IntegerSimplifyProperties.Run();
        int integerNonEqualChecks = IntegerNonEqualProperties.Run();
        int integerCompareChecks = IntegerCompareProperties.Run();
        int integerValueFactsChecks = IntegerValueFactsProperties.Run();
        ConstantAbiUses.Run();
        await ConstantAbiUses.RunFoldedFunctionsAsync();
        ConstantLoadOffsetChecks.Run();
        await AddressViewJourney.RunAsync();
        AggregateUses.Run();
        DceReceipt dce = DceChecks.Run(output);
        LlvmModule scope = Lower(ScopeSource, "llvm-scope.msync");
        LlvmInstruction[] scopeCode = Instructions(scope);
        Require(scopeCode.OfType<LlvmHeapAllocInst>().Any(), "A source Scope must become an ordinary heap allocation.");
        LlvmGetElementPtrInstruction[] fields = scopeCode.OfType<LlvmGetElementPtrInstruction>()
            .Where(instruction => instruction.SourceElementType is LlvmStructType structure
                && structure.NumElements == 2 && structure.Elements.All(type => type.IsDoubleTy())).ToArray();
        Require(fields.Length != 0, "Source Scope fields lost their typed struct GEP representation.");
        Require(fields.All(field => field.Source is { Source: "llvm-scope.msync", Length: > 0 }),
            "Scope field instructions lost their producing source context.");
        Require(fields.All(field => field.ResultElementType.IsDoubleTy()), "Scope field addresses must retain their Number type.");
        Require(scopeCode.OfType<LlvmStoreInstruction>().Any(store => fields.Any(field => ReferenceEquals(field, store.PointerOperand))),
            "Source Scope initialization/mutation must store through a field address.");
        Require(scopeCode.OfType<LlvmLoadInstruction>().Any(load => fields.Any(field => ReferenceEquals(field, load.PointerOperand))),
            "A source member read must load through a field address.");
        Require(scopeCode.Any(instruction => instruction.Opcode == LlvmOpcode.FAdd),
            "Number member arithmetic must be ordinary floating-point IR.");
        string scopeDump = LlvmIrWriter.Write(scope);
        Require(scopeDump.Contains("heapalloc", StringComparison.Ordinal)
            && scopeDump.Contains("getelementptr", StringComparison.Ordinal)
            && scopeDump.Contains("load", StringComparison.Ordinal)
            && scopeDump.Contains("store", StringComparison.Ordinal), "The source Scope IR dump omitted its memory operations.");

        LlvmHirLoweringResult ioLowering = LlvmHirLowerer.Lower(Compiler.Analyze(IoSource, "llvm-io.msync"));
        LlvmModule io = ioLowering.Module;
        LlvmInstruction[] ioCode = Instructions(io);
        LlvmFileInInst[] reads = ioCode.OfType<LlvmFileInInst>().ToArray();
        foreach (LlvmFileInFormat format in Enum.GetValues<LlvmFileInFormat>())
            Require(reads.Any(instruction => instruction.Format == format), $"Known io.read_{format} did not become FileIn.");
        LlvmFileOutInst[] writes = ioCode.OfType<LlvmFileOutInst>().ToArray();
        foreach (LlvmFileOutMode mode in Enum.GetValues<LlvmFileOutMode>())
            Require(writes.Any(instruction => instruction.Mode == mode), $"Known file output mode {mode} did not become FileOut.");
        Require(ioCode.OfType<LlvmCallBase>().All(call => call.CalledFunction is { } target
            && ioLowering.RuntimeCalls.TryGetValue(target, out NativeBinary operation) && operation == NativeBinary.PathJoin),
            "Known file IO must use FileIn/FileOut; only explicit Path construction calls are expected here.");
        Require(reads.Cast<LlvmInstruction>().Concat(writes).All(instruction => instruction.Source is
            { Source: "llvm-io.msync", Length: > 0 }), "Explicit file operations lost their actual source context.");
        string ioDump = LlvmIrWriter.Write(io);
        Require(ioDump.Contains("file.in", StringComparison.Ordinal) && ioDump.Contains("file.out", StringComparison.Ordinal),
            "Known source IO did not survive as explicit operations in the IR dump.");
        LlvmModule path = Lower(PathTransferSource, "llvm-path-transfer.msync");
        LlvmInstruction[] pathCode = Instructions(path);
        LlvmFileOutInst pathTransfer = pathCode.OfType<LlvmFileOutInst>().Single();
        Require(pathTransfer.Mode == LlvmFileOutMode.Transfer && !pathCode.OfType<LlvmFileInInst>().Any(),
            "Path transfer must retain its Path operand, not invent an eager file read.");
        Require(pathTransfer.Value.Type is LlvmStructType pathType && pathType.NumElements == 7
            && pathType.Elements.Take(2).All(type => type.IsIntegerTy(8))
            && pathType.Elements.Skip(2).All(type => type.IsPointerTy())
            && ReferenceEquals(pathTransfer.Value.Type, pathTransfer.Destination.Type),
            "Path transfer lost the explicit path/binding descriptor ABI.");
        string pathDump = LlvmIrWriter.Write(path);
        if (output is not null)
        {
            File.WriteAllText(Path.Combine(output, "scope.msync"), ScopeSource);
            File.WriteAllText(Path.Combine(output, "io.msync"), IoSource);
            File.WriteAllText(Path.Combine(output, "scope.ll"), scopeDump);
            File.WriteAllText(Path.Combine(output, "io.ll"), ioDump);
            File.WriteAllText(Path.Combine(output, "path-transfer.ll"), pathDump);
        }
        ExecutionReceipt execution = await OptimizedExecutionJourney.RunAsync(output);
        CallableReceipt callables = await CallableObjectJourney.RunAsync(output);
        TypeOperationReceipt typesOperations = await TypeOperationJourney.RunAsync(output);
        SccpReceipt sccp = await SccpExecutionJourney.RunAsync(output);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "passed",
            apIntPairs,
            apFloatPairs,
            fpRangeChecks,
            integerRangeChecks,
            knownFpClassChecks,
            knownBitsChecks,
            integerSimplifyChecks,
            integerNonEqualChecks,
            integerCompareChecks,
            integerValueFactsChecks,
            executionIntegration = true,
            execution,
            callables,
            typesOperations,
            sccp,
            dce,
            scopeInstructions = scopeCode.Length,
            scopeHeapAllocations = scopeCode.OfType<LlvmHeapAllocInst>().Count(),
            scopeFieldAddresses = fields.Length,
            ioReadFormats = reads.Select(instruction => instruction.Format.ToString()).Distinct().Order().ToArray(),
            ioWriteModes = writes.Select(instruction => instruction.Mode.ToString()).Distinct().Order().ToArray(),
            scopeDumpSha256 = Hash(scopeDump),
            ioDumpSha256 = Hash(ioDump),
            pathTransferDumpSha256 = Hash(pathDump)
        }));
    }

    private static LlvmModule Lower(string source, string name) => LlvmHirLowerer.Lower(Compiler.Analyze(source, name)).Module;
    private static LlvmInstruction[] Instructions(LlvmModule module) => module.Functions
        .SelectMany(function => function.BasicBlocks).SelectMany(block => block.Instructions).ToArray();
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
