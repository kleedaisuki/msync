using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirTextChunkChecks
{
    private const string Source = """
        let policy = $std::text::StringBuilder::new();
        let alias = policy;
        $std::text::StringBuilder::append(policy, "A🙂");
        let prefix = alias::chunks;
        $std::text::StringBuilder::append(alias, "β");
        $std::text::StringBuilder::append(policy, "世");
        $std::text::StringBuilder::append(policy, "Z");
        let fork = $std::text::append_chunk(prefix, "Ω");
        $std::text::StringBuilder::build(alias) -> ./builder.txt;
        $std::string::join(prefix, "|") -> ./prefix.txt;
        $std::string::join(fork, "|") -> ./fork.txt;
        let current = [""];
        for n in $std::ranges::iota(0, 12000) {
            current = $std::text::append_chunk(current, (n % 10) as String);
        };
        $std::string::join(current, "") -> ./digits.txt;
        $std::text::StringBuilder::clear(alias);
        $std::text::StringBuilder::build(policy) -> ./cleared.txt;
        $std::string::join(prefix, "|") -> ./retained.txt;
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", $"text-chunks-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        try
        {
            var expected = string.Concat(Enumerable.Repeat("0123456789", 1200));
            for (int level = 0; level <= 3; ++level)
            {
                var observations = new List<CompilationObservation>();
                var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
                HpirProgram portable = Reload(Compiler.LowerHpir(Compiler.Analyze(Source, "text-chunks.msync", trace), level, trace));
                HpirProgram program = Reload(HpirBinding.Prepare(portable, new(root, root)));
                Require(program.Imports.Count(item => item.Service == HpirService.AppendTextChunk) == 1
                    && observations.Any(item => item.Metrics?.GetValueOrDefault("strings.chunk_appends") > 0),
                    "The selected append and snapshot joins must share the typed vector representation.");
                Check(program, root, expected, native: false);
                if (HpirMachineImage.IsSupported) Check(program, root, expected, native: true);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS typed Text chunks O0–O3: alias, frozen prefixes, fork, bounded copies, clear and collection");
    }

    private static void Check(HpirProgram program, string root, string expected, bool native)
    {
        foreach (string file in Directory.GetFiles(root, "*.txt")) File.Delete(file);
        var observed = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions { Timeline = new(observed.Enqueue, "text-chunks") };
        HpirRunOutcome result;
        if (native)
        {
            using var image = HpirMachineImage.Compile(program);
            result = HpirExecutor.RunMachineOutcome(image, options);
        }
        else result = HpirExecutor.RunOutcome(program, options);
        Require(result.FailedTransfers == 0
            && Read("builder.txt") == "A🙂β世Z" && Read("prefix.txt") == "|A🙂" && Read("fork.txt") == "|A🙂|Ω"
            && Read("digits.txt") == expected && Read("cleared.txt") == "" && Read("retained.txt") == "|A🙂",
            "A live alias, append fork, clear, String order, or historical snapshot changed its bytes.");
        var appends = observed.Where(item => item.Name == "hpir.text.append_chunk").ToArray();
        Require(appends.Length == 12005
            && appends.Count(item => item.Attributes.GetValueOrDefault("chunks.storage") == "forked") == 1
            && appends.Sum(item => (long)item.Metrics.GetValueOrDefault("chunks.copied")) < 2 * appends.Length
            && observed.Any(item => item.Name == "hpir.heap.collect"),
            "The real append path must extend its tail in place, copy only on growth/fork, and retain payload across collection.");
        return;
        string Read(string file) => File.ReadAllText(Path.Combine(root, file));
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
