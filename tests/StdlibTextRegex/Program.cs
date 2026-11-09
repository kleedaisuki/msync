using System.Diagnostics;
using Msync.Core;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

if (args.Contains("--bench", StringComparer.Ordinal))
{
    string libraryPath = args.FirstOrDefault(argument => argument.StartsWith("--bench-source=", StringComparison.Ordinal))?[15..]
        ?? "stdlib/text.msync";
    string library = File.ReadAllText(libraryPath);
    foreach (int count in new[] { 64, 512, 4096 })
    {
        string source = library + $"let b = Builder(); for x in $std::ranges::iota(0, {count}) {{ b::append(\"abcdefghijklmnopqrstuvwxyz012345\"); }}; b::build()";
        Module module = Frontend.Compile(source, "text-builder-benchmark", 2);
        _ = await Interpreter.ExecuteAsync(module);
        List<double> times = [];
        List<long> allocations = [];
        for (int sample = 0; sample < 3; sample++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            Stopwatch watch = Stopwatch.StartNew();
            Value result = await Interpreter.ExecuteAsync(module);
            watch.Stop();
            if (result is not StringValue text || text.Data.Length != count * 32)
                throw new InvalidOperationException("Builder benchmark lost text.");
            times.Add(watch.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
        }
        Console.WriteLine($"Builder append={count}, chars={count * 32}, median_ms={times.Order().ElementAt(1):F3}, median_bytes={allocations.Order().ElementAt(1)}");
    }
    return;
}

(string Source, string Expected)[] cases =
[
    ("$std::regex::is_match(\"ABC\", \"(?i)abc\")", "true"),
    ("$std::regex::count(\"a😀b😀\", \"😀\")", "2"),
    ("let found? = $std::regex::find(\"A😀abc\", \"abc\"); if found? { -1 } else { found::index }", "2"),
    ("let found? = $std::regex::find(\"abc\", \"z\"); found?", "true"),
    ("$std::string::join($std::regex::values(\"a11b22\", \"[0-9]+\"), \",\")", "11,22"),
    ("$std::regex::replace(\"a12b\", \"(?<digits>[0-9]+)\", \"[${digits}]\")", "a[12]b"),
    ("$std::string::join($std::regex::split(\"a,b;c\", \"[,;]\"), \"|\")", "a|b|c")
];
int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    foreach (var (source, expected) in cases) await Check(source, expected, level);
    await Check("let b = $std::text::Builder(); b::append(\"A😀\"); b::append_line(\"x\"); b::build()", "A😀x\n", level);
    await Check("let b = $std::text::Builder(); b::append(\"A😀\"); let alias = b; alias::append(\"z\"); b::length()", "3", level);
    await Check("let b = $std::text::Builder(); let c = $std::text::Builder(); b::append(\"a\"); c::empty()", "true", level);
    await Check("let b = $std::text::Builder(); b::append(\"a\"); let snapshot = b::build(); b::append(\"b\"); snapshot", "a", level);
    await Check("let b = $std::text::Builder(); b::append_all([\"a\", \"b\", \"c\"]); b::clear(); b::append(\"z\"); b::build()", "z", level);
    await Check("let chunks = [\"a\"]; let snapshot = chunks; chunks = $std::text::append_chunk(chunks, \"b\"); $std::string::join(snapshot, \"\")", "a", level);
    await Check("let calls = 0; let inputs = $std::ranges::map([\"a\", \"b\"], fn(value: String) { calls = calls + 1; value }); let chunks = $std::text::append_chunk(inputs, \"c\"); calls", "2", level);
    await Check("let b = $std::text::Builder(); for x in $std::ranges::iota(0, 129) { b::append(\"x\"); }; $std::string::length(b::build())", "129", level);
    await Check("let pattern = $std::regex::compile(\"[0-9]+\"); pattern::replace(\"a12b\", \"x\")", "axb", level);
    await Check("let pattern = $std::regex::compile(\"[0-9]+\"); pattern::pattern()", "[0-9]+", level);
    await Check("let item = $std::ranges::first($std::regex::matches(\"A😀abc\", \"(?<part>abc)(?<missing>z)?\")); let group = $std::ranges::last(item::groups); group::success", "false", level);
    await Check("let item = $std::ranges::first($std::regex::matches(\"A😀abc\", \"(?<part>abc)\")); let group = $std::ranges::last(item::groups); group::index", "2", level);
    await Check("let item = $std::ranges::first($std::regex::matches(\"a😀bc\", \"(?<=(?<prefix>😀))b\")); let group = $std::ranges::last(item::groups); group::index", "1", level);
    await Check("let matches = $std::regex::matches(\"abc\", \"abc\"); let first = $std::ranges::first(matches); first::value = \"changed\"; $std::ranges::first(matches)::value", "abc", level);
    await Check("if false { $std::regex::is_match(\"x\", \"[\"); }; \"ok\"", "ok", level);
    Module invalid = Frontend.Compile("let _ = $std::regex::is_match(\"x\", \"[\");", "regex-pattern-error", level);
    foreach (Module stage in new[] { invalid, RoundTrip(invalid), RoundTrip(Binding.Bind(invalid, new(Environment.CurrentDirectory, HostContext.HomeDirectory))) })
    {
        try { _ = await Interpreter.ExecuteAsync(stage); }
        catch (MsyncException error) when (error.Message.Contains("regex.is_match:", StringComparison.Ordinal)) { checks++; continue; }
        throw new InvalidOperationException($"O{level} {stage.Stage} lost the selected regex parse diagnostic.");
    }
}
Console.WriteLine($"PASS: {checks} text builder and regex source/artifact contracts");

async Task Check(string source, string expected, int level)
{
    if (args.Contains("--source-library", StringComparer.Ordinal) && source.Contains("$std::text::", StringComparison.Ordinal))
        source = File.ReadAllText("stdlib/text.msync") + source.Replace("$std::text::", "", StringComparison.Ordinal);
    Module module = Frontend.Compile(source, "text-regex-journey", level);
    foreach (Module stage in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module, new(Environment.CurrentDirectory, HostContext.HomeDirectory))) })
    {
        Value result = await Interpreter.ExecuteAsync(stage);
        checks++;
        if (result.ToString() != expected)
            throw new InvalidOperationException($"O{level} {stage.Stage}: expected '{expected}', got '{result}': {source}");
    }
}

static Module RoundTrip(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream);
    stream.Position = 0;
    return Artifact.Read(stream);
}
