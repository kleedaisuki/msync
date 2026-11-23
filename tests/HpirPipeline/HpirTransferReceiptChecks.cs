using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirTransferReceiptChecks
{
    private const string Name = "transfer-receipt.msync";
    private const string Observer = """
        let copied = ./first.txt -> ./shared.txt;
        copied as String -> ./copy-result.txt;
        ./second.txt -> ./shared.txt;
        """;
    private const string Gated = """
        fn write_receipt() -> Bool {
            let child = release /bin/sh("-c", "while [ ! -f release ]; do sleep 0.01; done; printf first-generation");
            let copied = child::stdout -> ./shared.txt;
            copied as String -> ./copy-result.txt
        };
        let receipt = write_receipt();
        ./second.txt -> ./shared.txt;
        $std::io::println("admitted");
        receipt
        """;

    internal static void Run()
    {
        string dir = Path.GetFullPath(Path.Combine(".temp", "hpir", $"receipt-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                HpirProgram program = Compile(Observer, level, dir);
                CheckObserver(program, dir, false, false);
                if (HpirMachineImage.IsSupported) CheckObserver(program, dir, true, false);
                if (level == 2) CheckObserver(program, dir, false, true);
            }
            if (!OperatingSystem.IsWindows())
            {
                HpirProgram gated = Compile(Gated, 2, dir, returnsReceipt: true);
                CheckGate(gated, dir, false);
                if (HpirMachineImage.IsSupported) CheckGate(gated, dir, true);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
        Console.WriteLine("PASS detached transfer receipt with typed Bool admission, writer order and original failure");
    }

    private static HpirProgram Compile(string source, int level, string dir, bool returnsReceipt = false)
    {
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram portable = Reload(Compiler.LowerHpir(Compiler.Analyze(source, Name, trace), level, trace));
        HpirProgram program = Reload(HpirBinding.Prepare(portable, new(dir, dir)));
        Require(program.Imports.Count(item => item.Service == HpirService.LiveTransferBoolText) == 1
            && program.Imports.Count(item => item.Service == HpirService.FutureDemand) == (returnsReceipt ? 1 : 0)
            && observations.Any(item => item.Metrics?.GetValueOrDefault("paths.transfer_bool_text") == 1),
            "A direct transfer receipt must not wait in the guest or invent a Text job.");
        return program;
    }

    private static void CheckObserver(HpirProgram program, string dir, bool native, bool missing)
    {
        Reset(dir);
        if (missing) File.Delete(Path.Combine(dir, "first.txt"));
        var errors = new ConcurrentQueue<MsyncException>();
        var options = new ExecutionOptions(WorkingDirectory: dir) { ReportError = errors.Enqueue };
        CheckFiles(dir, Execute(program, options, native), missing);
        if (missing)
            Require(errors.TryDequeue(out var error) && error.Span?.Source == Name
                && error.Message.Contains("first.txt", StringComparison.Ordinal), "The failing copy lost its original diagnostic.");
        Require(errors.IsEmpty, "The receipt added a second diagnostic or a spurious failure.");
    }

    private static void CheckFiles(string dir, HpirRunOutcome result, bool missing)
    {
        Require(result.FailedTransfers == (missing ? 1 : 0)
            && File.ReadAllText(Path.Combine(dir, "copy-result.txt")) == (missing ? "false" : "true")
            && File.ReadAllText(Path.Combine(dir, "shared.txt")) == "second-generation",
            "The dependent write lost its actual receipt or later resource ordering.");
    }

    private static void CheckGate(HpirProgram program, string dir, bool native)
    {
        Reset(dir);
        using var output = new AdmissionMarker();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var observations = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: dir, Output: output)
        { Timeline = new(observations.Enqueue, "transfer-receipt-gate") };
        Task<HpirRunOutcome> running = Task.Run(() => Execute(program, options, native, stop.Token));
        try
        {
            Task first = Task.WhenAny(output.Admitted.Task, running).WaitAsync(stop.Token).GetAwaiter().GetResult();
            if (first == running) _ = running.GetAwaiter().GetResult();
            Require(first == output.Admitted.Task && !running.IsCompleted
                && !File.Exists(Path.Combine(dir, "copy-result.txt")) && !File.Exists(Path.Combine(dir, "shared.txt")),
                "Effects must be admitted without waiting for the source, while retaining the shared resource order.");
            File.WriteAllText(Path.Combine(dir, "release"), "");
            HpirRunOutcome result = running.WaitAsync(stop.Token).GetAwaiter().GetResult();
            CheckFiles(dir, result, missing: false);
            Require(result.Value == 1 && observations.Any(item => item.Name == "hpir.work.frame_exit"
                && item.Metrics.GetValueOrDefault("work.promoted_effect_dependencies") == 2),
                "The child should transfer both host dependencies with its observable receipt, not join them on return.");
            Require(observations.Any(item => item.Name == "hpir.effect.admit"
                && item.Attributes.GetValueOrDefault("transfer.destination")?.EndsWith("copy-result.txt", StringComparison.Ordinal) == true
                && item.Metrics.GetValueOrDefault("input.text_pending") == 1),
                "Admission must record the actual pending Bool dependency.");
        }
        finally
        {
            stop.Cancel();
            if (!running.IsCompleted)
                try { _ = running.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
    }

    private static void Reset(string dir)
    {
        File.Delete(Path.Combine(dir, "shared.txt"));
        File.Delete(Path.Combine(dir, "copy-result.txt"));
        File.Delete(Path.Combine(dir, "release"));
        File.WriteAllText(Path.Combine(dir, "first.txt"), "first-generation");
        File.WriteAllText(Path.Combine(dir, "second.txt"), "second-generation");
    }

    private static HpirRunOutcome Execute(HpirProgram program, ExecutionOptions options, bool native, CancellationToken token = default)
    {
        if (!native) return HpirExecutor.RunOutcome(program, options, token);
        using var image = HpirMachineImage.Compile(program);
        return HpirExecutor.RunMachineOutcome(image, options, token);
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private sealed class AdmissionMarker : StringWriter
    {
        internal TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Require(buffer.Span.SequenceEqual("admitted"), "Unexpected gate output.");
            Admitted.SetResult();
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
