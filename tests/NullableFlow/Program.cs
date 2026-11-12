using Msync.Optimization;
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
    ("nullable-loop-stream", """
        let trace = 0;
        let stream = $std::ranges::transform([1, 2, 3], fn(item: Number) {
            trace = trace * 10 + item;
            if item == 2 { null } else { item }
        });
        for value? in stream {
            if value is null { trace = trace * 10 + 9; } else { trace = trace * 10 + 8; };
        };
        trace
        """, 182938),
    ("nonnull-loop-marked", """
        fn number(value: Number) { value };
        let total = 0;
        for value? in [2, 5] { total = total + number(value); };
        total
        """, 7),
    ("independent-structural-summaries", """
        let first = scope { kind: "marker", value: 0 };
        let second = scope { kind: "another", value: 0 };
        fn head(values: Range) { $std::ranges::front(values) };
        let score = 0;
        for row in [first, second] {
            if row::kind is not null {
                let entry = head([scope { priority: 7 }, scope { priority: 9 }]);
                score = score + entry::priority;
            };
        };
        score
        """, 14),
    ("independent-summary-write", """
        let first = scope { kind: "marker", value: 0 };
        let second = scope { kind: "another", value: 0 };
        fn head(values: Range) { $std::ranges::front(values) };
        let score = 0;
        for row in [first, second] {
            row::kind = "changed";
            let entry = head([scope { priority: 7 }, scope { priority: 9 }]);
            score = score + entry::priority;
        };
        if first::kind == "changed" and second::kind == "changed" { score } else { 0 }
        """, 14),
];
if (args.Contains("--indexed", StringComparer.Ordinal))
    cases.Add(("indexed-value", """
        let values = $std::storage::vector_new([4]);
        let below? = $std::storage::vector_get(values, 0);
        $std::storage::vector_set(values, 0, below);
        $std::storage::vector_get(values, 0)
        """, 4));
(string Source, string Code)[] rejected =
[
    ("let value? = 4; value = \"text\";", "E0203"),
    ("let value? = 4; value = null; fn take(item: Number) { item }; take(value)", "E0203"),
    ("let value? = 4; let clear = fn { value = null; }; clear(); fn take(item: Number) { item }; take(value)", "E0203"),
    ("for value in [1, null] { value; };", "E0201"),
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
    foreach (var (source, code) in rejected)
    {
        try { Compiler.Compile(source, "nullable-rejected.msync", level); }
        catch (CompileException error) when (error.Diagnostic.Code == code)
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
