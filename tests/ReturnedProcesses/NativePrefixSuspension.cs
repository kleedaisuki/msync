using System.Runtime.InteropServices;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class NativePrefixSuspension
{
    private const string Source = """
        let io = $std::io;
        fn locate(rows, threshold: Number) -> Number {
            let index = $std::arena::root(rows);
            loop {
                if index == 0 { return 0; };
                if $std::arena::key(rows, index) >= threshold {
                    io::println("waiting");
                    let response? = io::read_line();
                    if response? { return 0; };
                    return index + (response as Number);
                };
                index = $std::arena::child(rows, index, true);
            }
        };
        let rows = $std::arena::new(Number);
        let previous = 0;
        for i in $std::ranges::iota(1, 257) {
            let row = $std::arena::allocate(rows, i * 23 % 127, previous);
            if previous == 0 { $std::arena::set_root(rows, row); }
            else { $std::arena::set_child(rows, previous, true, row); };
            previous = row;
        };
        let checksum = 0;
        for _ in $std::ranges::iota(0, 96) { checksum = checksum + locate(rows, 200); };
        checksum + locate(rows, 0)
        """;

    internal static async Task Run()
    {
        var portable = Compiler.CompileInstructions(Source, "native-prefix-suspension.msync", 2);
        var bound = Binding.BindInstructions(portable, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
        foreach (var (name, module) in new[] { ("source", portable), ("object", RoundTrip(portable)), ("bound", RoundTrip(bound)) })
            await Check(name, module, cancel: false);
        await Check("bound-cancel", RoundTrip(bound), cancel: true);
    }

    private static async Task Check(string name, Module module, bool cancel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var input = new GateReader();
        using var output = new StringWriter { NewLine = "\n" };
        var profile = new RuntimeProfile();
        var options = new ExecutionOptions(Output: output) { Input = input, Profile = profile };
        var pending = ExecutionHost.Create(module, options, timeout.Token).ExecuteAsync(timeout.Token).AsTask();
        await input.Requested.Task.WaitAsync(timeout.Token);
        if (pending.IsCompleted || output.ToString() != "waiting\n")
            throw new InvalidOperationException($"{name}: expected one visible event before the incomplete read.");
        if (cancel)
        {
            timeout.Cancel();
            try
            {
                _ = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                throw new InvalidOperationException("Cancellation did not leave the pending read.");
            }
            catch (OperationCanceledException) when (pending.IsCanceled) { }
        }
        else
        {
            input.Response.SetResult("41");
            if (await pending.WaitAsync(timeout.Token) is not NumberValue { Data: 42 })
                throw new InvalidOperationException($"{name}: live return state changed.");
        }
        if (input.Calls != 1 || output.ToString() != "waiting\n")
            throw new InvalidOperationException($"{name}: selected effects or live return state changed.");
        ulong prefixes = 0, pauses = 0, continuations = 0;
        foreach (var sample in profile.Drain())
        {
            prefixes += sample.Metrics.GetValueOrDefault("native_worker_prefix_exits");
            pauses += sample.Metrics.GetValueOrDefault("dispatch_pauses");
            continuations += sample.Metrics.GetValueOrDefault("dispatch_continuations");
        }
        if (continuations == 0 || continuations > pauses)
            throw new InvalidOperationException($"{name}: incomplete dispatch was not observed.");
        var architecture = RuntimeInformation.ProcessArchitecture;
        bool native = !string.Equals(Environment.GetEnvironmentVariable("MSYNC_JIT"), "off", StringComparison.OrdinalIgnoreCase)
            && (architecture == Architecture.X64 && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
                || architecture == Architecture.Arm64 && OperatingSystem.IsLinux());
        if (prefixes != (native ? 1UL : 0UL))
            throw new InvalidOperationException($"{name}: expected one native effect handoff when enabled, got {prefixes}.");
        Console.WriteLine($"PASS {name}: {prefixes} native handoff, one incomplete read and one effect");
    }

    private static Module RoundTrip(Module module)
    {
        using var stream = new MemoryStream();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }

    private sealed class GateReader : TextReader
    {
        internal TaskCompletionSource<bool> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Requested.TrySetResult(true);
            return new(Response.Task.WaitAsync(cancellationToken));
        }
    }
}
