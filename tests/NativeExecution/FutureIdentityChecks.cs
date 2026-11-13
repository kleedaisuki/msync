using System.Globalization;
using Msync.Compiler;
using Msync.Core.Native;
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
            ("binary", "let saved = child::exit_code; let a = saved + 1; let b = saved + 1;", "[a, b]", "2\n8\n")
        ];
        int checks = 0;
        foreach (var item in cases)
            foreach (int level in new[] { 0, 2 })
            {
                string directory = Path.Combine(root, $"future-identity-{item.Name}-O{level}");
                Directory.CreateDirectory(directory);
                File.Delete(Path.Combine(directory, "ready"));
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
                StringWriter output = new(CultureInfo.InvariantCulture), errors = new(CultureInfo.InvariantCulture);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
                await using NativeInterpreter interpreter = new(Compiler.CompileNative(source, file, level), new(directory, file, Output: output, Error: errors));
                await interpreter.ExecuteAsync(timeout.Token);
                if (output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) != item.Expected || errors.ToString().Length != 0)
                    throw new Exception($"Future identity {item.Name} O{level}: expected {item.Expected}, got {output}; errors={errors}");
                checks++;
            }
        Console.WriteLine("PASS future-identity: copied pending value is one member; independent projections/computations remain distinct, O0/O2");
        checks += await NonDemandingTypeTests(root);
        return checks;
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
