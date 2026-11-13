using Msync.Compiler;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class StandardStreamChecks
{
    public static async Task RunAsync()
    {
        int checks = 0;
        foreach (int level in new[] { 0, 2 })
        {
            const string source = """
                let io = $std::io;
                let empty? = io::read_line();
                if empty is not null { io::println(empty); };
                let name? = io::prompt("name? ");
                if name is not null { io::print(name); };
                io::eprintln("error");
                io::flush_error();
                """;
            foreach (NativeProgram program in Images(source, "standard-streams.msync", level))
            {
                using StringReader input = new("\r\n名\r\nuntouched");
                using RecordingWriter output = new() { NewLine = "|" };
                using RecordingWriter error = new() { NewLine = "!" };
                ExecutionOptions options = new(Output: output, Error: error) { Input = input };
                await Execute(program, options);
                if (output.ToString() != "|name? 名" || error.ToString() != "error!"
                    || output.FlushCount != 1 || error.FlushCount != 1 || input.ReadToEnd() != "untouched")
                    throw new InvalidOperationException($"O{level} {program.Stage}: borrowed streams, line endings or flush contract changed.");
                checks++;
            }
            foreach (NativeProgram program in Images("$std::io::read_line()", "cancelled-stdin.msync", level))
            {
                using CancellableReader input = new();
                using CancellationTokenSource cancellation = new();
                ExecutionOptions options = new() { Input = input };
                Task<NativeValue> pending = Execute(program, options, cancellation.Token).AsTask();
                await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    throw new InvalidOperationException("Cancelled input completed as a value.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                NativeValue resumed = await Execute(program, options).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (resumed.AsString() != "kept")
                    throw new InvalidOperationException($"O{level} {program.Stage}: cancellation lost the next line or retained reader ownership.");
                checks++;
            }
        }
        Console.WriteLine($"PASS: {checks} source/codec/prepared borrowed-stream and cancellation ownership journeys");
    }

    private static IEnumerable<NativeProgram> Images(string source, string filename, int level)
    {
        NativeProgram program = Compiler.Compile(source, filename, level);
        yield return program;
        yield return Roundtrip(program);
        yield return Roundtrip(NativePreparation.Prepare(program));
    }

    private static NativeProgram Roundtrip(NativeProgram program)
    {
        using MemoryStream artifact = new();
        NativeArtifact.Write(program, artifact);
        artifact.Position = 0;
        return NativeArtifact.Read(artifact);
    }

    private static async ValueTask<NativeValue> Execute(NativeProgram program, ExecutionOptions options, CancellationToken token = default)
    {
        await using NativeInterpreter interpreter = new(program, options);
        return await interpreter.ExecuteAsync(token);
    }

    private sealed class RecordingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellableReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (Entered.TrySetResult()) await Task.Delay(Timeout.Infinite, cancellationToken);
            return "kept";
        }
    }
}
