using System.Globalization;
using System.Reflection;
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
        foreach (var (recurrence, readsCounter) in new[]
        {
            ("x * 1.0000000001 + 0.125", false),
            ("x * 1.0000000001 + i % 11", true)
        })
        {
            string source = Program(start, step, comparison, "line as Number", recurrence);
            var baseline = Compiler.Compile(source, "scalar-loop", 0);
            foreach (int level in new[] { 2, 3 })
            {
                var optimized = Compiler.Compile(source, "scalar-loop", level);
                Require(optimized.Functions.Any(function => function.Instructions.Count(i => i.Op == OpCode.Multiply) >= 5),
                    "Runtime-bounded scalar loop did not emit its fast region and exact fallback");
                if (!readsCounter)
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
            string source = Program("0", "1", ">=", bound, "x * 1.0000000001 + i % 11", "x + i");
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

        string indexed = Program("0", "1", ">=", "line as Number", "x * 1.0000000001 + i % 11");
        RuntimeProfile profile = new(instructionSites: true);
        const string count = "4099";
        var unrolled = Compiler.Compile(indexed, "indexed-recurrence", 2);
        var original = Compiler.Compile(indexed, "indexed-recurrence", 1);
        long originalBits = await Execute(original, count, profile: profile);
        var originalSamples = profile.Drain();
        long unrolledBits = await Execute(unrolled, count, profile: profile);
        var unrolledSamples = profile.Drain();
        Require(originalBits == unrolledBits, "Grouped checks changed the counter-dependent result");
        Require(Backedges(originalSamples) == 4099 && Backedges(unrolledSamples) == 1027,
            "Grouped checks did not remove three out of every four backedges while preserving the tail");
        Require(Visits(originalSamples, OpCode.Remainder) == 4099 && Visits(unrolledSamples, OpCode.Remainder) == 4099,
            "Grouping changed the number of counter-dependent calculations");
        Require(Instructions(unrolledSamples) < Instructions(originalSamples),
            "Counter-dependent grouping did not reduce dynamic dispatch");
        int counterStore = indexed.IndexOf("i = i +", StringComparison.Ordinal);
        int accumulatorStore = indexed.IndexOf("x = x *", StringComparison.Ordinal);
        Require(SiteVisits(originalSamples, OpCode.StoreLocal, counterStore) == 4099
            && SiteVisits(unrolledSamples, OpCode.StoreLocal, counterStore) == 4099,
            "Grouped execution did not retain each counter update");
        Require(SiteVisits(originalSamples, OpCode.StoreLocal, accumulatorStore) == 4099
            && SiteVisits(unrolledSamples, OpCode.StoreLocal, accumulatorStore) == 1027,
            "Grouped execution retained intermediate accumulator stores");

        var plainBound = CopyBound(raw: false);
        Require(!ReferenceEquals(plainBound, OptimizeDirect(plainBound)),
            "A private bound lost its value proof across inlined argument copies");
        var rawBound = CopyBound(raw: true);
        Require(ReferenceEquals(rawBound, OptimizeDirect(rawBound)),
            "A cast target was incorrectly treated as proof that an aliased bound was a private value");

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

        async Task<long> Execute(Module module, string input, CancellationToken cancellationToken = default,
            RuntimeProfile? profile = null)
        {
            using var reader = new StringReader(input + "\n");
            Value value = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root) { Input = reader, Profile = profile },
                cancellationToken);
            return BitConverter.DoubleToInt64Bits(((NumberValue)value).Data);
        }

        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static ulong Backedges(IReadOnlyList<RuntimeProfileSample> samples) => samples
        .Where(sample => sample.Name == "vm.edge" && int.Parse(sample.Attributes["target.pc"], CultureInfo.InvariantCulture)
            < int.Parse(sample.Attributes["instruction.pc"], CultureInfo.InvariantCulture))
        .Aggregate(0UL, (total, sample) => total + sample.Metrics["hits"]);

    private static ulong Visits(IReadOnlyList<RuntimeProfileSample> samples, OpCode opcode) => samples
        .Where(sample => sample.Name == "vm.opcode" && sample.Attributes["opcode"] == opcode.ToString())
        .Aggregate(0UL, (total, sample) => total + sample.Metrics["hits"]);

    private static ulong SiteVisits(IReadOnlyList<RuntimeProfileSample> samples, OpCode opcode, int sourceOffset) => samples
        .Where(sample => sample.Name == "vm.instruction" && sample.Attributes["opcode"] == opcode.ToString()
            && sample.Attributes.TryGetValue("source.offset", out string? offset)
            && int.Parse(offset, CultureInfo.InvariantCulture) == sourceOffset)
        .Aggregate(0UL, (total, sample) => total + sample.Metrics["hits"]);

    private static ulong Instructions(IReadOnlyList<RuntimeProfileSample> samples) => samples
        .Where(sample => sample.Name == "vm.function")
        .Aggregate(0UL, (total, sample) => total + sample.Metrics["instructions"]);

    private static FunctionCode CopyBound(bool raw) => new("argument-copy", 0, 5,
        [raw ? new(OpCode.LoadStorage, 0) : new(OpCode.NativeCall, Text: "io.read_line"),
         new(OpCode.InitializeLocal, 1), new(OpCode.LoadLocal, 1), new(OpCode.Constant, 0),
         new(OpCode.NativeCall, 2, Text: "cast"), new(OpCode.InitializeLocal, 2),
         new(OpCode.LoadLocal, 2), new(OpCode.InitializeLocal, 2),
         new(OpCode.Constant, 1), new(OpCode.StoreLocal, 3), new(OpCode.Constant, 1), new(OpCode.StoreLocal, 4),
         new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 2), new(OpCode.GreaterEqual), new(OpCode.JumpIfFalse, 17),
         new(OpCode.Jump, 26),
         new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, 3), new(OpCode.Add), new(OpCode.StoreLocal, 4),
         new(OpCode.LoadLocal, 3), new(OpCode.Constant, 2), new(OpCode.Add), new(OpCode.StoreLocal, 3), new(OpCode.Jump, 12),
         new(OpCode.LoadLocal, 4), new(OpCode.Return)], TypeRef.Number, CaptureCount: raw ? 1 : 0);

    private static FunctionCode OptimizeDirect(FunctionCode function)
    {
        var assembly = typeof(Optimizer).Assembly;
        List<Value> constants = [new TypeValue(TypeRef.Number), new NumberValue(0), new NumberValue(1)];
        object interner = assembly.GetType("Msync.Optimization.ConstantInterner")!
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single().Invoke([constants]);
        var optimize = assembly.GetType("Msync.Optimization.NaturalLoopOptimization")!
            .GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (FunctionCode)optimize.Invoke(null, [function, constants, interner, 2, null])!;
    }

    private static string Program(string start, string step, string comparison, string bound,
        string recurrence = "x * 1.0000000001 + 0.125", string result = "x") => $$"""
        fn run(n: Number) {
            let i = {{start}};
            let x = 0;
            loop {
                if i {{comparison}} n { break; };
                x = {{recurrence}};
                i = i + {{step}};
            };
            {{result}}
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
