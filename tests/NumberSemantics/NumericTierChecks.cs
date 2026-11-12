using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

internal static class NumericTierChecks
{
    internal static async Task Run(string root, Action<bool, string> require)
    {
        const string source = """
            fn run(n: Number) {
                let i = 0;
                let x = 0;
                loop {
                    if i >= n { break; };
                    x = x * 1.0000000001 + 0.125;
                    i = i + 1;
                };
                x
            };
            let line? = $std::io::read_line();
            if line? { 0 } else { run(line as Number) }
            """;
        using MemoryStream semantic = new();
        HirArtifact.Write(Compiler.Analyze(source, "numeric-tier"), semantic);
        semantic.Position = 0;
        var mso = RoundTrip(Optimizer.Optimize(HirLowerer.Lower(HirArtifact.Read(semantic)).Module));
        var mexe = RoundTrip(Binding.Bind(mso, new(root, root)));

        foreach (var image in new[] { mso, mexe })
            foreach (int n in new[] { 0, 7, 8, 9, 1024 })
            {
                var profile = new RuntimeProfile(instructionSites: true);
                var options = new ExecutionOptions(WorkingDirectory: root)
                    { Profile = profile, Input = new StringReader(n.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
                var result = (NumberValue)await Interpreter.ExecuteAsync(image, options);
                double expected = 0;
                for (int i = 0; i < n; i++) expected = expected * 1.0000000001 + 0.125;
                require(Bits(result.Data) == Bits(expected), $"O2 {image.Stage} recurrence with {n} iterations");
                var samples = profile.Drain();
                if (n is 0 or 7 or 1024)
                    require(Metric(samples, "numeric_loop_entries") == (n == 1024 ? 1UL : 0UL),
                        $"O2 {image.Stage}/{n}: only a hot residual loop enters the tier");
                if (n == 1024)
                    require(Metric(samples, "numeric_loop_operations") < Metric(samples, "instructions"), "Physical operations are tracked separately");
            }

        foreach (int n in new[] { 7, 8, 100, 101 })
        {
            var (tiered, tieredSamples) = await RunFrame(Frame(n, captured: false));
            var (captured, capturedSamples) = await RunFrame(Frame(n, captured: true));
            require(Bits(tiered.Data) == (n % 2 == 0 ? long.MinValue : 0L) &&
                Bits(captured.Data) == Bits(tiered.Data), "Signed zero survives the tier and Cell fallback");
            require(Metric(tieredSamples, "numeric_loop_entries") == (n < 8 ? 0UL : 1UL) &&
                Metric(capturedSamples, "numeric_loop_entries") == 0, "A Cell is never treated as a private Number slot");
            if (n > 8)
                require(Metric(tieredSamples, "number_boxes") < Metric(capturedSamples, "number_boxes"), "Exit materializes only changed slots");
            require(LogicalPath(tieredSamples).SequenceEqual(LogicalPath(capturedSamples)),
                "Tier preserves source-PC instruction and control-flow counts");
        }

        // Aggregate profiling runs the counted native variant where available;
        // compare the returned Number bits without formatting through the CLI.
        var (negativeZero, nativeSamples) = await RunFrame(Frame(1024, captured: false), instructionSites: false);
        var (positiveZero, _) = await RunFrame(Frame(1025, captured: false), instructionSites: false);
        var (_, interpretedSamples) = await RunFrame(Frame(1024, captured: false));
        require(Bits(negativeZero.Data) == long.MinValue && Bits(positiveZero.Data) == 0L,
            "Numeric tier preserves signed zero across a hot native boundary");
        require(Metric(nativeSamples, "instructions") == Metric(interpretedSamples, "instructions"),
            "Counted native execution preserves logical opcode totals");

        async Task<(NumberValue Result, IReadOnlyList<RuntimeProfileSample> Samples)> RunFrame(Module module, bool instructionSites = true)
        {
            var profile = new RuntimeProfile(instructionSites);
            var result = (NumberValue)await Interpreter.ExecuteAsync(RoundTrip(module), new(WorkingDirectory: root) { Profile = profile });
            return (result, profile.Drain());
        }
    }

    private static Module Frame(int n, bool captured) => new(
        [new NumberValue(0), new NumberValue(1), new NumberValue(n), new NumberValue(-0d)],
        [new("signed_zero", 0, 2,
            [new(OpCode.Constant, 0), new(OpCode.StoreLocal, 0), new(OpCode.Constant, 3), new(OpCode.StoreLocal, 1),
             new(captured ? OpCode.LoadCell : OpCode.LoadLocal, 1), new(OpCode.Pop),
             new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), new(OpCode.Less), new(OpCode.JumpIfFalse, 20),
             new(OpCode.LoadLocal, 1), new(OpCode.Constant, 1), new(OpCode.Multiply), new(OpCode.Negate), new(OpCode.StoreLocal, 1),
             new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.StoreLocal, 0), new(OpCode.Jump, 6),
             new(OpCode.LoadLocal, 1), new(OpCode.Return)], TypeRef.Number)]);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static ulong Metric(IReadOnlyList<RuntimeProfileSample> samples, string key) =>
        samples.Where(sample => sample.Name == "vm.function").Aggregate(0UL, (total, sample) => total + sample.Metrics[key]);

    private static IEnumerable<(string, string, string?, ulong)> LogicalPath(IReadOnlyList<RuntimeProfileSample> samples) =>
        samples.Where(sample => sample.Name is "vm.instruction" or "vm.edge")
            .Select(sample => (sample.Name, sample.Attributes["instruction.pc"], sample.Attributes.GetValueOrDefault("target.pc"), sample.Metrics["hits"]));

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
