using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

internal static class RecursiveCaptureChecks
{
    private const string Walk = "fn walk(n) { let snapshot = n; if n == 3 { first = fn { snapshot }; }; if n == 0 { null } else { walk(n - 1) } }; ";

    public static async Task Run()
    {
        int checks = 0;
        foreach (int level in new[] { 0, 2, 3 })
        {
            foreach ((string source, double expected) in new (string, double)[]
            {
                ("let first = fn { 0 }; " + Walk + "walk(3); first()", 3),
                ("let first = fn { 0 }; " + Walk + "walk(3); let saved = first; walk(7); saved() + first()", 6),
                ("let first = fn { 9 }; " + Walk + "walk(2); first()", 9),
                ("let first = fn { 0 }; fn set(value) { if value > 0 { first = fn { value }; }; }; set(4); let saved = first; set(7); saved() + first()", 11),
                ("let first = fn { 0 }; fn set(value) { if value > 0 { first = fn { value }; }; }; set(4); first = fn { 8 }; set(-1); first()", 8),
                ("fn apply(operation, value) { operation(value) }; fn inc(value) { value + 1 }; apply(fn(value) { apply(inc, value) }, 4)", 5),
                ("let op = fn { 1 }; fn read() { op() }; read(); op = fn { 7 }; read()", 7)
            })
            {
                HirModule hir = Compiler.Analyze(source, "recursive-captures.msync");
                using MemoryStream graph = new();
                HirArtifact.Write(hir, graph);
                graph.Position = 0;
                Module code = HirLowerer.Lower(HirArtifact.Read(graph), new(OptimizationLevel: level)).Module;
                using MemoryStream artifact = new();
                Artifact.Write(code, artifact);
                artifact.Position = 0;
                Require(await Interpreter.ExecuteAsync(Artifact.Read(artifact)), expected, $"O{level}: {source}");
                checks++;
            }
            CompilerSession compiler = new(optimization: level);
            await using ExecutionSession execution = new();
            async Task<Value> Submit(string source)
            {
                Value result = await execution.ExecuteAsync(compiler.Compile(source));
                compiler.AcceptSubmission(execution.GetLocalValue);
                return result;
            }
            await Submit("let first = fn { 0 }; " + Walk);
            await Submit("walk(3); let saved = first;");
            await Submit("walk(7);");
            Require(await Submit("saved() + first()"), 6, $"O{level}: session capture locations");
            checks++;
        }
        Console.WriteLine($"PASS {checks} recursive capture journeys");
    }

    private static void Require(Value value, double expected, string context)
    {
        if (value is not NumberValue number || number.Data != expected)
            throw new InvalidOperationException($"{context}: expected {expected}, received {value}");
    }
}
