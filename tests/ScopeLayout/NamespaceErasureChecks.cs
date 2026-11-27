using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

internal static class NamespaceErasureChecks
{
    private const string Pure = """
        object Ops { step: fn(x: Number) -> Number { x + 1 } };
        Ops::step(7)
        """;

    private const string Live = """
        object Ops {
            step: fn(x: Number) -> Number { x + 1 },
        };
        $std::io::println(Ops::step(7) as String);
        Ops::step = fn(x: Number) -> Number { x + 2 };
        $std::io::println(Ops::step(7) as String);
        fn wrap(seed: Number) {
            object { step: fn(x: Number) -> Number { x + seed } }
        };
        let one = wrap(3);
        let two = wrap(5);
        $std::io::println(one::step(7) as String);
        $std::io::println(two::step(7) as String);
        fn receiver(ops) {
            $std::io::println("receiver");
            ops
        };
        fn argument() -> Number {
            $std::io::println("argument");
            7
        };
        receiver(Ops)::step(argument())
        """;

    internal static async Task Run()
    {
        BindingContext context = new(Environment.CurrentDirectory, Environment.CurrentDirectory);
        foreach (int level in new[] { 0, 2 })
        {
            var pure = Compiler.Compile(Pure, "erased-namespace", level);
            Require(pure.StaticScopes.Count == 0 && !pure.Functions.SelectMany(function => function.Instructions).Any(instruction =>
                instruction.Op is OpCode.NewScope or OpCode.LoadField or OpCode.LoadStaticScope or OpCode.LoadStaticField),
                "A static code symbol must not allocate or read an unobserved namespace.");
            var live = Compiler.Compile(Live, "live-namespace", level);
            Require(live.Functions.SelectMany(function => function.Instructions).Any(instruction =>
                instruction.Op == OpCode.LoadField && live.FieldProjections[instruction.A].Name == "step"),
                "Captured or computed callees must still obtain their actual environment.");
            foreach (var (plain, observed) in new[]
            {
                (pure, live), (RoundTrip(pure), RoundTrip(live)),
                (LowerDetached(Pure, level), LowerDetached(Live, level)),
                (RoundTrip(Binding.Bind(pure, context)), RoundTrip(Binding.Bind(live, context)))
            })
            {
                Require(await Interpreter.ExecuteAsync(plain) is NumberValue { Data: 8 },
                    "Erasing a namespace changed the direct symbol result.");
                using StringWriter output = new() { NewLine = "\n" };
                Require(await Interpreter.ExecuteAsync(observed, new() { Output = output })
                    is NumberValue { Data: 9 } && output.ToString() == "8\n9\n10\n12\nreceiver\nargument\n",
                    "Member replacement, actual closure environments, or receiver order changed.");
            }
        }
        Console.WriteLine("PASS: namespace erasure, live callee environments, source and detached artifacts");
    }

    private static Module LowerDetached(string source, int level)
    {
        var analyzed = Compiler.Analyze(source, "namespace");
        using MemoryStream stream = new();
        HirArtifact.Write(analyzed with
        { Sources = [.. analyzed.Sources.Select(document => document with { Text = null })] }, stream);
        stream.Position = 0;
        return Compiler.LowerInstructions(HirArtifact.Read(stream), level);
    }

    private static Module RoundTrip(Module image)
    {
        using MemoryStream stream = new();
        Artifact.Write(image, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
