using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirStringChecks
{
    private const string SourceName = "hpir-unicode.msync";
    private const string Source = """
        let s = $std::string;
        let io = $std::io;
        let cluster = "🧪é";
        io::println(s::length(cluster) as String);
        io::println(s::utf8_size(cluster) as String);
        io::println(s::char_at(cluster, 0));
        io::println(s::substring(cluster, 1, 10000000000000000));
        io::println(s::to_lowercase("ΟΣ İ 𐐀"));
        io::println(s::to_uppercase("Straße ﬃ"));
        io::println(s::to_lowercase("draft.txt"));
        io::println(s::to_lowercase("DRAFT.TXT"));
        io::println(s::replace(s::trim("　draft-橋.png　"), ".png", ".txt"));
        io::println(s::concat(s::repeat("橋", 2), s::concat("", "🧪")));
        io::println(s::repeat("", 1e300));
        io::println(s::concat(s::trim_start("　x　"), s::trim_end("　y　")));
        if s::starts_with("draft-橋", "draft-") and s::ends_with("draft-橋", "橋") and s::contains(cluster, "é")
            and not s::is_ascii(cluster) and "𐀀" > "" and "é" != "é" and s::compare("𐀀", "") == 1 {
            42
        } else { 0 }
        """;

    private const string Expected = "3\n7\n🧪\né\nος i̇ 𐐨\nSTRASSE FFI\ndraft.txt\ndraft.txt\ndraft-橋.txt\n橋橋🧪\n\nx　　y\n";

    internal static void Run()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                HpirProgram program = Compile(Source, level);
                using var output = new StringWriter { NewLine = "\n" };
                var profile = new RuntimeProfile(instructionSites: true);
                ulong result = HpirExecutor.Run(program, new(Output: output) { Profile = profile });
                if (BitConverter.UInt64BitsToDouble(result) != 42 || output.ToString() != Expected)
                    throw new Exception($"HPIR Unicode O{level}: incorrect scalar behavior or output:\n{output}");
                var samples = profile.Drain();
                string[] required = ["string.length", "string.utf8_size", "string.starts_with", "string.compare", "string.greater",
                    "string.not_equal", "string.concat", "string.to_lowercase", "string.to_uppercase", "string.replace"];
                if (required.Any(name => !samples.Any(sample => sample.Name == "vm.native"
                        && sample.Attributes["native.name"] == name && sample.Attributes.GetValueOrDefault("source.file") == SourceName))
                    || samples.Sum(sample => (long)sample.Metrics.GetValueOrDefault("text_unicode_case")) != 2
                    || samples.Sum(sample => (long)sample.Metrics.GetValueOrDefault("text_ascii_case")) != 2
                    || samples.Sum(sample => (long)sample.Metrics.GetValueOrDefault("text_inputs_reused")) < 3)
                    throw new Exception($"HPIR Unicode O{level}: missing attributed String calls or casing/reuse decisions");
            }

            HpirProgram invalid = Compile("$std::string::substring(\"🧪\", 2, 1)", 2);
            try { _ = HpirExecutor.Run(invalid); throw new Exception("HPIR accepted an out-of-range Unicode scalar index."); }
            catch (MsyncException error) when (error.Span?.Source == SourceName
                && error.Message.Contains("String length 1 (Unicode scalar values)", StringComparison.Ordinal)) { }
            Collection();
        }
        finally { CultureInfo.CurrentCulture = saved; }
        Console.WriteLine("PASS HPIR ready String: Unicode scalar, ordinal and full casing semantics O0–O3");
    }

    private static void Collection()
    {
        const string source = """
            let s = $std::string;
            fn churn(limit: Number, kept: String) -> Number {
                let index = 0;
                let count = 0;
                loop {
                    if index >= limit { break count + s::length(kept); };
                    let made = s::repeat("𐐀", 80);
                    count = count + s::length(s::to_lowercase(made));
                    index = index + 1;
                }
            };
            churn(2300, s::concat("🧪", "保留"))
            """;
        HpirProgram program = Compile(source, 2);
        var profile = new RuntimeProfile();
        ulong result = HpirExecutor.Run(program, new() { Profile = profile });
        if (BitConverter.UInt64BitsToDouble(result) != 184003
            || !profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_collections") > 0))
            throw new Exception("HPIR lost a live String in a suspended caller or skipped real collection.");
    }

    private static HpirProgram Compile(string source, int level)
    {
        HpirProgram portable = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(source, SourceName), level));
        string directory = Environment.CurrentDirectory;
        return RoundTrip(HpirBinding.Prepare(portable, new(directory, directory)));
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }
}
