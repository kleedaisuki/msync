using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;
using static PortableCallFixture;

internal static class CallTargetChecks
{
    private static readonly MethodInfo Inline = typeof(Optimizer).Assembly
        .GetType("Msync.Optimization.FunctionInlining")!.GetMethod("Inline", BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static async Task<int> RunAsync(string root)
    {
        int checks = 0;

        var diamond = Diamond(sameTarget: true);
        await Check("same callee, distinct captures", diamond, 0, [("", 11), ("input", 12)]);
        await Check("distinct callees", Diamond(sameTarget: false), 1, [("", 11), ("input", 8)]);
        await Check("iterator exhausted and item edges", Iteration(), 0, [("", 12)]);
        await Check("a backedge kills a changed callable", Reassignment(), 1, [("", 22)]);
        await Check("a value read from a Cell is not a shared parameter", CellArgument(), 0, [("", 4)]);
        Require(Rejects(CellArgument(raw: true), "call argument cannot be a raw location"),
            "A raw captured location cannot cross the ordinary argument boundary");
        await Check("call may replace an exposed local callable", CapturedWrite(), 1, [("", 22)]);
        await Check("an already evaluated callable outlives argument effects", EvaluatedCallee(), 0, [("", 11)]);
        Require(Rejects(ExcessCaptureAlias(), "specialization changes the capture count"),
            "An imported direct call cannot hide extra captured aliases in private target locals");
        RecursiveComponent();
        await Product();
        await CapturedLoop();
        return checks;

        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks++;
        }

        async Task Check(string label, Module before, int calls, (string Input, double Result)[] examples)
        {
            Artifact.Validate(before);
            var after = (Module)Inline.Invoke(null, [before, 2])!;
            Require(after.Functions[0].Instructions.Count(item => item.Op == OpCode.Call) == calls, label + ": residual calls");
            foreach (var (input, result) in examples)
                foreach (var image in new[] { before, RoundTrip(after), RoundTrip(Binding.Bind(after, new(root, root))) })
                    Require(await Execute(image, input) == result, label + ": execution result");
        }

        async Task<double> Execute(Module image, string input)
        {
            using StringReader reader = new(input);
            var value = await Interpreter.ExecuteAsync(image with { OptimizationLevel = 0 },
                new(WorkingDirectory: root, Output: TextWriter.Null) { Input = reader });
            return ((NumberValue)value).Data;
        }

        async Task Product()
        {
            const string source = """
                fn kernel(condition: Bool, x: Number) {
                    let selected = fn(n: Number) { n + 1 };
                    if condition { $std::io::println("yes"); };
                    selected(x)
                };
                let input? = $std::io::read_line();
                if input? { 0 } else { kernel(input == "12", input as Number) }
                """;
            foreach (int level in new[] { 2, 3 })
            {
                var portable = Compiler.Compile(source, "ipa-cfg", level);
                var kernel = portable.Functions.Single(item => item.Name == "kernel" && item.ReturnType == TypeRef.Number);
                Require(kernel.Instructions.All(item => item.Op != OpCode.Call), "product pipeline removes the local call after the branch");
                foreach (var module in new[] { portable, RoundTrip(portable), RoundTrip(Binding.Bind(portable, new(root, root))) })
                {
                    Require(await Execute(module, "12") == 13, "product true branch");
                    Require(await Execute(module, "13") == 14, "product false branch");
                }
            }
        }

        async Task CapturedLoop()
        {
            using var stream = typeof(CallTargetChecks).Assembly.GetManifestResourceStream("cfg-call-loop.msync")!;
            using StreamReader reader = new(stream);
            string source = reader.ReadToEnd();
            foreach (int level in new[] { 2, 3 })
            {
                var portable = Compiler.Compile(source, "cfg-call-loop.msync", level);
                Require(portable.Functions.SelectMany(body => body.Instructions).All(item => item.Op != OpCode.Call),
                    "captured callable loop no longer dispatches source calls");
                var bound = RoundTrip(Binding.Bind(portable, new(root, root)));
                foreach (var (input, factor) in new[] { ("31\n", 1.0000000001), ("31\npresent\n", 1.0000000002) })
                {
                    double expected = 0;
                    for (int index = 0; index < 31; index++) expected = expected * factor + 0.125;
                    foreach (var module in new[] { portable, RoundTrip(portable), bound })
                    {
                        var result = await Execute(module, input);
                        Require(BitConverter.DoubleToInt64Bits(result) == BitConverter.DoubleToInt64Bits(expected),
                            "the selected capture and binary64 evaluation order are preserved");
                    }
                }
            }
        }

        void RecursiveComponent()
        {
            var method = typeof(Optimizer).Assembly.GetType("Msync.Optimization.CallTargetGraph")!
                .GetMethod("FindRecursive", BindingFlags.NonPublic | BindingFlags.Static)!;
            // The second edge from A reaches C, which links back through
            // completed B. A DFS active-path marker misses C.
            Dictionary<int, int>[] edges =
            [
                new() { [0] = 1, [1] = 2 }, new() { [0] = 0 }, new() { [0] = 1 },
                new() { [0] = 3 }, new() { [0] = 2 }
            ];
            var result = (bool[])method.Invoke(null, [edges])!;
            Require(result.SequenceEqual([true, true, true, true, false]), "all recursive SCC members are marked");
        }
    }

    private static Module Diamond(bool sameTarget) => Image([new NumberValue(1), new NumberValue(2), new NumberValue(10)],
        [
            new("main", 0, 0,
                [new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 6),
                 new(OpCode.Constant, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.Jump, 8),
                 new(OpCode.Constant, 1), new(OpCode.MakeClosure, sameTarget ? 1 : 2, 1),
                 new(OpCode.Constant, 2), new(OpCode.Call, 1), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            CapturedArithmetic("add", OpCode.Add),
            CapturedArithmetic("subtract", OpCode.Subtract)
        ]);

    private static FunctionCode CapturedArithmetic(string name, OpCode operation) => new(name, 1, 2,
        [new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 0), new(operation), new(OpCode.Return)],
        TypeRef.Number, CaptureCount: 1, ParameterTypes: [TypeRef.Number])
        { CodeSignature = new([Number], Number, [new(Number, false)]) };

    private static Module Iteration() => Image([new NumberValue(1), new NumberValue(2), new NumberValue(10)],
        [
            new("main", 0, 3,
                [new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 0),
                 new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.MakeRange, 2), new(OpCode.GetIterator), new(OpCode.InitializeLocal, 1),
                 new(OpCode.IteratorNext, 1, 14), new(OpCode.InitializeLocal, 2), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 2),
                 new(OpCode.Call, 1), new(OpCode.Pop), new(OpCode.Jump, 7),
                 new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), new(OpCode.Call, 1), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            new("increment", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.Return)],
                TypeRef.Number, ParameterTypes: [TypeRef.Number]) { CodeSignature = new([Number], Number, []) }
        ]);

    private static Module Reassignment() => Image(
        [new NumberValue(11), new NumberValue(22), new NumberValue(0), new NumberValue(1), new NumberValue(2)],
        [
            new("main", 0, 3,
                [new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 0), new(OpCode.Constant, 2), new(OpCode.InitializeLocal, 1),
                 new(OpCode.Constant, 2), new(OpCode.InitializeLocal, 2),
                 new(OpCode.LoadLocal, 1), new(OpCode.Constant, 4), new(OpCode.GreaterEqual), new(OpCode.JumpIfTrue, 20),
                 new(OpCode.LoadLocal, 0), new(OpCode.Call), new(OpCode.StoreLocal, 2), new(OpCode.MakeClosure, 2), new(OpCode.StoreLocal, 0),
                 new(OpCode.LoadLocal, 1), new(OpCode.Constant, 3), new(OpCode.Add), new(OpCode.StoreLocal, 1), new(OpCode.Jump, 6),
                 new(OpCode.LoadLocal, 2), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            LiteralFunction("first", 0),
            LiteralFunction("second", 1)
        ]);

    private static Module CellArgument(bool raw = false)
    {
        Instruction[] read = raw ? [] : [new(OpCode.Pop), new(OpCode.LoadLocal, 0)];
        return Image([new NumberValue(4), new NumberValue(9)],
        [
            new("main", 0, 1,
                [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.MakeClosure, 1),
                 new(OpCode.LoadCell, 0, Number + 1), .. read,
                 new(OpCode.Call, 1), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            new("replace", 1, 1,
                [new(OpCode.Constant, 1), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Return)],
                TypeRef.Number, ParameterTypes: [TypeRef.Number]) { CodeSignature = new([Number], Number, []) }
        ]);
    }

    private static Module CapturedWrite() => Image([new NumberValue(11), new NumberValue(22)],
        [
            new("main", 0, 1,
                [new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, Callable + 1), new(OpCode.MakeClosure, 3, 1),
                 new(OpCode.Call), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Call), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            LiteralFunction("first", 0),
            LiteralFunction("second", 1),
            Rebind()
        ]);

    private static Module EvaluatedCallee() => Image([new NumberValue(11), new NumberValue(22)],
        [
            new("main", 0, 1,
                [new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0),
                 new(OpCode.LoadCell, 0, Callable + 1), new(OpCode.MakeClosure, 3, 1), new(OpCode.Call), new(OpCode.Call, 1), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            LiteralFunction("first", 0, ignoresArgument: true),
            LiteralFunction("second", 1, ignoresArgument: true),
            Rebind()
        ]);

    private static FunctionCode Rebind() => new("rebind", 0, 1,
        [new(OpCode.MakeClosure, 2), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)],
        TypeRef.Null, CaptureCount: 1) { CodeSignature = new([], Null, [new(Callable, true)]) };

    private static Module ExcessCaptureAlias() => Image([new NumberValue(11), new NumberValue(22)],
        [
            new("main", 0, 1,
                [new(OpCode.MakeClosure, 1), new(OpCode.InitializeLocal, 0),
                 new(OpCode.LoadCell, 0, Callable + 1), new(OpCode.LoadCell, 0, Callable + 1),
                 new(OpCode.MakeClosure, 3, 2), new(OpCode.Call, 0, 5), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) },
            LiteralFunction("first", 0),
            LiteralFunction("second", 1),
            new("carrier", 0, 2, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 2)
                { CodeSignature = new([], Null, [new(Callable, true), new(Callable, true)]) },
            new("extra-captures", 0, 2,
                [new(OpCode.MakeClosure, 1), new(OpCode.StoreLocal, 0), new(OpCode.MakeClosure, 2), new(OpCode.StoreLocal, 1),
                 new(OpCode.LoadLocal, 0), new(OpCode.Call), new(OpCode.Return)], TypeRef.Number)
                 { CodeSignature = new([], Number, []) }
        ]);

    private static FunctionCode LiteralFunction(string name, int index, bool ignoresArgument = false) =>
        new(name, ignoresArgument ? 1 : 0, ignoresArgument ? 1 : 0,
            [new(OpCode.Constant, index), new(OpCode.Return)], TypeRef.Number)
        { CodeSignature = new(ignoresArgument ? [Any] : [], Number, []) };

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}

internal static class PortableCallFixture
{
    internal const int Number = 0, BoolCode = 1, TextCode = 2, Null = 3, Callable = 4, Any = 5;

    internal static Module Image(IReadOnlyList<Value> constants, IReadOnlyList<FunctionCode> functions) =>
        new(constants, functions, OptimizationLevel: 0)
        {
            CodeTypes = [
                new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.String), new(TypeKind.Null),
                new(TypeKind.Function), new(TypeKind.Any)
            ]
        };

    internal static bool Rejects(Module module, string message)
    {
        using MemoryStream bytes = new();
        Artifact.WriteProduced(module, bytes);
        bytes.Position = 0;
        try { _ = Artifact.Read(bytes); }
        catch (MsyncException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return true; }
        return false;
    }
}
