using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirUnionTransferChecks
{
    private const string Name = "union-transfer.msync";
    private const string Source = """
        fn publish(mode: Number, target: Path, receipt: Path) {
            let selected = if mode == 1 { scope { value?: ./input.txt } }
                else { if mode == 2 { scope { value?: "fallback" } } else { scope { value?: null } } };
            let item? = selected::value;
            if item is null { return; };
            let copied = item -> target;
            copied as String -> receipt;
        };
        publish(1, ./from-path.txt, ./path-status.txt);
        publish(2, ./from-text.txt, ./text-status.txt);
        publish(0, ./no-value.txt, ./no-status.txt);
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", $"union-transfer-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "fallback"), "not the literal");
            for (int level = 0; level <= 3; ++level)
            {
                List<CompilationObservation> evidence = [];
                var trace = new CompilationTrace(evidence.Add, includeOptimizerPasses: true);
                var module = Compiler.Analyze(Source, Name, trace);
                var portable = Reload(Compiler.LowerHpir(module, level, trace));
                HpirProgram program = Reload(HpirBinding.Prepare(portable, new(root, root)));
                Require(evidence.Any(item => item.Metrics?.GetValueOrDefault("paths.transfer_union_sites") == 1)
                    && program.Imports.Count(item => item.Service == HpirService.LiveTransfer) == 2,
                    "The narrowed physical union must select between two concrete arms at one site.");
                Check(program, root, native: false, missing: false);
                if (HpirMachineImage.IsSupported) Check(program, root, native: true, missing: false);
                if (level == 2)
                {
                    Check(program, root, native: false, missing: true);
                    if (HpirMachineImage.IsSupported) Check(program, root, native: true, missing: true);
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS nullable scope Path/String transfer, one typed arm, live failure receipt and skipped Null O0–O3");
    }

    private static void Check(HpirProgram program, string root, bool native, bool missing)
    {
        foreach (string target in new[] { "from-path.txt", "path-status.txt", "from-text.txt", "text-status.txt" })
            File.Delete(Path.Combine(root, target));
        string input = Path.Combine(root, "input.txt");
        if (missing) File.Delete(input);
        else File.WriteAllText(input, "copy bytes");
        var diagnostics = new ConcurrentQueue<MsyncException>();
        var observations = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions
        {
            ReportError = diagnostics.Enqueue,
            Timeline = new(observations.Enqueue, Name)
        };
        HpirRunOutcome result;
        if (native)
        {
            using var image = HpirMachineImage.Compile(program);
            result = HpirExecutor.RunMachineOutcome(image, options);
        }
        else result = HpirExecutor.RunOutcome(program, options);
        Require(result.FailedTransfers == (missing ? 1 : 0)
            && File.ReadAllText(Path.Combine(root, "path-status.txt")) == (missing ? "false" : "true")
            && File.ReadAllText(Path.Combine(root, "from-text.txt")) == "fallback"
            && File.ReadAllText(Path.Combine(root, "text-status.txt")) == "true"
            && !File.Exists(Path.Combine(root, "no-value.txt")) && !File.Exists(Path.Combine(root, "no-status.txt")),
            "The union selected the wrong input, altered its receipt, or observed the Null arm.");
        if (missing)
            Require(diagnostics.TryDequeue(out var error) && error.Span?.Source == Name
                && error.Message.Contains("input.txt", StringComparison.Ordinal), "The selected Path lost its original failure.");
        else Require(File.ReadAllText(Path.Combine(root, "from-path.txt")) == "copy bytes", "Path was written as text.");
        Require(diagnostics.IsEmpty, "An unselected arm was admitted or a failure duplicated.");
        var admitted = observations.Where(item => item.Name == "hpir.effect.admit").ToArray();
        Require(admitted.Length == 4
            && admitted.Count(item => item.Attributes.GetValueOrDefault("transfer.kind") == "path") == 1
            && admitted.Count(item => item.Attributes.GetValueOrDefault("transfer.kind") == "text") == 3,
            "Expected one Path copy, one literal write and two receipts, without any unselected admission.");
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
