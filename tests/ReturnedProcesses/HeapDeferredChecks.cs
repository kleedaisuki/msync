using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class HeapDeferredChecks
{
    internal static async Task Run(string executable)
    {
        string childPath = executable.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
        string source = $$"""
            let Heap = $std::containers::Heap;
            fn make() {
                let child = $std::process::spawn("{{childPath}}", ["--child"]);
                let status = $std::process::exit_code(child);
                object { child: child, queue: Heap::new([status]) }
            };
            let state = make();
            let saved = Heap::snapshot(state::queue);
            $std::process::write_stdin(state::child, "heap");
            $std::process::close_stdin(state::child);
            let selected? = Heap::pop(state::queue);
            let snapshot? = $std::ranges::front(saved);
            object {
                selected_ok: if selected is not null and selected == 0 { true } else { false },
                snapshot_ok: if snapshot is not null and snapshot == 0 { true } else { false },
                output: $std::process::stdout($std::process::wait(state::child)),
            }
            """;
        var image = Compiler.Compile(source, "heap-deferred-owner.msync", 2);
        var context = new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory);
        foreach (var (label, module) in new[] {
            ("source", image), ("mso", RoundTrip(image)), ("mexe", RoundTrip(Binding.Bind(image, context)))
        })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = (ScopeValue)await Interpreter.ExecuteAsync(module,
                new(WorkingDirectory: Environment.CurrentDirectory), timeout.Token);
            if (result["selected_ok"] is not BoolValue { Data: true }
                || result["snapshot_ok"] is not BoolValue { Data: true }
                || result["output"] is not StringValue { Data: "echo:heap" })
                throw new InvalidOperationException($"{label}: dense Heap lost a deferred Number or joined its child early.");
            Console.WriteLine($"PASS {label}: vector creation and snapshot retain a gated Process Number until selected");
        }
    }

    private static Module RoundTrip(Module image)
    {
        using var stream = new MemoryStream();
        Artifact.Write(image, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
