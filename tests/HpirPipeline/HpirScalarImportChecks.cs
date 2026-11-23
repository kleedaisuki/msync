using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirScalarImportChecks
{
    private const string SourceName = "hpir-scalar-imports.msync";
    private const string Source = """
        fn measured(x: Number) -> Number {
            $std::math::fma($std::math::sqrt(x), -2, 0.125)
        };
        let sample = measured(49);
        $std::io::println("λ😀");
        $std::io::println(sample as String);
        let zero = $std::math::copysign(0, -1);
        $std::io::eprint(zero as String);
        if $std::math::signbit(zero) and 2**3**2 == 512 { sample } else { 0 }
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; level++)
        {
            HpirProgram program = Compile(Source, level);
            var output = new StringWriter(CultureInfo.GetCultureInfo("de-DE")) { NewLine = "\n" };
            var error = new StringWriter(CultureInfo.InvariantCulture);
            var profile = new RuntimeProfile(instructionSites: true);
            var records = new List<RuntimeTimelineRecord>();
            double result = BitConverter.UInt64BitsToDouble(HpirExecutor.Run(program, new(Output: output, Error: error)
            {
                Profile = profile,
                Timeline = new(records.Add, "test")
            }));
            if (result != -13.875 || output.ToString() != "λ😀\n-13.875\n" || error.ToString() != "-0")
                throw new Exception($"Typed scalar imports O{level}: {result}; stdout={output}; stderr={error}");
            var samples = profile.Drain().Where(sample => sample.Name == "vm.native").ToArray();
            if (!samples.Any(sample => sample.Attributes["native.name"] == "math.fma")
                || !samples.Any(sample => sample.Attributes["native.name"] == "math.signbit")
                || !samples.Any(sample => sample.Attributes["native.name"] == "format.number"
                    && sample.Attributes.ContainsKey("allocation.layout"))
                || !records.Any(record => record.Name == "hpir.link.text_literals"
                    && record.Metrics.GetValueOrDefault("literals.entries") > 0)
                || !records.Any(record => record.Name == "hpir.host.write"
                    && record.Attributes["native.name"] == "io.println" && record.Attributes["source.file"] == SourceName))
                throw new Exception($"Typed scalar imports O{level}: missing service, layout or source attribution");
        }

        HpirProgram parsed = Compile("\"  -0x2a \" as Number", 2);
        if (BitConverter.UInt64BitsToDouble(HpirExecutor.Run(parsed)) != -42
            || !parsed.Imports.Any(import => import.Service == HpirService.ParseF64))
            throw new Exception("Ready HPIR textual Number parsing lost invariant base-prefixed semantics.");
        CheckMachineMath();

        HpirProgram failedMath = Compile("$std::math::clamp(0, 1, -1)", 2);
        try { _ = HpirExecutor.Run(failedMath); throw new Exception("HPIR clamp accepted reversed limits."); }
        catch (MsyncException failure) when (failure.Message == "math.clamp requires lower <= upper."
            && failure.Span?.Source == SourceName) { }

        HpirProgram failedWrite = Compile("$std::io::println($std::math::sqrt(144) as String);", 2);
        using var broken = new BrokenOutput();
        try { _ = HpirExecutor.Run(failedWrite, new(Output: broken)); throw new Exception("HPIR discarded writer failure."); }
        catch (MsyncException failure) when (failure.Message.Contains("sink closed", StringComparison.Ordinal)
            && failure.InnerException is IOException && failure.Span?.Source == SourceName) { }
    }

    private static void CheckMachineMath()
    {
        if (!HpirMachineImage.IsSupported) return;
        const string source = """
            fn sample(n: Number, zero: Number) {
                $std::io::println($std::math::floor(n) as String);
                $std::io::println($std::math::ceil(n) as String);
                $std::io::println($std::math::trunc(n) as String);
                $std::io::println($std::math::round_even(n) as String);
                $std::io::println($std::math::abs(n) as String);
                $std::io::println($std::math::sqrt(zero) as String);
                $std::io::println($std::math::floor(zero) as String);
            };
            sample(-2.5, $std::math::copysign(0, -1));
            """;
        HpirProgram program = Compile(source, 2);
        const string expected = "-3\n-2\n-2\n-2\n2.5\n-0\n-0\n";
        using StringWriter interpreted = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
        _ = HpirExecutor.Run(program, new(Output: interpreted));
        var profile = new RuntimeProfile(instructionSites: true);
        List<RuntimeTimelineRecord> samples = [];
        RuntimeTimeline trace = new(samples.Add, "machine-math");
        using StringWriter native = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using var image = HpirMachineImage.Compile(program, observe: true, trace);
        _ = HpirExecutor.RunMachineCandidate(image, new(Output: native) { Profile = profile });
        var counts = profile.Drain().Where(sample => sample.Name == "vm.native").ToArray();
        bool round = System.Runtime.Intrinsics.X86.Sse41.IsSupported;
        if (interpreted.ToString() != expected || native.ToString() != expected
            || !counts.Any(sample => sample.Attributes["native.name"] == "math.sqrt"
                && sample.Metrics.GetValueOrDefault("machine_inline_calls") == 1)
            || counts.Where(sample => sample.Attributes["native.name"] == "math.floor")
                .Aggregate(0UL, (sum, sample) => sum + sample.Metrics.GetValueOrDefault("machine_inline_calls")) != (round ? 2UL : 0)
            || !samples.Any(sample => sample.Name == "hpir.machine.emit.function"
                && sample.Attributes.GetValueOrDefault("function.name") == "sample"
                && sample.Metrics.GetValueOrDefault("guest.inline_service_sites") == (round ? 7UL : 2UL)))
            throw new InvalidOperationException($"Ready math machine selection or attribution differs from the physical image: {native}");
    }

    private static HpirProgram Compile(string source, int level)
    {
        HpirProgram produced = Compiler.LowerHpir(Compiler.Analyze(source, SourceName), level);
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(produced, image);
        image.Position = 0;
        HpirProgram decoded = HpirArtifact.ReadTrusted(image);
        string directory = Environment.CurrentDirectory;
        HpirProgram bound = HpirBinding.Prepare(decoded, new(directory, directory, SourceName));
        using var executable = new MemoryStream();
        HpirArtifact.WriteProduced(bound, executable);
        executable.Position = 0;
        return HpirArtifact.ReadTrusted(executable);
    }

    private sealed class BrokenOutput : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("sink closed"));
    }
}
