using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirSelectionIteratorChecks
{
    private const string Fixture = """
        let selection = $local @ ./input/<\.txt$>;
        let unopened = $local @ ./missing/<.*>;
        fn churn(limit: Number) -> Number {
            let i = 0;
            loop {
                if i >= limit { break i; };
                let element = scope { value: i };
                i = element::value + 1;
            }
        };
        churn(36000);
        ./payload.txt -> ./input/fresh.txt;
        let count = 0;
        for selected in selection {
            count = count + 1;
            if count == 1 { continue; };
            $std::io::println(selected as String);
            if count == 3 { break; };
        };
        acquire;
        """;

    private const string StaticFixture = """
        for selected in ./fixture/<\.txt$> { $std::io::println(selected as String); };
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "selection-iterator", Guid.NewGuid().ToString("N")));
        string input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.txt"), "fresh");
            File.WriteAllText(Path.Combine(input, "00-skip.txt"), "first");
            File.WriteAllText(Path.Combine(input, "zzz.txt"), "last");
            File.WriteAllText(Path.Combine(input, "excluded.bin"), "no");
            string fixture = Path.Combine(root, "fixture");
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "old.txt"), "before bind");
            for (int level = 0; level <= 3; ++level)
            {
                string late = Path.Combine(input, "late.txt");
                File.Delete(late);
                HpirProgram bound = Prepare(root, level);
                // Membership belongs to the runtime cursor, not this artifact bind.
                File.WriteAllText(late, "added after bind");
                Check(root, bound, null, level);
                CheckFrozen(root, fixture, level);
                if (!HpirMachineImage.IsSupported) continue;
                using var machine = HpirMachineImage.Compile(bound);
                Check(root, bound, machine, level);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR regex Path cursor: frozen membership, live writer frontier, captured Path roots and abrupt close");
    }

    private static HpirProgram Prepare(string root, int level)
    {
        var bound = Reload(HpirBinding.Prepare(Compile(root, level, Fixture), new(root, root)));
        if (bound.IteratorSelections.Count != 0 || bound.PreparedTransfers.Count != 0
            || !bound.Imports.Any(site => site.Service == HpirService.DirectoryOpen && site.Binding == 1))
            throw new InvalidOperationException($"O{level}: qualified Regex cursor was not retained as a live selector.");
        return bound;
    }

    private static HpirProgram Compile(string root, int level, string source)
    {
        string path = Path.Combine(root, "selection.msync");
        File.WriteAllText(path, source);
        var hir = Compiler.Analyze(File.ReadAllText(path), path);
        File.Delete(path);
        return Reload(Compiler.LowerHpir(hir, level));
    }

    private static void CheckFrozen(string root, string fixture, int level)
    {
        string late = Path.Combine(fixture, "late.txt");
        File.Delete(late);
        HpirProgram portable = Compile(root, level, StaticFixture);
        if (portable.IteratorSelections is not [{ Members: null }])
            throw new InvalidOperationException($"O{level}: the static selector lost its portable membership recipe.");
        HpirProgram bound = Reload(HpirBinding.Prepare(portable, new(root, root)));
        if (bound.IteratorSelections is not [{ Members: [{ Raw: "./fixture/old.txt" }] }])
            throw new InvalidOperationException($"O{level}: binding did not observe original selector membership.");
        File.WriteAllText(late, "after bind");
        ExecuteFrozen(root, bound, null, level);
        if (!HpirMachineImage.IsSupported) return;
        using var machine = HpirMachineImage.Compile(bound);
        ExecuteFrozen(root, bound, machine, level);
    }

    private static void ExecuteFrozen(string root, HpirProgram bound, HpirMachineImage? machine, int level)
    {
        using var output = new StringWriter { NewLine = "\n" };
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output)
        { Timeline = new(events.Enqueue, "bound-selection") };
        if (machine is null) HpirExecutor.RunOutcome(bound, options);
        else HpirExecutor.RunMachineOutcome(machine, options);
        if (output.ToString().Replace('\\', '/') != "./fixture/old.txt\n"
            || events.Count(item => item.Name == "hpir.directory.open"
                && item.Attributes.GetValueOrDefault("selection.membership") == "frozen") != 1
            || events.Any(item => item.Metrics.ContainsKey("directory.first_pull")))
            throw new InvalidOperationException($"O{level}: frozen membership was rediscovered: {output}");
    }

    private static void Check(string root, HpirProgram bound, HpirMachineImage? machine, int level)
    {
        string produced = Path.Combine(root, "input", "fresh.txt");
        File.Delete(produced);
        using var output = new StringWriter { NewLine = "\n" };
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output)
        { Timeline = new(events.Enqueue, "qualified-selection") };
        HpirRunOutcome result = machine is null ? HpirExecutor.RunOutcome(bound, options)
            : HpirExecutor.RunMachineOutcome(machine, options);
        string[] expected = ["./input/fresh.txt", "./input/late.txt"];
        var paths = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('\\', '/'));
        if (result.FailedTransfers != 0 || !paths.SequenceEqual(expected)
            || File.ReadAllText(produced) != "fresh"
            || events.Count(item => item.Name == "hpir.directory.open" && item.Attributes.ContainsKey("selection.pattern")) != 1
            || events.Count(item => item.Name == "hpir.directory.next"
                && item.Metrics.GetValueOrDefault("directory.yielded") == 1) != 3
            || !events.Any(item => item.Name == "hpir.heap.collect")
            || events.Count(item => item.Name == "hpir.directory.close"
                && item.Attributes.GetValueOrDefault("cleanup.reason") == "explicit") != 1)
            throw new InvalidOperationException($"O{level}: qualified selection lost ordering, liveness or close: {output}");
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using MemoryStream bytes = new();
        HpirArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        return HpirArtifact.ReadTrusted(bytes);
    }
}
