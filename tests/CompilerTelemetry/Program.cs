using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using Msync.Hir;
using Msync.Optimization;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class Program
{
    private const string Source = """
        fn update(flag: Bool, seed: Number) {
            let row = if flag { object { n: seed, text: "left" } }
                else { object { text: "right", n: seed + 1 } };
            let alias = row;
            alias::n = row::n + 2;
            row::n
        };
        update(true, 3) + update(false, 4) + $std::ranges::size([1, 2])
        """;

    private static async Task Main()
    {
        foreach (int level in Enumerable.Range(0, 4)) await Equivalent(level);
        Failures();
        AdmissionAndPassFailures();
        Editor();
        await Session();
        Console.WriteLine("PASS compiler telemetry: same-process artifact equivalence O0–O3, execution, failures, incremental editor and persistent session");
    }

    private static async Task Equivalent(int level)
    {
        List<CompilationObservation> rows = [];
        CompilationTrace trace = new(rows.Add, includeOptimizerPasses: true);
        NativeProgram plain = SourceCompiler.Compile(Source, "telemetry.msync", level);
        NativeProgram observed = SourceCompiler.Compile(Source, "telemetry.msync", level, trace);
        using MemoryStream first = new(), second = new();
        NativeArtifact.WriteProduced(plain, first);
        NativeArtifact.WriteProduced(observed, second, trace);
        Check(first.ToArray().AsSpan().SequenceEqual(second.ToArray()), $"O{level} telemetry changed artifact bytes");
        second.Position = 0;
        NativeProgram replay = NativeArtifact.Read(second, trace);
        await using NativeInterpreter interpreter = new(replay);
        Check((await interpreter.ExecuteAsync()).AsNumber() == 14, $"O{level} replay changed the result");
        Ownership(rows);
        Check(rows.Any(row => row.Name == "compiler.artifact.decode_section" && row.Metrics?.GetValueOrDefault("section.bytes") > 0), "Missing section byte counts");
        Check(rows.Any(row => row.Name == "compiler.semantic.instantiate" && row.Metrics?.ContainsKey("cache.miss") == true), "Missing specialization evidence");
    }

    private static void Failures()
    {
        List<CompilationObservation> rows = [];
        CompilationTrace trace = new(rows.Add, includeOptimizerPasses: true);
        OperationCanceledException expected = new("cancel compiler work");
        try
        {
            trace.Measure<int, int>("outer", 0, _ =>
                trace.Measure<int, int>("inner", 0, _ => throw expected));
            throw new InvalidOperationException("Cancellation was swallowed");
        }
        catch (OperationCanceledException error) when (ReferenceEquals(error, expected)) { }
        Check(rows.Count == 2 && rows.All(row => row.Outcome == CompilationOutcome.Cancelled && ReferenceEquals(row.Error, expected)), "Cancellation lost its cause or outcome");
        trace.Measure("after", 1, static value => value);
        Check(rows[^1].ParentSpanId is null, "Failed measurement leaked compiler ancestry");
        Ownership(rows);
    }

    private static void AdmissionAndPassFailures()
    {
        List<CompilationObservation> rows = [];
        CompilationTrace trace = new(rows.Add, includeOptimizerPasses: true);
        using MemoryStream invalid = new([0, 1, 2]);
        try { HirArtifact.Read(invalid, trace); throw new InvalidOperationException("Invalid HIR was admitted"); }
        catch (MsyncException) { }
        Check(rows.Any(row => row.Name == "compiler.hir.decode" && row.Outcome == CompilationOutcome.Error && row.Error is not null), "HIR header failure lost decode attribution");
        rows.Clear();
        NativeProgram program = SourceCompiler.Compile("let n = 1; n + 2", "bad-constant.msync", 0);
        NativeFunctionCode function = program.Functions[program.EntryPoint];
        bool replaced = false;
        NativeInstruction[] instructions = function.Instructions.Select(instruction =>
        {
            if (replaced || instruction.Op != NativeOp.Constant) return instruction;
            replaced = true;
            return instruction with { A = int.MaxValue };
        }).ToArray();
        NativeFunctionCode[] functions = program.Functions.ToArray();
        functions[program.EntryPoint] = function with { Instructions = instructions };
        Exception? failure = null;
        try { NativeOptimizer.Optimize(program with { Functions = functions }, 2, trace); }
        catch (Exception error) { failure = error; }
        Check(failure is not null && rows.Any(row => row.Name.StartsWith("optimizer.pass.", StringComparison.Ordinal)
            && ReferenceEquals(row.Error, failure)), "Failed optimizer pass lost its exact cause");
        Ownership(rows);
    }

    private static void Editor()
    {
        List<CompilationObservation> rows = [];
        CompilationTrace trace = new(rows.Add, includeOptimizerPasses: true);
        EditorAnalysisSession editor = new("incremental.msync");
        foreach (string source in new[] { "let first = 1; first", "let first = 1; first", "let first = 1;  first", "let first = 1; first + 2" })
            Check(editor.Analyze(source, default, trace).Diagnostic is null, "Incremental editor lost valid semantics");
        Check(rows.Any(row => row.Name == "editor.revision.reused"), "Missing revision reuse");
        Check(rows.Any(row => row.Name == "editor.rebase"), "Missing source rebase");
        Check(rows.Any(row => row.Name == "editor.prefix.analyze"), "Missing prefix analysis");
        Check(rows.Any(row => row.Name == "compiler.semantic.body"), "Editor omitted semantic telemetry");
        Check(rows.Any(row => row.Name == "compiler.parse.reused"), "Preparsed input manufactured a parser invocation");
        Ownership(rows);
    }

    private static async Task Session()
    {
        List<CompilationObservation> rows = [];
        CompilationTrace trace = new(rows.Add, includeOptimizerPasses: true);
        CompilerSession compiler = new("<repl>", 2, trace);
        await using NativeExecutionSession execution = new();
        foreach (string source in new[] { "let row = object { n: 1 };", "row::n = row::n + 6; row::n" })
        {
            NativeValue result = await execution.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(execution.GetLocalValue);
            if (result.Kind == NativeKind.Number) Check(result.AsNumber() == 7, "Session alias mutation changed");
        }
        Check(compiler.AnalyzeEditor("row::n + 1", execution.GetLocalValue, trace).Diagnostic is null, "Session editor lost accepted values");
        Check(rows.Count(row => row.Name == "compiler.lower") == 2, "Session lowering omitted telemetry");
        Check(rows.Any(row => row.Name == "editor.project"), "Session prompt omitted telemetry");
        Ownership(rows);
    }

    private static void Ownership(IReadOnlyList<CompilationObservation> rows)
    {
        Dictionary<string, CompilationObservation> spans = rows.Where(row => row.SpanId is not null).ToDictionary(row => row.SpanId!);
        foreach (CompilationObservation row in rows)
            if (row.ParentSpanId is { } parent)
                Check(spans.ContainsKey(parent) && parent != row.SpanId, "Compiler observation has a missing or self parent");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
