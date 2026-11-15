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

internal sealed record EarlyLoopReceipt(int SourceCases, int Executions, int DuplicateMultipliesBefore,
    int DuplicateMultipliesAfter, ulong DuplicateMultiplyHitsBefore, ulong DuplicateMultiplyHitsAfter,
    ulong KernelArithmeticHitsBefore, ulong KernelArithmeticHitsAfterLicm, ulong ScalarCseErased, ulong LoadCseErased,
    ulong LicmInstructionsHoisted, ulong LicmPromotionExitStores);

internal static class EarlyLoopJourney
{
    private sealed record Case(string Name, string Source, double Expected);
    private static readonly Case[] Cases =
    [
        new("duplicate-products", "fn duplicates(a: Number, b: Number) { a * b + a * b }; duplicates(2, 3)", 12),
        new("memory-callback-kill", """
            fn memory(seed: Number) {
                let item = scope { value: seed };
                let before = item::value;
                item::value = seed + 1;
                let written = item::value;
                fn mutate() { item::value = item::value + 5; };
                mutate();
                before * 100 + written * 10 + item::value
            };
            memory(2)
            """, 238),
        new("loop-memory-alias", """
            fn accumulate(limit: Number) {
                let item = scope { value: 1 }; let alias = item; let index = 0;
                loop {
                    if index >= limit { break; };
                    alias::value = item::value + index;
                    index = index + 1;
                };
                item::value
            };
            accumulate(4)
            """, 7),
        new("loop-callback-clobber", """
            fn mutate(row: Scope) { row::value = row::value + 1; };
            let item = scope { value: 1 }; let index = 0; let total = 0;
            loop {
                if index >= 3 { break; };
                let before = item::value;
                mutate(item);
                total = total + before * 10 + item::value;
                index = index + 1;
            };
            total
            """, 69),
        new("nested", """
            let outer = 0; let total = 0;
            loop {
                if outer >= 3 { break; };
                let inner = 0;
                loop {
                    if inner >= 2 { break; };
                    total = total + outer * 10 + inner;
                    inner = inner + 1;
                };
                outer = outer + 1;
            };
            total + outer
            """, 66),
        new("early-exits", """
            let index = 0; let total = 0;
            loop {
                index = index + 1;
                if index == 3 { break; };
                if index >= 8 { break; };
                total = total + index;
            };
            total * 10 + index
            """, 33),
        new("iteration-captures", """
            let first = fn { 0 }; let second = fn { 0 }; let third = fn { 0 };
            for item in [1, 2, 3] {
                if item == 1 { first = fn { item }; } else {
                    if item == 2 { second = fn { item }; } else { third = fn { item }; };
                };
            };
            first() + second() * 10 + third() * 100
            """, 321),
        new("invariant-kernel", """
            fn kernel(limit: Number, scale: Number, shift: Number) {
                let index = 0; let total = 0;
                loop {
                    if index >= limit { break; };
                    let invariant = scale * scale + shift;
                    total = total + invariant + index;
                    index = index + 1;
                };
                total
            };
            kernel(8, 3, 1)
            """, 108),
        new("zero-trip", """
            fn zero(limit: Number, scale: Number, shift: Number) {
                let index = 0; let total = 0;
                loop {
                    if index >= limit { break; };
                    let invariant = scale * scale + shift;
                    total = total + invariant;
                    index = index + 1;
                };
                total
            };
            zero(0, 3, 1)
            """, 0)
    ];

    internal static async Task<EarlyLoopReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "early-loops");
        List<CompilationObservation> records = [];
        CompilationTrace trace = new(records.Add, includeOptimizerPasses: true);
        int executions = 0, productsBefore = 0, productsAfter = 0;
        ulong productHitsBefore = 0, productHitsAfter = 0, kernelBefore = 0, kernelAfter = 0;
        foreach (Case test in Cases)
        foreach (bool optimize in new[] { false, true })
        {
            string work = Path.Combine(root, test.Name, optimize ? "early-cse-full-loop-simplify-lcssa-licm" : "mem2reg");
            Directory.CreateDirectory(work);
            string sourceFile = Path.Combine(work, "source.msync");
            File.WriteAllText(sourceFile, test.Source);
            HirModule hir = Compiler.Analyze(test.Source, sourceFile);
            File.WriteAllText(Path.Combine(work, "typed-hir.txt"), HirPrinter.Print(hir));
            LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(hir, trace);
            foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
                LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function, trace), trace);
            File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
            if (optimize)
            foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
            {
                LlvmEarlyCse.Run(function, trace);
                LlvmDominatorTree tree = new(function, trace);
                LlvmLoopInfo loops = new(function, tree, trace);
                LlvmLoopSimplify.Run(function, tree, loops, preserveLcssa: false, trace: trace);
                // Run consumes and updates the actual mutable analyses; LCSSA uses those same instances.
                LlvmLcssa.Run(function, tree, loops, trace);
                Program.Require(loops.TopLevelLoops.All(loop => loop.IsRecursivelyLCSSAForm(tree, loops)),
                    "Full LoopSimplify plus LCSSA left actual source loops outside loop-closed SSA.");
                Dictionary<string, ulong> memoryMetrics = [];
                LlvmAAResults alias = new(function, graph.Module.DataLayout, tree, memoryMetrics);
                using LlvmMemorySsa memory = new(function, alias, tree, trace, memoryMetrics);
                LlvmMemorySsaUpdater updater = new(memory);
                LlvmTargetLibraryInfo library = new(graph.Module);
                LlvmTargetTransformInfo target = new(graph.Module.DataLayout);
                foreach (LlvmLoop loop in loops.GetLoopsInPreorder().Reverse().ToArray())
                    LlvmLicm.Run(loop, loops, tree, alias, updater, library, target, trace: trace);
                Program.Require(loops.TopLevelLoops.All(loop => loop.IsRecursivelyLCSSAForm(tree, loops)),
                    "LICM motion/promotion broke real loop-closed SSA.");
            }
            File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(graph.Module));
            if (test.Name == "duplicate-products")
            {
                int count = graph.Module.Functions.SelectMany(function => function.BasicBlocks)
                    .SelectMany(block => block.Instructions).Count(instruction => instruction.Opcode == LlvmOpcode.FMul);
                if (optimize) productsAfter = count; else productsBefore = count;
            }
            NativeProgram program = LlvmNativeLowerer.Lower(graph, trace);
            RuntimeProfile profile = new(instructionSites: true);
            await using (NativeInterpreter interpreter = new(program,
                new ExecutionOptions(WorkingDirectory: work, SourceFile: sourceFile)
                { Profile = profile, Tiers = new NativeTierOptions(NativeJitPolicy.Off) }))
            {
                NativeValue value = await interpreter.ExecuteAsync();
                Program.Require(value.AsNumber() == test.Expected,
                    $"{test.Name}/{optimize}: expected {test.Expected}, actual {value.AsNumber()}.");
            }
            IReadOnlyList<RuntimeProfileSample> runtime = profile.Drain();
            File.WriteAllLines(Path.Combine(work, "runtime.profile.jsonl"), runtime.Select(sample => JsonSerializer.Serialize(sample)));
            if (test.Name == "duplicate-products")
            {
                ulong hits = BinaryHits(runtime, test.Source, "a * b", exact: true);
                if (optimize) productHitsAfter = hits; else productHitsBefore = hits;
            }
            if (test.Name == "invariant-kernel")
            {
                ulong hits = BinaryHits(runtime, test.Source, "scale * scale + shift", exact: false);
                if (optimize) kernelAfter = hits; else kernelBefore = hits;
            }
            executions++;
        }
        ulong scalar = Metric(records, "erase.scalar-cse"), loads = Metric(records, "erase.load-cse");
        Program.Require(productsBefore == 2 && productsAfter == 1 && productHitsBefore == 2 && productHitsAfter == 1 && scalar > 0,
            "Real duplicate a*b must lose one IR multiply and one actually executed multiply, not merely report a pass.");
        Program.Require(kernelBefore == 16 && kernelAfter == 2,
            $"Actual invariant arithmetic must improve from 16 to 2 executed operations; observed {kernelBefore} to {kernelAfter}.");
        File.WriteAllLines(Path.Combine(root, "compiler.trace.jsonl"), records.Select(record => JsonSerializer.Serialize(new
        { record.Name, record.Outcome, record.Attributes, record.Metrics, error = record.Error?.Message })));
        return new(Cases.Length, executions, productsBefore, productsAfter, productHitsBefore, productHitsAfter,
            kernelBefore, kernelAfter, scalar, loads,
            LicmMetric(records, "instructions.hoisted"), LicmMetric(records, "promotion.exit_stores"));
    }

    private static ulong LicmMetric(IEnumerable<CompilationObservation> records, string metric) => records
        .Where(record => record.Name == "optimizer.pass.llvm-licm")
        .Aggregate(0UL, (total, record) => total + (record.Metrics?.GetValueOrDefault(metric) ?? 0));

    private static ulong Metric(IEnumerable<CompilationObservation> records, string metric) => records
        .Where(record => record.Name == "optimizer.pass.llvm-early-cse")
        .Aggregate(0UL, (total, record) => total + (record.Metrics?.GetValueOrDefault(metric) ?? 0));

    private static ulong BinaryHits(IEnumerable<RuntimeProfileSample> samples, string source, string expression, bool exact)
    {
        int start = source.IndexOf(expression, StringComparison.Ordinal), end = start + expression.Length;
        return samples.Where(sample => sample.Name == "vm.instruction"
            && sample.Attributes.GetValueOrDefault("opcode") == "Binary"
            && sample.Attributes.TryGetValue("source.offset", out string? offsetText)
            && sample.Attributes.TryGetValue("source.length", out string? lengthText)
            && Matches(source, expression, exact, start, end,
                int.Parse(offsetText, CultureInfo.InvariantCulture), int.Parse(lengthText, CultureInfo.InvariantCulture)))
            .Aggregate(0UL, (total, sample) => total + sample.Metrics.GetValueOrDefault("hits"));
    }
    private static bool Matches(string source, string expression, bool exact, int start, int end, int offset, int length) =>
        exact ? source.AsSpan(offset, length).SequenceEqual(expression.AsSpan()) : offset >= start && offset + length <= end;
}
