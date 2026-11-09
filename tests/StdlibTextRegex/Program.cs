using Msync.Core;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

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
    await Check("let b = $std::text::Builder(); b::append_all([\"a\", \"b\", \"c\"]); b::clear(); b::append(\"z\"); b::build()", "z", level);
    await Check("let b = $std::text::Builder(); for x in $std::ranges::iota(0, 129) { b::append(\"x\"); }; $std::string::length(b::build())", "129", level);
    await Check("let pattern = $std::regex::compile(\"[0-9]+\"); pattern::replace(\"a12b\", \"x\")", "axb", level);
    await Check("let item = $std::ranges::first($std::regex::matches(\"A😀abc\", \"(?<part>abc)(z)?\")); let group = $std::ranges::last(item::groups); group::success", "false", level);
    await Check("let item = $std::ranges::first($std::regex::matches(\"A😀abc\", \"(?<part>abc)\")); let group = $std::ranges::last(item::groups); group::index", "2", level);
}
Console.WriteLine($"PASS: {checks} text builder and regex source/artifact contracts");

async Task Check(string source, string expected, int level)
{
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
