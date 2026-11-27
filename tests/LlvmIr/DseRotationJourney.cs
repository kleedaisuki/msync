using System.Globalization;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record DseRotationReceipt(int SourceCases, int Executions, int ArtifactExecutions,
    int ComponentStoresBefore, int ComponentStoresAfter, int OverwrittenArmStoresRemoved,
    ulong FieldStoreHitsBefore, ulong FieldStoreHitsAfter, ulong PublicDseOverwrites, ulong PublicLoopsRotated);

internal static class DseRotationJourney
{
    private const string Overwrite = """
        fn overwrite(flag: Bool) {
            let row = object { left: 0, right: 2 };
            if flag { row::left = 1; } else { row::left = 3; };
            row::left = 5;
            row::left + row::right
        };
        overwrite(true) + overwrite(false)
        """;
    private const string Clobber = """
        fn observe(row: Object) { row::left };
        fn keep(flag: Bool) {
            let row = object { left: 0, right: 2 };
            if flag { row::left = 1; } else { row::left = 3; };
            let seen = observe(row);
            row::left = 5;
            seen * 10 + row::left + row::right
        };
        keep(true) + keep(false)
        """;
    private const string Escaped = """
        fn make(value: Number) {
            let row = object { left: value, right: 2 };
            row::left = value + 1;
            row
        };
        let first = make(3); let second = make(5); let alias = first;
        alias::left + second::left
        """;
    private const string Rotation = """
        let calls = 0;
        fn below(index: Number, limit: Number) { calls = calls + 1; index < limit };
        fn sum(limit: Number) {
            let index = 0; let total = 0;
            loop {
                if not below(index, limit) { break; };
                $std::io::append_text(./iterations.txt, "x");
                total = total + index + 1;
                index = index + 1;
            };
            total
        };
        $std::io::write_text(./iterations.txt, "");
        let zero = sum(0); let one = sum(1); let many = sum(4);
        let outer = 0;
        loop {
            if outer >= 3 { break; };
            let inner = 0;
            loop {
                if inner >= 2 { break; };
                $std::io::append_text(./iterations.txt, "n");
                inner = inner + 1;
            };
            outer = outer + 1;
        };
        let early = 0;
        loop {
            early = early + 1;
            if early == 3 { break; };
            $std::io::append_text(./iterations.txt, "e");
        };
        (zero + one + many) * 100 + calls
        """;

    internal static async Task<DseRotationReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "dse-rotation");
        Directory.CreateDirectory(root);
        List<CompilationObservation> componentRecords = [];
        (int before, int after, int removed) = Component(root, new(componentRecords.Add, includeOptimizerPasses: true));
        List<CompilationObservation> publicRecords = [];
        CompilationTrace publicTrace = new(publicRecords.Add, includeOptimizerPasses: true);
        int executions = 0, artifacts = 0;
        ulong beforeHits = 0, afterHits = 0;
        foreach (int level in new[] { 0, 2 })
        foreach ((string name, string source, double expected) in new[]
        { ("overwrite", Overwrite, 14d), ("callback-clobber", Clobber, 54d),
          ("escaped-alias", Escaped, 10d), ("rotation", Rotation, 1108d) })
        {
            string work = Path.Combine(root, "O" + level, name);
            Directory.CreateDirectory(work);
            string sourceFile = Path.Combine(work, "source.msync");
            File.WriteAllText(sourceFile, source);
            NativeProgram program = Compiler.Compile(source, sourceFile, level, publicTrace);
            File.WriteAllText(Path.Combine(work, "native-code.txt"), NativeArtifact.Disassemble(program));
            IReadOnlyList<RuntimeProfileSample> runtime = await Execute(program, work, expected, name == "rotation");
            executions++;
            if (name == "overwrite")
            {
                ulong hits = FieldStoreHits(runtime);
                if (level == 0) beforeHits = hits; else afterHits = hits;
            }
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            File.WriteAllBytes(Path.Combine(work, "program.native"), bytes.ToArray());
            bytes.Position = 0;
            await Execute(NativeArtifact.Read(bytes), work, expected, name == "rotation");
            artifacts++;
        }
        Program.Require(beforeHits > afterHits,
            $"Actual overwritten field-store hits must decrease, observed {beforeHits} to {afterHits}.");
        ulong dse = Metric(publicRecords, "optimizer.pass.llvm-dse", "dse.stores.overwritten");
        ulong rotated = Metric(publicRecords, "optimizer.pass.llvm-loop-rotate", "loops.rotated");
        Program.Require(rotated > 0, "The actual public source pipeline did not rotate any of the exercised loops.");
        SaveTrace(Path.Combine(root, "component.trace.jsonl"), componentRecords);
        SaveTrace(Path.Combine(root, "public-compiler.trace.jsonl"), publicRecords);
        return new(4, executions, artifacts, before, after, removed, beforeHits, afterHits, dse, rotated);
    }

    private static (int, int, int) Component(string root, CompilationTrace trace)
    {
        string work = Path.Combine(root, "dse-component");
        Directory.CreateDirectory(work);
        string sourceFile = Path.Combine(work, "source.msync");
        File.WriteAllText(sourceFile, Overwrite);
        HirModule hir = Compiler.Analyze(Overwrite, sourceFile);
        File.WriteAllText(Path.Combine(work, "typed-hir.txt"), HirPrinter.Print(hir));
        LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(hir);
        foreach (LlvmFunction function in graph.Module.Functions.Where(function => !function.IsDeclaration))
        {
            LlvmDominatorTree tree = new(function);
            LlvmPromoteMemoryToRegister.Run(function, tree);
            LlvmEarlyCse.Run(function, tree: tree);
            LlvmSccp.Run(function);
            LlvmDeadCodeElimination.Run(function);
            LlvmInstSimplifyPass.Run(function);
            LlvmAdce.Run(function, new LlvmDominatorTree(function, isPostDominator: true));
        }
        LlvmFunction target = graph.Module.Functions.Single(function => function.Name == "overwrite");
        LlvmStoreInstruction[] beforeStores = target.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmStoreInstruction>().ToArray();
        LlvmStoreInstruction[] armStores = beforeStores.Where(store => store.Source is { } source
            && (Overwrite.AsSpan(source.Start, source.Length).StartsWith("row::left = 1", StringComparison.Ordinal)
                || Overwrite.AsSpan(source.Start, source.Length).StartsWith("row::left = 3", StringComparison.Ordinal))).ToArray();
        Program.Require(armStores.Length == 2, "Both real diamond-arm field stores must survive the common pre-DSE passes.");
        File.WriteAllText(Path.Combine(work, "before-dse.ll"), LlvmIrWriter.Write(graph.Module));
        foreach (LlvmFunction function in graph.Module.Functions.Where(function => !function.IsDeclaration))
        {
            Dictionary<string, ulong> metrics = [];
            LlvmDominatorTree tree = new(function, trace), post = new(function, trace, isPostDominator: true);
            LlvmCycleInfo cycles = new(function, trace, metrics);
            LlvmAAResults alias = new(function, tree: tree, metrics: metrics);
            using LlvmMemorySsa memory = new(function, alias, tree, trace, metrics);
            LlvmDeadStoreElimination.Run(function, alias, memory, tree, post,
                new LlvmTargetLibraryInfo(graph.Module), cycles, trace: trace);
        }
        int after = target.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmStoreInstruction>().Count();
        int removed = armStores.Count(store => store.Parent is null);
        Program.Require(removed == 2 && beforeStores.Length > after,
            "The actual DSE component must delete both overwritten arm stores, not inherit unrelated O2 credit.");
        File.WriteAllText(Path.Combine(work, "after-dse.ll"), LlvmIrWriter.Write(graph.Module));
        return (beforeStores.Length, after, removed);
    }

    private static async Task<IReadOnlyList<RuntimeProfileSample>> Execute(NativeProgram program, string work, double expected, bool rotation)
    {
        RuntimeProfile profile = new(instructionSites: true);
        await using (NativeInterpreter interpreter = new(program, new ExecutionOptions(WorkingDirectory: work)
        { Profile = profile, Tiers = new NativeTierOptions(NativeJitPolicy.Off) }))
        {
            NativeValue value = await interpreter.ExecuteAsync();
            Program.Require(value.AsNumber() == expected, $"{work}: expected {expected}, actual {value.AsNumber()}.");
        }
        if (rotation)
            Program.Require(File.ReadAllText(Path.Combine(work, "iterations.txt")) == "xxxxxnnnnnnee",
                "Loop rotation changed actual zero/one/multiple/nested/early-exit IO counts or order.");
        IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
        File.WriteAllLines(Path.Combine(work, "runtime.profile.jsonl"), samples.Select(sample => JsonSerializer.Serialize(sample)));
        return samples;
    }

    private static ulong FieldStoreHits(IEnumerable<RuntimeProfileSample> samples) => samples.Where(sample =>
        sample.Name == "vm.instruction" && sample.Attributes.GetValueOrDefault("opcode") == "Store"
        && sample.Attributes.GetValueOrDefault("function.name") == "overwrite"
        && sample.Attributes.TryGetValue("source.offset", out string? offset)
        && sample.Attributes.TryGetValue("source.length", out string? length)
        && Overwrite.AsSpan(int.Parse(offset, CultureInfo.InvariantCulture), int.Parse(length, CultureInfo.InvariantCulture))
            .StartsWith("row::left =", StringComparison.Ordinal))
        .Aggregate(0UL, (total, sample) => total + sample.Metrics.GetValueOrDefault("hits"));
    private static ulong Metric(IEnumerable<CompilationObservation> records, string name, string metric) => records
        .Where(record => record.Name == name).Aggregate(0UL, (total, record) => total + (record.Metrics?.GetValueOrDefault(metric) ?? 0));
    private static void SaveTrace(string path, IEnumerable<CompilationObservation> records) =>
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(new
        { record.Name, record.Outcome, record.Attributes, record.Metrics, error = record.Error?.Message })));
}
