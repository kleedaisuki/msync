using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class FutureIdentityChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        if (OperatingSystem.IsWindows()) return 0;
        (string Name, string Definitions, string Items, string Expected)[] cases =
        [
            ("copy", "let saved = child::exit_code; let a = saved; let b = saved;", "[a, b]", "1\n7\n"),
            ("projection", "", "[child::exit_code, child::exit_code]", "2\n7\n"),
            ("binary", "let saved = child::exit_code; let a = saved + 1; let b = saved + 1;", "[a, b]", "2\n8\n"),
            ("unary", "let saved = child::exit_code; let a = -saved; let b = -saved;", "[a, b]", "2\n-7\n"),
            ("comparison", "let saved = child::exit_code; let a = saved > 3; let b = saved > 3;", "[a, b]", "2\ntrue\n"),
            ("field", "let row = object { n: 0 }; row::n = child::exit_code; let a = row::n + 1; let b = row::n + 1;",
                "[a,b]", "2\n8\n"),
            ("capture", "let saved = child::exit_code; fn twice() { let a = saved + 1; let b = saved + 1; [a,b] };",
                "twice()", "2\n8\n"),
            ("parameter", "fn twice(n: Number) { let a = n + 1; let b = n + 1; [a,b] };",
                "twice(child::exit_code)", "2\n8\n"),
            ("range-callback", """
                let saved = child::exit_code;
                fn increment(n: Number) -> Number { n + 1 };
                """, "$std::ranges::transform([saved,saved], increment)", "2\n8\n"),
            ("iteration", """
                let saved = child::exit_code;
                let accumulated = [0];
                for n in [saved,saved] {
                    accumulated = $std::ranges::materialize($std::ranges::concat(accumulated, [n + 1]));
                };
                """, "$std::ranges::drop(accumulated, 1)", "2\n8\n"),
            ("demand", """
                let trigger = release /bin/sh("-c", "exit 0");
                let a = child::exit_code + 1;
                let b = if trigger::exit_code == 0 { a } else { 3 };
                """, "[a,b]", "1\n8\n")
        ];
        int checks = 0;
        foreach (var item in cases)
            for (int level = 0; level <= 3; ++level)
            {
                string directory = Path.Combine(root, $"future-identity-{item.Name}-O{level}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                string source = $$"""
                    let child = release /bin/sh("-c", "while [ ! -f ready ]; do sleep 0.01; done; exit 7");
                    {{item.Definitions}}
                    let values = $std::ranges::materialize($std::ranges::unique({{item.Items}}));
                    $std::io::println($std::ranges::size(values) as String);
                    $std::io::write_text(./ready, "go");
                    $std::io::println($std::ranges::front(values) as String);
                    """;
                string file = Path.Combine(directory, "program.msync");
                await File.WriteAllTextAsync(file, source);
                NativeProgram program = Compiler.CompileNative(source, file, level);
                await Execute(program, "source");
                using MemoryStream portable = new();
                NativeArtifact.Write(program, portable);
                portable.Position = 0;
                File.Delete(file);
                NativeProgram detached = NativeArtifact.Read(portable);
                await Execute(detached, "portable");
                using MemoryStream executable = new();
                NativeArtifact.Write(NativePreparation.Prepare(detached, new BindingContext(directory, directory, null)), executable);
                executable.Position = 0;
                await Execute(NativeArtifact.Read(executable), "prepared");

                async Task Execute(NativeProgram image, string form)
                {
                    File.Delete(Path.Combine(directory, "ready"));
                    using StringWriter output = new(CultureInfo.InvariantCulture), errors = new(CultureInfo.InvariantCulture);
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
                    await using NativeInterpreter interpreter = new(image, new(directory, file, Output: output, Error: errors));
                    await interpreter.ExecuteAsync(timeout.Token);
                    if (output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) != item.Expected || errors.ToString().Length != 0)
                        throw new Exception($"Future identity {item.Name} O{level}/{form}: expected {item.Expected}, got {output}; errors={errors}");
                    checks++;
                }
            }
        Console.WriteLine("PASS future-identity: distinct evaluations, copies, selected arm, field, capture and call, O0–O3 across source/detached portable/prepared");
        checks += await NonDemandingTypeTests(root);
        checks += ReadyAndDeferredCarriers(root);
        checks += await ReadySequenceItems(root);
        return checks;
    }

    private static async Task<int> ReadySequenceItems(string root)
    {
        const string source = """
            let child = release /bin/sh("-c", "while [ ! -f ready ]; do sleep 0.01; done; exit 4");
            let sequence = $std::ranges::iota(0, child::exit_code);
            $std::io::println("queued");
            $std::io::write_text(./ready, "go");
            let sum = 0;
            for item in sequence { sum = sum + item; };
            $std::io::println(sum as String);
            """;
        string directory = Path.Combine(root, $"pending-sequence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        List<CompilationObservation> observations = [];
        var program = Compiler.CompileNative(source, Path.Combine(directory, "sequence.msync"), 2,
            new CompilationTrace(observations.Add, includeOptimizerPasses: true));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var vm = new NativeInterpreter(program, new(directory, Output: output));
        await vm.ExecuteAsync(stop.Token);
        var decision = observations.Single(observation => observation.Name == "compiler.llvm.lower.readiness.function"
            && observation.Attributes?.GetValueOrDefault("function.name") == "<main>");
        if (output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) != "queued\n6\n"
            || decision.Metrics?.GetValueOrDefault("scalar.ready") != 1
            || decision.Metrics?.GetValueOrDefault("scalar.lifted") != 0)
            throw new Exception("A deferred sequence bound must not block construction or make its items pending.");
        return 1;
    }

    private static int ReadyAndDeferredCarriers(string root)
    {
        List<CompilationObservation> trace = [];
        const string source = """
            fn ready(n: Number) -> Number { n * n + 1 };
            fn delayed(n: Number) -> Bool { if n + 1 > 3 { true } else { false } };
            let child = release /bin/sh("-c", "exit 7");
            let a = ready(3);
            if delayed(child::exit_code) { a } else { 0 }
            """;
        _ = Compiler.CompileNative(source, Path.Combine(root, "scalar-carriers.msync"), 2,
            new CompilationTrace(trace.Add, includeOptimizerPasses: true));
        var ready = Find("ready");
        var delayed = Find("delayed");
        if (ready.GetValueOrDefault("scalar.ready") != 2 || ready.GetValueOrDefault("scalar.lifted") != 0
            || delayed.GetValueOrDefault("scalar.lifted") != 2 || delayed.GetValueOrDefault("demand.retained") != 1
            || !trace.Any(record => record.Name == "compiler.llvm.target.whole_ssa"
                && record.Attributes?.GetValueOrDefault("function.name")?.StartsWith("ready", StringComparison.Ordinal) == true
                && record.Metrics?.GetValueOrDefault("ssa.whole_functions") == 1))
            throw new Exception("Deferred carrier selection must not replace an unrelated ready scalar body or erase the pending Bool demand.");
        return 1;

        IReadOnlyDictionary<string, ulong> Find(string name) => trace.Single(record =>
            record.Name == "compiler.llvm.lower.readiness.function"
            && record.Attributes?.GetValueOrDefault("function.name")?.StartsWith(name, StringComparison.Ordinal) == true).Metrics!;
    }

    private static async Task<int> NonDemandingTypeTests(string root)
    {
        int checks = 0;
        foreach (int level in new[] { 0, 2 })
            foreach (bool number in new[] { true, false })
            {
                string directory = Path.Combine(root, $"pending-type-O{level}-{number}");
                Directory.CreateDirectory(directory);
                File.Delete(Path.Combine(directory, "ready"));
                string source = """
                    let child = release /bin/sh("-c", "while [ ! -f ready ]; do sleep 0.01; done; exit 7");
                    let kind = if $std::io::read_line() is null { Number } else { Bool };
                    let value = child::exit_code;
                    $std::io::println((value is kind) as String);
                    $std::io::write_text(./ready, "go");
                    $std::io::println(value as String);
                    """;
                NativeProgram program = Compiler.Compile(source, Path.Combine(directory, "pending-type.msync"), level);
                using MemoryStream bytes = new();
                NativeArtifact.Write(program, bytes);
                bytes.Position = 0;
                StringWriter output = new(CultureInfo.InvariantCulture), errors = new(CultureInfo.InvariantCulture);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
                await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes), new(directory, Output: output, Error: errors)
                { Input = new StringReader(number ? "" : "bool\n") });
                await interpreter.ExecuteAsync(timeout.Token);
                string expected = number ? "true\n7\n" : "false\n7\n";
                if (output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) != expected || errors.ToString().Length != 0)
                    throw new Exception($"Pending Type test O{level}: expected {expected}, got {output}; errors={errors}");
                checks++;
            }
        Console.WriteLine("PASS pending-type: dynamic Type targets inspect producer contracts before child release, O0/O2 artifacts");
        return checks;
    }
}
