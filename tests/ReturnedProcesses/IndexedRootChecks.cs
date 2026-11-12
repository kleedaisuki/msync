using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class IndexedRootChecks
{
    internal static async Task Run(string executable)
    {
        string childExecutable = executable.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
        string source = $$"""
            fn make() {
                let child = $std::process::spawn("{{childExecutable}}", ["--child"]);
                let pending = $std::ranges::transform([1], fn(value: Number) {
                    $std::io::println("enumerated");
                    value
                });
                scope {
                    inputs: $std::storage::vector_new([pending]),
                    children: $std::storage::vector_new([child]),
                }
            };
            let result = make();
            let saved = $std::storage::vector_snapshot(result::children);
            $std::storage::vector_clear(result::children);
            let child = $std::ranges::front(saved);
            $std::process::write_stdin(child, "vector");
            $std::process::close_stdin(child);
            $std::process::stdout($std::process::wait(child))
            """;
        Module image = Compiler.Compile(source, "indexed-ownership-roots.msync", 2);
        var context = new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory);
        foreach (var (label, module) in new[] {
            ("source", image), ("mso", RoundTrip(image)), ("mexe", RoundTrip(Binding.Bind(image, context)))
        })
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            using StringWriter output = new();
            Value result = await Interpreter.ExecuteAsync(module,
                new(WorkingDirectory: Environment.CurrentDirectory, Output: output), timeout.Token);
            if (result is not StringValue { Data: "echo:vector" })
                throw new InvalidOperationException($"{label}: the live Process stored in the vector was not transferred.");
            if (output.GetStringBuilder().Length != 0)
                throw new InvalidOperationException($"{label}: retention enumerated a lazy Range stored in a vector.");
            Console.WriteLine($"PASS {label}: live vector Process, independent snapshot, no lazy sibling enumeration");
        }
    }

    internal static async Task RunArena(string executable)
    {
        string childExecutable = executable.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
        string source = $$"""
            let Set = $std::containers::Set;
            fn make() {
                let child = $std::process::spawn("{{childExecutable}}", ["--child"]);
                let set = Set::with_policy(Process, scope {
                    Self: Process,
                    sample?: null,
                    less: fn(left: Process, right: Process) { false },
                });
                Set::insert(set, child);
                set
            };
            let set = make();
            let saved = Set::snapshot(set);
            let child = $std::ranges::front(saved);
            Set::remove(set, child);
            $std::process::write_stdin(child, "arena");
            $std::process::close_stdin(child);
            $std::process::stdout($std::process::wait(child))
            """;
        Module image = Compiler.Compile(source, "set-arena-ownership-roots.msync", 2);
        var context = new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory);
        foreach (var (label, module) in new[] {
            ("source", image), ("mso", RoundTrip(image)), ("mexe", RoundTrip(Binding.Bind(image, context)))
        })
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            Value result = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: Environment.CurrentDirectory), timeout.Token);
            if (result is not StringValue { Data: "echo:arena" })
                throw new InvalidOperationException($"{label}: Set arena lost the original live Process key.");
            Console.WriteLine($"PASS {label}: live Process escapes through Set arena, snapshot survives erase");
        }
    }

    private static Module RoundTrip(Module image)
    {
        using MemoryStream stream = new();
        Artifact.Write(image, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
