using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using static Msync.Core.Hpir.HpirOp;
using static Msync.Core.Hpir.HpirWidth;

internal static class HpirProcessFutureChecks
{
    internal static void Run()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Build.props")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
        string directory = Path.Combine(root, ".temp", "hpir", "future-process", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = new QueuedWriter();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = new ExecutionOptions(WorkingDirectory: directory, Output: output);
        Task<ulong> run = Task.Run(() => HpirExecutor.Run(SavedImage(), options, stop.Token));
        try
        {
            output.Queued.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
            string started = Path.Combine(directory, "started");
            while (!File.Exists(started))
            {
                if (run.IsCompleted) _ = run.GetAwaiter().GetResult();
                Task.Delay(10, stop.Token).GetAwaiter().GetResult();
            }
            Require(!run.IsCompleted, "Pending Number and Bool lenses cannot finish without the process gate.");
            File.WriteAllText(Path.Combine(directory, "go"), "go");
            Require(run.WaitAsync(stop.Token).GetAwaiter().GetResult() == Bits(8),
                "A reloaded HPIR uses the same released child for both typed projections and returns the expected Number.");
            Require(File.ReadAllText(started) == "one\n", "Both projections share one actual host launch.");
        }
        finally
        {
            stop.Cancel();
            if (!run.IsCompleted)
                try { _ = run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
        Console.WriteLine("HPIR released process: saved image, typed Number/Bool, deferred identities and one child passed.");
    }

    private static HpirProgram SavedImage()
    {
        var add = HpirFutureOperation.Add;
        HpirProgram image = new([
            new("entry", 104 + HpirPathImports.PathBytes, U64, [],
            [
                new(AddressOfStatic, 104 + HpirPathImports.Raw, Immediate: 0),
                new(AddressOfStatic, 8, Immediate: 1), new(AddressOfStatic, 16, Immediate: 2), new(AddressOf, 0, 104),
                new(Call, 24, 0), new(Call, 32, 1), new(Call, 40, 2),
                new(ConstU64, 48, Immediate: (long)Bits(1)), new(Call, 56, 3), new(Call, 64, 3),
                new(CopyU64, 72, 56), new(EqU64, 80, 56, 64), new(EqU64, 81, 56, 72),
                new(ConstU8, 82, Immediate: 1), new(XorU8, 80, 80, 82), new(AndU8, 80, 80, 81),
                new(Branch, D: 23, A: 80, B: 17),
                new(AddressOfStatic, 88, Immediate: 3), new(Call, -1, 4),
                new(Call, 81, 5), new(Branch, D: 21, A: 81, B: 23),
                new(Call, 96, 6), new(Return, A: 96), new(Call, -1, 7)
            ], [new(~0, [0, 8, 16]), new(~1, [24]), new(~2, [24]), new(~3, [32, 48]),
                new(~4, [88]), new(~5, [40]), new(~6, [56]), new(~7, [])],
                [new(4, new("released-process.msync", 2, 12))])
            {
                Safepoints = [new(4, []), new(5, [24]), new(6, [24, 32]),
                    new(8, [32, 40]), new(9, [32, 40, 56]), new(19, [40, 56]), new(21, [56])]
            }
        ], 0)
        {
            Layouts = [HpirFutureImports.ProcessHandle(), HpirFutureImports.ProcessScalarProjection(),
                HpirFutureImports.ProcessScalarProjection(), HpirFutureImports.Lifted(add)],
            Imports = [HpirFutureImports.StartProcess(0, release: true, [HpirProcessArgument.Text, HpirProcessArgument.Text]), HpirFutureImports.ProcessExit(1),
                HpirFutureImports.ProcessSuccess(2), HpirFutureImports.Lift(add, HpirFutureInput.Deferred, HpirFutureInput.Ready, 3),
                HpirScalarImports.OutputCall(HpirTextOutput.PrintLine), HpirFutureImports.Demand(U8), HpirFutureImports.Demand(U64),
                new(HpirService.Fail, (int)HpirFailure.Unreachable, HpirWidth.Void, [])],
            TextLiterals = ["/bin/sh", "-c", "echo one >> started; while [ ! -f go ]; do sleep 0.01; done; exit 7", "queued"]
        };
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(image, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static ulong Bits(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);

    private sealed class QueuedWriter : StringWriter
    {
        internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            Require(buffer.Span.SequenceEqual("queued"), "Unexpected guest output.");
            Queued.SetResult();
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
