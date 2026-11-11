using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal static class NaturalLoops
{
    internal static async Task<int> VerifyAsync(string root)
    {
        int checks = 0;
        foreach (var (start, step, comparison, inputs) in new[]
        {
            ("0", "1", ">=", new[] { "-Infinity", "-1", "-0", "0", "0.1", "3", "3.1", "4", "4.1", "8", "9", "101" }),
            ("-3", "2", ">", new[] { "-Infinity", "-5", "-3", "-1.5", "0", "3", "3.1", "5", "11", "32" }),
            ("9007199254740980", "1", ">=", new[] { "9007199254740980", "9007199254740985", "9007199254740988", "9007199254740992" })
        })
        {
            string source = Program(start, step, comparison, "line as Number");
            var baseline = Compiler.Compile(source, "scalar-loop", 0);
            foreach (int level in new[] { 2, 3 })
            {
                var optimized = Compiler.Compile(source, "scalar-loop", level);
                Require(optimized.Functions.Any(function => function.Instructions.Count(i => i.Op == OpCode.Multiply) >= 5),
                    "Runtime-bounded scalar loop did not emit its fast region and exact fallback");
                Require(optimized.Functions.Any(function => function.Instructions.Count(i => i.Op == OpCode.StoreLocal) <
                    2 * function.Instructions.Count(i => i.Op == OpCode.Multiply)), "Private recurrence did not carry on the operand stack");
                var portable = RoundTrip(optimized);
                var bound = RoundTrip(Binding.Bind(portable, new(root, root)));
                foreach (string input in inputs)
                {
                    long expected = await Execute(baseline, input);
                    Require(await Execute(optimized, input) == expected, $"O{level} changed scalar input {input}");
                    Require(await Execute(portable, input) == expected, $".mso changed scalar input {input}");
                    Require(await Execute(bound, input) == expected, $".mexe changed scalar input {input}");
                }
            }
        }

        foreach (string bound in new[] { "0", "1", "3.1", "4" })
        {
            string source = Program("0", "1", ">=", bound);
            var baseline = Compiler.Compile(source, "finite-scalar-loop", 0);
            var optimized = Compiler.Compile(source, "finite-scalar-loop", 2);
            Require(optimized.Functions.All(function => !function.Instructions.Select((instruction, pc) =>
                instruction.Op == OpCode.Jump && instruction.A <= pc).Contains(true)), "Known short loop retained a backedge");
            Require(await Execute(optimized, "ignored") == await Execute(baseline, "ignored"), "Exact small expansion changed result");
        }

        string captured = """
            let i = 0;
            let x = 0;
            let read = fn { i };
            let line? = $std::io::read_line();
            if line? { 0 } else {
                let n = line as Number;
                loop {
                    if i >= n { break; };
                    x = x + read();
                    i = i + 1;
                };
                x
            }
            """;
        var shared = Compiler.Compile(captured, "captured-loop", 2);
        Require(await Execute(shared, "9") == await Execute(Compiler.Compile(captured, "captured-loop", 0), "9"),
            "A captured induction slot stopped observing individual iterations");

        var signedZero = Compiler.Compile(Program("0", "1", ">=", "line as Number")
            .Replace("let x = 0", "let x = -0", StringComparison.Ordinal)
            .Replace(" + 0.125", "", StringComparison.Ordinal), "signed-zero-loop", 2);
        Require(await Execute(signedZero, "9") == BitConverter.DoubleToInt64Bits(-0d),
            "Stack-carried recurrence did not preserve negative zero");

        var infinite = Compiler.Compile(Program("0", "1", ">=", "line as Number"), "nan-loop", 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            await Execute(infinite, "NaN", cancellation.Token);
            throw new InvalidOperationException("NaN changed a nonterminating source loop into a finite loop");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { checks++; }
        return checks;

        async Task<long> Execute(Module module, string input, CancellationToken cancellationToken = default)
        {
            using var reader = new StringReader(input + "\n");
            Value value = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root) { Input = reader }, cancellationToken);
            return BitConverter.DoubleToInt64Bits(((NumberValue)value).Data);
        }

        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static string Program(string start, string step, string comparison, string bound) => $$"""
        fn run(n: Number) {
            let i = {{start}};
            let x = 0;
            loop {
                if i {{comparison}} n { break; };
                x = x * 1.0000000001 + 0.125;
                i = i + {{step}};
            };
            x
        };
        let line? = $std::io::read_line();
        if line? { 0 } else { run({{bound}}) }
        """;

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
