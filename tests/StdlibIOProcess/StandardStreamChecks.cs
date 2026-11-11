using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class StandardStreamChecks
{
    public static async Task RunAsync()
    {
        using StringReader input = new("\r\n名\r\nuntouched");
        using RecordingWriter output = new() { NewLine = "|" };
        using RecordingWriter error = new() { NewLine = "!" };
        ExecutionOptions options = new(Output: output, Error: error) { Input = input };
        Module module = Compiler.Compile("""
            let io = $std::io;
            let empty? = io::read_line();
            if empty is not null { io::println(empty); };
            let name? = io::prompt("name? ");
            if name is not null { io::print(name); };
            io::eprintln("error");
            io::flush_error();
            """, "standard-streams.msync");
        await Interpreter.ExecuteAsync(module, options);
        if (output.ToString() != "|name? 名" || error.ToString() != "error!"
            || output.FlushCount != 1 || error.FlushCount != 1 || input.ReadToEnd() != "untouched")
            throw new InvalidOperationException("Interpreter did not borrow the injected reader and separate writers with their own line endings.");

        using CancellableReader cancellable = new();
        using CancellationTokenSource cancellation = new();
        Module read = Compiler.Compile("$std::io::read_line()", "cancelled-stdin.msync");
        var borrowed = options with { Input = cancellable };
        Task<Value> pending = Interpreter.ExecuteAsync(read, borrowed, cancellation.Token).AsTask();
        await cancellable.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Cancelled input completed as a value.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        var resumed = await Interpreter.ExecuteAsync(read, borrowed).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (resumed is not StringValue { Data: "kept" })
            throw new InvalidOperationException("Cancellation lost the following line or kept ownership of the reader.");
        Console.WriteLine("PASS: borrowed readers/writers, channel separation, prompt flush and cancellation ownership");
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
