using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirPathFilterChecks
{
    private const string Filter = """
        let visits = 0;
        let selected = [./input, ./missing] | $std::ranges::filter(fn(path: Path) {
            visits = visits + 1;
            exists path
        });
        $std::io::println(visits as String);
        for path in selected { path -> ./output; };
        $std::io::println(visits as String);
        """;

    private const string Writer = """
        let child = release /bin/sh("-c", "while [ ! -f go ]; do sleep 0.01; done; printf delayed");
        child::stdout -> ./input;
        """;

    internal static void Run()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "hpir", "path-filter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (int level = 0; level <= 3; ++level)
            {
                Check(directory, level, pending: false);
                if (!OperatingSystem.IsWindows()) Check(directory, level, pending: true);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS Path filter: no eager predicate, exactly two checks, and one genuinely pending Bool at O0–O3");
    }

    private static void Check(string root, int level, bool pending)
    {
        string work = Directory.CreateDirectory(Path.Combine(root, $"O{level}-{pending}")).FullName;
        var module = Compiler.Analyze(pending ? Writer + '\n' + Filter : Filter, Path.Combine(work, "filter.msync"));
        HpirProgram bound = Reload(HpirBinding.Prepare(Reload(Compiler.LowerHpir(module, level)), new(work, work)));
        Verify(null);
        if (HpirMachineImage.IsSupported)
        {
            using var image = HpirMachineImage.Compile(bound, observe: true);
            Verify(image);
        }
        return;

        void Verify(HpirMachineImage? image)
        {
            string input = Path.Combine(work, "input"), output = Path.Combine(work, "output"), gate = Path.Combine(work, "go");
            File.Delete(input);
            File.Delete(output);
            File.Delete(gate);
            if (!pending) File.WriteAllText(input, "ready");
            var events = new ConcurrentQueue<RuntimeTimelineRecord>();
            using StringWriter printed = new() { NewLine = "\n" };
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(8));
            var profile = new RuntimeProfile(instructionSites: true);
            var options = new ExecutionOptions(WorkingDirectory: root, Output: printed)
            { Profile = profile, Timeline = new(Observe, "typed-filter") };
            if (image is null) _ = HpirExecutor.Run(bound, options, stop.Token);
            else _ = HpirExecutor.RunMachineCandidate(image, options, stop.Token);
            var queries = events.Where(item => item.Name == "hpir.file_read.admit").ToArray();
            bool waited = profile.Drain().Any(item => item.Name == "vm.native" && item.Attributes.GetValueOrDefault("native.name") == "FutureDemand");
            if (printed.ToString() != "0\n2\n" || File.ReadAllText(output) != (pending ? "delayed" : "ready")
                || queries.Length != 2 || queries.Any(IsBlockedQuery) != pending || waited != pending)
                throw new Exception($"Path filter O{level}: output={printed}; queries={queries.Length}; pending={pending}; waited={waited}");

            void Observe(RuntimeTimelineRecord item)
            {
                events.Enqueue(item);
                if (!pending || item.Name != "hpir.file_read.admit" || !IsBlockedQuery(item)) return;
                if (File.Exists(input) || File.Exists(output)) throw new Exception("The gate was already bypassed.");
                File.WriteAllText(gate, "release");
            }
        }
    }

    private static bool IsBlockedQuery(RuntimeTimelineRecord item) => item.Metrics.GetValueOrDefault("dependencies.live") > 0;

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }
}
