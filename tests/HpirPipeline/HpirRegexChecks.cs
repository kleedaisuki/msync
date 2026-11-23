using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirRegexChecks
{
    private const string Name = "regex-live-selection.msync";

    internal static void Run()
    {
        string root = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".temp", "hpir", $"regex-{Guid.NewGuid():N}")).FullName;
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            for (int level = 0; level <= 3; ++level) Ready(root, level);
            Malformed(root);
            LiveSelection(root);
            Console.WriteLine("PASS HPIR Regex: ready String/Path, invariant and named replacement, live selection after an admitted writer");
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
            Directory.Delete(root, true);
        }
    }

    private static void Ready(string root, int level)
    {
        const string source = """
            fn reflects(subject: String, pattern: String) -> Bool {
                $std::regex::is_match(subject, pattern)
            };
            if reflects("FILE", $std::string::concat("(?i)", "file")) { $std::io::println("invariant"); };
            $std::io::println(match ./case.FILE {<(?i)file$>=>{"path"},_=>{"wrong"}});
            $std::io::println($std::regex::escape("a+b"));
            $std::io::println($std::regex::replace("a12b", "(?<digits>[0-9]+)", "[${digits}]"));
            """;
        using var output = new StringWriter { NewLine = "\n" };
        var events = new List<RuntimeTimelineRecord>();
        HpirProgram program = Compile(source, root, level);
        _ = HpirExecutor.Run(program, new(Output: output) { Timeline = new(events.Add, "regex") });
        if (output.ToString() != "invariant\npath\na\\+b\na[12]b\n"
            || !events.Any(item => item.Name == "hpir.regex" && item.Attributes["regex.operation"] == "regex.operator.path"
                && item.Metrics.GetValueOrDefault("regex.literal") == 1)
            || !events.Any(item => item.Name == "hpir.regex" && item.Attributes["regex.operation"] == "regex.is_match"
                && item.Metrics.TryGetValue("regex.literal", out ulong known) && known == 0))
            throw new Exception($"Unexpected Regex output or carrier at O{level}: {output}");
    }

    private static void Malformed(string root)
    {
        HpirProgram program = Compile("let _ = $std::regex::is_match(\"text\", \"[\");", root, 2);
        try { _ = HpirExecutor.Run(program); }
        catch (MsyncException error) when (error.Span?.Source == Name && error.InnerException is RegexParseException
            && error.Message.StartsWith("regex.is_match:", StringComparison.Ordinal)) { return; }
        throw new Exception("Malformed dynamic Regex did not preserve its failure cause and source.");
    }

    private static void LiveSelection(string root)
    {
        const string source = """
            match "aa" {<(?<part>a)(?:\k<part>)>=>{"matched"},_=>{"wrong"}} -> ./match-result.txt;
            ./source.txt -> ./input/written.txt;
            ./input/<\.txt$> -> ./selected/;
            """;
        string detached = Directory.CreateDirectory(Path.Combine(root, "detached")).FullName;
        string input = Directory.CreateDirectory(Path.Combine(root, "input")).FullName;
        File.WriteAllText(Path.Combine(root, "source.txt"), "fresh");
        File.WriteAllText(Path.Combine(input, "written.txt"), "stale");
        File.WriteAllText(Path.Combine(input, "excluded.bin"), "exclude");
        HpirProgram program = Compile(source, root, 2);
        File.WriteAllText(Path.Combine(input, "after-bind.txt"), "live membership");
        if (program.PreparedTransfers.Count != 0 || !program.Imports.Any(import => import.Service == HpirService.LiveTransferSelection))
            throw new Exception("The branching fixture did not retain a live Path selection.");
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        _ = HpirExecutor.Run(program, new(WorkingDirectory: detached)
        {
            Timeline = new(item =>
            {
                events.Enqueue(item);
                if (!OperatingSystem.IsWindows() && item.Name == "file.sync"
                    && item.Attributes.GetValueOrDefault("file.stage_path")?.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true
                    && !release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Selection was not admitted while the preceding file write was held.");
                if (item.Name == "hpir.effect.admit" && item.Attributes.GetValueOrDefault("transfer.kind") == "selection") release.Set();
            }, "regex-selection")
        });
        string selected = Path.Combine(root, "selected");
        if (File.ReadAllText(Path.Combine(root, "match-result.txt")) != "matched" || File.ReadAllText(Path.Combine(selected, "written.txt")) != "fresh"
            || File.ReadAllText(Path.Combine(selected, "after-bind.txt")) != "live membership"
            || File.Exists(Path.Combine(selected, "excluded.bin")) || !OperatingSystem.IsWindows()
                && !events.Any(item => item.Name == "hpir.effect.admit" && item.Attributes.GetValueOrDefault("transfer.kind") == "selection"
                    && item.Metrics.GetValueOrDefault("dependencies.live") == 1))
            throw new Exception("Live Regex selection did not wait for the right writer or used frozen membership.");
    }

    private static HpirProgram Compile(string source, string root, int level)
    {
        var program = Compiler.LowerHpir(Compiler.Analyze(source, Name), level);
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(HpirBinding.Prepare(program, new(root, root)), image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }
}
