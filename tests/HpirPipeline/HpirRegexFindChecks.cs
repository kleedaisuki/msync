using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirRegexFindChecks
{
    private const string Name = "regex-find-captures.msync";
    private const string Capture = """
        let regex = $std::regex;
        let io = $std::io;
        let r = $std::ranges;
        let pattern = $std::string::concat("(?<=(?<prefix>", "😀))b");
        let found? = regex::find("a😀bc", pattern);
        if found? {
            io::println("wrong");
        } else {
            io::println(found::value);
            io::println(found::index as String);
            io::println(found::length as String);
            let groups = found::groups;
            io::println(r::size(groups) as String);
            let prefix = r::at(groups, 1);
            io::println(prefix::name);
            io::println(prefix::value);
            io::println(prefix::index as String);
            io::println(prefix::length as String);
        };
        let other? = regex::find("A😀abc", "(?<part>abc)(?<missing>z)?");
        if other? { io::println("wrong"); } else {
            let groups = other::groups;
            io::println(r::size(groups) as String);
            let missing = r::at(groups, 2);
            io::println(missing::name);
            io::println(missing::value);
            io::println(missing::index as String);
            io::println(missing::length as String);
            if missing::success { io::println("wrong"); } else { io::println("unset"); };
        };
        let absent? = regex::find("abc", "z");
        if absent? { io::println("absent"); };
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level) Captures(level);
        RetainAcrossCollection();
        Console.WriteLine("PASS HPIR regex.find: nullable first match, Unicode scalar capture offsets, exact groups and collection");
    }

    private static void Captures(int level)
    {
        var selection = new List<CompilationObservation>();
        HpirProgram executable = Compile(Capture, level, new(selection.Add, includeOptimizerPasses: true));
        using var output = new StringWriter { NewLine = "\n" };
        var events = new List<RuntimeTimelineRecord>();
        _ = HpirExecutor.Run(executable, new(Output: output) { Timeline = new(events.Add, "regex-find") });
        const string expected = "b\n2\n1\n2\nprefix\n😀\n1\n1\n3\nmissing\n\n-1\n0\nunset\nabsent\n";
        var finds = events.Where(e => e.Name == "hpir.regex.find").ToArray();
        if (output.ToString() != expected
            || !finds.Select(e => e.Metrics.GetValueOrDefault("regex.groups")).SequenceEqual([2UL, 3UL, 0UL])
            || !finds.Select(e => e.Metrics.GetValueOrDefault("regex.literal")).SequenceEqual([0UL, 1UL, 1UL])
            || finds.Any(e => e.Attributes.GetValueOrDefault("source.file") != Name)
            || !selection.Any(e => e.Metrics?.GetValueOrDefault("regex.group_layout_checks_elided") > 0))
            throw new InvalidOperationException($"O{level} regex.find did not preserve capture fields and cardinality: {output}");
    }

    private static void RetainAcrossCollection()
    {
        const string source = """
            fn churn() {
                let i = 0;
                let size = 0;
                loop {
                    if i == 4500 { break size; };
                    size = size + $std::string::length($std::string::repeat("😀", 512));
                    i = i + 1;
                }
            };
            let found? = $std::regex::find("a😀bc", "(?<=(?<prefix>😀))b");
            if found? { -1 } else {
                let captures = found::groups;
                let size = churn();
                let prefix = $std::ranges::at(captures, 1);
                size + prefix::index
            }
            """;
        var profile = new RuntimeProfile();
        ulong bits = HpirExecutor.Run(Compile(source, 2), new() { Profile = profile });
        if (BitConverter.UInt64BitsToDouble(bits) != 4500 * 512 + 1
            || !profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_collections") != 0))
            throw new InvalidOperationException("The published capture vector did not survive an actual guest collection.");
    }

    private static HpirProgram Compile(string source, int level, CompilationTrace? trace = null)
    {
        HpirProgram portable = Compiler.LowerHpir(Compiler.Analyze(source, Name), level, trace);
        string directory = Environment.CurrentDirectory;
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(HpirBinding.Prepare(portable, new(directory, directory)), image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }
}
