using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

List<(string Name, string Source, double Expected)> cases =
[
    ("nonnull-initializer", "let value? = 4; fn take(item: Number) { item }; take(value)", 4),
    ("nullable-storage", """
        let value? = 4;
        fn take(item: Number) { item };
        let first = take(value);
        value = null;
        if value is null {
            value = 8;
            first + take(value)
        } else { 0 }
        """, 12),
    ("later-reassignment", "let value? = 4; value = null; value = 5; value", 5),
    ("captured-write", """
        let value? = 4;
        let clear = fn { value = null; };
        clear();
        if value is null { 5 } else { value }
        """, 5),
    ("generic-result", "fn identity(item) { item }; let value? = identity(4); value + 1", 5),
];
if (args.Contains("--indexed", StringComparer.Ordinal))
    cases.Add(("indexed-value", """
        let values = $std::storage::Buffer::new([4]);
        let below? = $std::storage::Buffer::get(values, 0);
        $std::storage::Buffer::set(values, 0, below);
        $std::storage::Buffer::get(values, 0)
        """, 4));
string[] rejected =
[
    "let value? = 4; value = \"text\";",
    "let value? = 4; value = null; fn take(item: Number) { item }; take(value)",
    "let value? = 4; let clear = fn { value = null; }; clear(); fn take(item: Number) { item }; take(value)",
];
int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    foreach (var test in cases)
    {
        var compiled = Compiler.Compile(test.Source, $"nullable-{test.Name}.msync", level);
        foreach (var image in Images(compiled))
        {
            var actual = await Interpreter.ExecuteAsync(image);
            if (actual is not NumberValue number || number.Data != test.Expected)
                throw new InvalidOperationException($"{test.Name} O{level} {image.Stage}: expected {test.Expected}, got {actual}");
            checks++;
        }
    }
    foreach (string source in rejected)
    {
        try { Compiler.Compile(source, "nullable-rejected.msync", level); }
        catch (CompileException error) when (error.Diagnostic.Code == "E0203")
        {
            checks++;
            continue;
        }
        throw new InvalidOperationException($"O{level} accepted an incompatible assignment or known-null argument: {source}");
    }
}
Console.WriteLine($"PASS {checks} nullable current-value and fixed-storage checks");

static IEnumerable<Module> Images(Module compiled)
{
    yield return compiled;
    yield return RoundTrip(compiled);
    yield return RoundTrip(Binding.Bind(compiled, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
}

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}
