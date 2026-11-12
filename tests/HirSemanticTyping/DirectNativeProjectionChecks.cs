using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

internal static class DirectNativeProjectionChecks
{
    internal static async Task Run()
    {
        const string projection = """
            let wrapper = scope { math: $std::math };
            wrapper::math::abs(-7)
            """;
        const string effects = """
            let math = $std::math;
            let state = scope { trace: 0 };
            let answer = ({ state::trace = 1; math })::abs({
                state::trace = state::trace * 10 + 2;
                -7
            });
            state::trace * 10 + answer
            """;
        foreach (int level in new[] { 0, 2 })
        {
            await CheckStages(projection, level, 7, static (hir, callee) =>
                hir.Node(callee).Kind == HirKind.NativeFunction);
            await CheckStages(effects, level, 127, static (hir, callee) =>
                hir.Node(callee).Kind == HirKind.MemberRead);
        }

        var compiler = new CompilerSession(optimization: 2);
        var runtime = new ExecutionSession();
        async Task<Value> Submit(string source)
        {
            var value = await runtime.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(runtime.GetLocalValue);
            return value;
        }
        CheckNumber(await Submit("""
            let math = scope { abs: $std::math::abs };
            fn use() { math::abs(-7) };
            use()
            """), 7);
        CheckNumber(await Submit("math::abs = fn(value: Number) { value }; use()"), -7);
        await CheckPortableDispatch();
        Console.WriteLine("PASS direct native lowering, effect order, round-trips and live session replacement");
    }

    private static async Task CheckPortableDispatch()
    {
        const string source = """
            fn apply(left: Bool, value: Number) {
                let operation = if left { fn(x) { x + 1 } } else { fn(x) { x * 2 } };
                operation(value)
            };
            apply(true, 10) * 100 + apply(false, 10)
            """;
        foreach (int level in new[] { 0, 2, 3 })
        {
            var portable = Compiler.CompileInstructions(source, optimization: level);
            if (level == 0 && !portable.Functions.SelectMany(function => function.Instructions)
                .Any(instruction => instruction is { Op: OpCode.Call, Text: not null }))
                throw new InvalidOperationException("Expected a portable dispatch table.");
            foreach (var image in new[] { portable, Binding.Bind(portable, new(Environment.CurrentDirectory, Environment.CurrentDirectory)) })
            {
                using MemoryStream artifact = new();
                Artifact.Write(image, artifact);
                artifact.Position = 0;
                CheckNumber(await Interpreter.ExecuteAsync(Artifact.Read(artifact)), 1120);
            }
        }
        Module invalid = new([], [
            new("bad-dispatch", 0, 0,
                [new(OpCode.MakeClosure, 1), new(OpCode.Call, Text: "1:1;1:1"), new(OpCode.Return)], TypeRef.Null),
            new("target", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)
        ]);
        using MemoryStream malformed = new();
        Artifact.WriteProduced(invalid, malformed);
        malformed.Position = 0;
        try { _ = Artifact.Read(malformed); }
        catch (MsyncException error) when (error.Message.Contains("Invalid compiled function dispatch entry", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("The reader accepted a duplicate dispatch alternative.");
    }

    private static async Task CheckStages(string source, int level, double expected,
        Func<HirModule, HirNodeId, bool> checkCallee)
    {
        var analyzed = Compiler.Analyze(source, "direct-native-projection.msync");
        using MemoryStream stream = new();
        HirArtifact.Write(analyzed, stream);
        stream.Position = 0;
        var hir = HirArtifact.Read(stream);
        var call = hir.Nodes.Single(node => node.Kind == HirKind.Call
            && hir.Calls[node.Data].NativeName == "math.abs");
        if (!checkCallee(hir, hir.Children(call)[0]))
            throw new InvalidOperationException("Native call has the wrong callee evaluation plan.");
        var portable = Compiler.LowerInstructions(hir, level);
        foreach (var code in new[] { portable, Binding.Bind(portable, new(Environment.CurrentDirectory, Environment.CurrentDirectory)) })
        {
            using MemoryStream artifact = new();
            Artifact.Write(code, artifact);
            artifact.Position = 0;
            CheckNumber(await Interpreter.ExecuteAsync(Artifact.Read(artifact)), expected);
        }
    }

    private static void CheckNumber(Value value, double expected)
    {
        if (value is not NumberValue number || number.Data != expected)
            throw new InvalidOperationException($"Expected {expected}, got {value}.");
    }
}
