using System.Globalization;
using System.Text;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record AdceReceipt(int SourceExecutions, int ArtifactExecutions, int BranchesBefore, int BranchesAfter,
    int PhisBefore, int PhisAfter, ulong DeadRecurrenceHitsBefore, ulong DeadRecurrenceHitsAfter,
    int PreservedFileReads, int PreservedFileWrites, int PreservedRangeCalls, int PendingBoolFailures);

internal static class AdceJourney
{
    private const string ControlSource = """
        fn dead_branch(flag: Bool, a: Number, b: Number) {
            if flag { let unused = a * b; } else { let unused = a + b; };
            7
        };
        fn dead_recurrence(limit: Number) {
            let index = 0; let dead = 0;
            loop {
                if index >= limit { break; };
                dead = dead + index * 2;
                index = index + 1;
            };
            9
        };
        dead_branch(true, 2, 3) + dead_recurrence(4)
        """;
    private const string EffectSource = """
        let effects = 0;
        fn __llvm_profile_instrument_target(value: Number) {
            $std::io::write_text(./profile-name.txt, "written"); value
        };
        __llvm_profile_instrument_target(1);
        fn note(value: Number) { effects = effects * 10 + value; value };
        let values = [note(1), note(2)];
        let row = scope { count: 0 };
        fn bump() { row::count = row::count + 1; note(3); };
        bump();
        [note(4)];
        let text = $std::io::read_text(./input.txt);
        $std::io::write_text(./output.txt, text);
        $std::io::append_text(./output.txt, "!");
        $std::io::write_text(./joined.txt, "");
        for line in $std::io::read_lines(./input.txt) {
            $std::io::append_text(./joined.txt, line);
            $std::io::append_text(./joined.txt, "|");
        };
        $std::io::write_bytes(./bytes-out.bin, $std::io::read_bytes(./input.bin));
        effects * 100 + row::count * 10 + $std::ranges::size(values)
        """;

    internal static async Task<AdceReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "adce");
        Directory.CreateDirectory(root);
        List<CompilationObservation> records = [];
        CompilationTrace trace = new(records.Add, includeOptimizerPasses: true);
        (int branchesBefore, int branchesAfter, int phisBefore, int phisAfter) = ComponentControl(root, trace);
        (int reads, int writes, int ranges) = ComponentEffects(root, trace);
        int executions = 0, artifacts = 0;
        ulong hitsBefore = 0, hitsAfter = 0;
        foreach (int level in new[] { 0, 2 })
        foreach ((string name, string source, double expected) in new[]
        { ("control", ControlSource, 16d), ("effects", EffectSource, 123412d) })
        {
            string work = Path.Combine(root, "O" + level, name);
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, "source.msync"), source);
            NativeProgram program = Compiler.Compile(source, Path.Combine(work, "source.msync"), level, trace);
            IReadOnlyList<RuntimeProfileSample> profile = await Execute(program, work, expected);
            executions++;
            if (name == "control")
            {
                ulong hits = RecurrenceHits(profile);
                if (level == 0) hitsBefore = hits; else hitsAfter = hits;
            }
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            File.WriteAllBytes(Path.Combine(work, "program.native"), bytes.ToArray());
            bytes.Position = 0;
            NativeProgram decoded = NativeArtifact.Read(bytes);
            await Execute(decoded, work, expected);
            artifacts++;
        }
        Program.Require(hitsBefore == 8 && hitsAfter == 0,
            $"Dead recurrence arithmetic must actually disappear: expected 8 to 0, got {hitsBefore} to {hitsAfter}.");
        int pendingFailures = await PendingBool(root);
        File.WriteAllLines(Path.Combine(root, "compiler.trace.jsonl"), records.Select(record => JsonSerializer.Serialize(new
        { record.Name, record.Outcome, record.Attributes, record.Metrics, error = record.Error?.Message })));
        return new(executions, artifacts, branchesBefore, branchesAfter, phisBefore, phisAfter,
            hitsBefore, hitsAfter, reads, writes, ranges, pendingFailures);
    }

    private static (int, int, int, int) ComponentControl(string root, CompilationTrace trace)
    {
        LlvmHirLoweringResult graph = Component(ControlSource, Path.Combine(root, "component-control"));
        LlvmFunction function = graph.Module.Functions.Single(function => function.Name == "dead_branch");
        int before = function.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmCondBrInstruction>().Count();
        int phis = Code(graph).OfType<LlvmPhiInstruction>().Count();
        File.WriteAllText(Path.Combine(root, "component-control", "before.ll"), LlvmIrWriter.Write(graph.Module));
        RunAdce(graph, trace);
        int after = function.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmCondBrInstruction>().Count();
        int afterPhis = Code(graph).OfType<LlvmPhiInstruction>().Count();
        File.WriteAllText(Path.Combine(root, "component-control", "after.ll"), LlvmIrWriter.Write(graph.Module));
        Program.Require(before == 1 && after == 0 && phis > afterPhis,
            "Actual source ADCE must remove dead finite branch control and a dead loop-carried SSA recurrence.");
        return (before, after, phis, afterPhis);
    }

    private static (int, int, int) ComponentEffects(string root, CompilationTrace trace)
    {
        string work = Path.Combine(root, "component-effects");
        LlvmHirLoweringResult graph = Component(EffectSource, work);
        int reads = Code(graph).OfType<LlvmFileInInst>().Count(), writes = Code(graph).OfType<LlvmFileOutInst>().Count(),
            stores = Code(graph).OfType<LlvmStoreInstruction>().Count(), ranges = RangeCalls(graph);
        File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
        RunAdce(graph, trace);
        File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(graph.Module));
        Program.Require(reads == 3 && writes > 0 && stores > 0 && ranges > 0
            && Code(graph).OfType<LlvmFileInInst>().Count() == reads
            && Code(graph).OfType<LlvmFileOutInst>().Count() == writes
            && Code(graph).OfType<LlvmStoreInstruction>().Count() == stores && RangeCalls(graph) == ranges,
            "ADCE discarded actual FileIn/Out, stores or ordinary Range constructor effects.");
        return (reads, writes, ranges);
    }

    private static LlvmHirLoweringResult Component(string source, string work)
    {
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "source.msync"), source);
        HirModule hir = Compiler.Analyze(source, Path.Combine(work, "source.msync"));
        File.WriteAllText(Path.Combine(work, "typed-hir.txt"), HirPrinter.Print(hir));
        LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(hir);
        foreach (LlvmFunction function in graph.Module.Functions.Where(function => !function.IsDeclaration))
            LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function));
        return graph;
    }
    private static void RunAdce(LlvmHirLoweringResult graph, CompilationTrace trace)
    {
        foreach (LlvmFunction function in graph.Module.Functions.Where(function => !function.IsDeclaration))
            LlvmAdce.Run(function, new LlvmDominatorTree(function, trace, isPostDominator: true),
                new LlvmDominatorTree(function, trace), trace: trace);
    }
    private static IEnumerable<LlvmInstruction> Code(LlvmHirLoweringResult graph) => graph.Module.Functions
        .SelectMany(function => function.BasicBlocks).SelectMany(block => block.Instructions);
    private static int RangeCalls(LlvmHirLoweringResult graph) => Code(graph).OfType<LlvmCallBase>().Count(call =>
        call.CalledFunction is { } target && graph.Constructors.GetValueOrDefault(target) == NativeKind.Range);

    private static async Task<IReadOnlyList<RuntimeProfileSample>> Execute(NativeProgram program, string work, double expected)
    {
        foreach (string name in new[] { "output.txt", "joined.txt", "bytes-out.bin", "profile-name.txt" })
            File.Delete(Path.Combine(work, name));
        File.WriteAllText(Path.Combine(work, "input.txt"), "alpha\nbeta\n", new UTF8Encoding(false));
        byte[] bytes = [0, 1, 128, 255];
        File.WriteAllBytes(Path.Combine(work, "input.bin"), bytes);
        RuntimeProfile profile = new(instructionSites: true);
        await using (NativeInterpreter interpreter = new(program, new ExecutionOptions(WorkingDirectory: work)
        { Profile = profile, Tiers = new NativeTierOptions(NativeJitPolicy.Off) }))
        {
            NativeValue actual = await interpreter.ExecuteAsync();
            Program.Require(actual.AsNumber() == expected, $"{work}: expected {expected}, actual {actual.AsNumber()}.");
        }
        if (expected == 123412)
        {
            Program.Require(File.ReadAllText(Path.Combine(work, "output.txt")) == "alpha\nbeta\n!"
                && File.ReadAllText(Path.Combine(work, "joined.txt")) == "alpha|beta|"
                && File.ReadAllBytes(Path.Combine(work, "bytes-out.bin")).AsSpan().SequenceEqual(bytes)
                && File.ReadAllText(Path.Combine(work, "profile-name.txt")) == "written",
                "Public/Native2 ADCE execution changed actual text/line/byte IO outputs.");
        }
        IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
        File.WriteAllLines(Path.Combine(work, "runtime.profile.jsonl"), samples.Select(sample => JsonSerializer.Serialize(sample)));
        return samples;
    }

    private static ulong RecurrenceHits(IEnumerable<RuntimeProfileSample> samples)
    {
        int start = ControlSource.IndexOf("dead + index * 2", StringComparison.Ordinal), end = start + "dead + index * 2".Length;
        return samples.Where(sample => sample.Name == "vm.instruction" && sample.Attributes.GetValueOrDefault("opcode") == "Binary"
            && sample.Attributes.TryGetValue("source.offset", out string? offset) && sample.Attributes.TryGetValue("source.length", out string? length)
            && int.Parse(offset, CultureInfo.InvariantCulture) >= start
            && int.Parse(offset, CultureInfo.InvariantCulture) + int.Parse(length, CultureInfo.InvariantCulture) <= end)
            .Aggregate(0UL, (total, sample) => total + sample.Metrics.GetValueOrDefault("hits"));
    }

    private static async Task<int> PendingBool(string root)
    {
        int failures = 0;
        foreach (int level in new[] { 0, 2 })
        foreach ((string variant, string consume) in new[]
        {
            ("bool", "fn discarded(flag: Bool) { if flag { 1; } else { 2; }; 7 }; discarded(pendingBool)"),
            ("match", "fn discarded_match(flag: Bool) { match flag { true => { 1; }, false => { 2; } }; 7 }; discarded_match(pendingBool)")
        })
        {
            string work = Path.Combine(root, "pending-" + variant + "-O" + level);
            Directory.CreateDirectory(work);
            string gate = Path.Combine(work, "release-bool"), sourceName = Path.Combine(work, "session.msync");
            if (File.Exists(gate)) File.Delete(gate);
            List<CompilationObservation> pendingTrace = [];
            CompilerSession compiler = new(sourceName, level, new CompilationTrace(pendingTrace.Add, includeOptimizerPasses: true));
            await using NativeExecutionSession execution = new(new ExecutionOptions(WorkingDirectory: work));
            try
            {
                const string initial = """
                    let child = release /bin/sh("-c", "while [ ! -f ./release-bool ]; do sleep 0.01; done; printf not-a-bool");
                    let pendingBool = child::stdout as Bool;
                    null
                    """;
                File.WriteAllText(Path.Combine(work, "01.msync"), initial);
                NativeProgram first = compiler.Compile(initial);
                await execution.ExecuteAsync(first);
                compiler.AcceptSubmission(execution.GetLocalValue);
                int slot = first.Functions[first.EntryPoint].Slots.Select((value, index) => (value, index))
                    .Single(pair => pair.value.Role == NativeSlotRole.Local && pair.value.Name == "pendingBool").index;
                Program.Require(execution.IsLocalInitialized(slot) && execution.GetLocalValue(slot) is null,
                    "The branch operand fixture must be an actual pending Bool, not a ready scalar.");
                File.WriteAllText(Path.Combine(work, "02.msync"), consume);
                NativeProgram second = compiler.Compile(consume);
                File.WriteAllText(Path.Combine(work, "second-native.json"), JsonSerializer.Serialize(new
                { second.Functions, second.Types, second.Layouts, second.SessionRoots }));
                File.WriteAllText(gate, "release");
                try
                {
                    NativeValue result = await execution.ExecuteAsync(second);
                    throw new InvalidOperationException($"O{level}/{variant}: consumed pending Bool failure was erased; returned {result.AsNumber()}.");
                }
                catch (MsyncException error)
                {
                    compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
                    Program.Require(error.Span is { Length: > 0 } span && span.Source == sourceName,
                        "Condition-consumed pending Bool conversion failure lost its original source context.");
                    File.WriteAllText(Path.Combine(work, "diagnostic.txt"), error.ToString());
                    failures++;
                }
            }
            finally
            {
                File.WriteAllText(gate, "release");
                File.WriteAllLines(Path.Combine(work, "compiler.trace.jsonl"), pendingTrace.Select(record => JsonSerializer.Serialize(new
                { record.Name, record.Outcome, record.Attributes, record.Metrics, error = record.Error?.Message })));
            }
        }
        return failures;
    }
}
