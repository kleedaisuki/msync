using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

var cases = new (string Name, string Source, string Expected)[]
{
    ("distinct", """
        let nodes = $std::storage::Buffer::new([scope { red: false }, scope { red: false }]);
        let a = $std::storage::Buffer::get(nodes, 0);
        let b = $std::storage::Buffer::get(nodes, 1);
        a::red = true;
        if b::red { "WRONG" } else { "ok" }
        """, "ok"),
    ("alias", """
        let nodes = $std::storage::Buffer::new([scope { red: false }]);
        let a = $std::storage::Buffer::get(nodes, 0);
        let b = $std::storage::Buffer::get(nodes, 0);
        a::red = true;
        b::red = false;
        if a::red { "WRONG" } else { "ok" }
        """, "ok"),
    ("external", """
        let a = scope { red: false };
        let b = scope { red: false };
        let nodes = $std::storage::Buffer::new([a, b]);
        let stored = $std::storage::Buffer::get(nodes, 0);
        stored::red = true;
        if a::red and not b::red { "ok" } else { "WRONG" }
        """, "ok"),
    ("callables", """
        let one = fn(value: Number) { value + 1 };
        let two = fn(value: Number) { value + 2 };
        let values = $std::storage::Buffer::new([one]);
        $std::storage::Buffer::push(values, two);
        let first = $std::storage::Buffer::get(values, 0);
        let second = $std::storage::Buffer::get(values, 1);
        if first(4) == 5 and second(4) == 6 { "ok" } else { "WRONG" }
        """, "ok"),
    ("field-callable", """
        let old = fn(value: Number) { value + 1 };
        let objects = $std::storage::Buffer::new([scope { operation: old }]);
        let a = $std::storage::Buffer::get(objects, 0);
        let b = $std::storage::Buffer::get(objects, 0);
        a::operation = fn(value: Number) { value + 2 };
        if b::operation(4) == 6 { "ok" } else { "WRONG" }
        """, "ok"),
    ("snapshot", """
        let values = $std::storage::Buffer::new([1, 2]);
        let snapshot = $std::storage::Buffer::snapshot(values);
        $std::storage::Buffer::set(values, 0, 7);
        if $std::ranges::at(snapshot, 0) == 1 and $std::storage::Buffer::get(values, 0) == 7 { "ok" } else { "WRONG" }
        """, "ok"),
    ("nullable-member", """
        let nodes = $std::storage::Buffer::new([scope { child?: scope { text: "ok" } }]);
        let a = $std::storage::Buffer::get(nodes, 0);
        if a::child is not null { a::child::text } else { "WRONG" }
        """, "ok"),
    ("nullable-branch", """
        let nodes = $std::storage::Buffer::new([scope { child?: scope { text: "ok" }, flag: false }]);
        let a = $std::storage::Buffer::get(nodes, 0);
        let b = $std::storage::Buffer::get(nodes, 0);
        if a::child is not null {
            if a::flag { b::child = null; "WRONG" } else { a::child::text }
        } else { "WRONG" }
        """, "ok"),
    ("nested", """
        let nodes = $std::storage::Buffer::new([scope { nested: scope { red: false } }]);
        let a = $std::storage::Buffer::get(nodes, 0);
        let b = $std::storage::Buffer::get(nodes, 0);
        a::nested::red = true;
        b::nested::red = false;
        if a::nested::red { "WRONG" } else { "ok" }
        """, "ok"),
    ("first-write", """
        let values = $std::storage::Buffer::new([]);
        $std::storage::Buffer::push(values, 7);
        if $std::storage::Buffer::get(values, 0) == 7 { "ok" } else { "WRONG" }
        """, "ok"),
    ("query-alias-isolation", """
        concept Numeric { operation: fn(value: Number) -> Number, };
        fn check(value: Numeric) { 0 };
        let a = scope { operation: fn(value: Number) { value + 1 } };
        let b = scope { operation: fn(value: Number) { value + 2 } };
        let nodes = $std::storage::Buffer::new([a, b]);
        let invalid = fn {
            check($std::storage::Buffer::get(nodes, 0));
            a::operation = fn(value: Number) { true };
        };
        let allowed = $std::meta::is_invocable(invalid);
        a::operation = fn(value: Number) { true };
        if not allowed { "ok" } else { "WRONG" }
        """, "ok"),
    ("nullable-adt", """
        let values = $std::storage::Buffer::new([$std::data::Option::some(null)]);
        let value = $std::storage::Buffer::get(values, 0);
        value::Item = Number;
        if $std::data::Option::is_some(value) { "ok" } else { "WRONG" }
        """, "ok"),
    ("path-conversion", """
        let paths = $std::storage::Buffer::new([./before]);
        $std::storage::Buffer::push(paths, "./after");
        if decltype($std::storage::Buffer::get(paths, 1)) is Path { "ok" } else { "WRONG" }
        """, "ok"),
};
int checks = 0;
foreach (var level in new[] { 0, 2, 3 })
foreach (var test in cases)
{
    var module = Compiler.Compile(test.Source, test.Name + ".msync", level);
    using var relocatable = new MemoryStream();
    Artifact.Write(module, relocatable);
    relocatable.Position = 0;
    var executable = Binding.Bind(module, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using var bound = new MemoryStream();
    Artifact.Write(executable, bound);
    bound.Position = 0;
    foreach (var image in new[] { module, Artifact.Read(relocatable), Artifact.Read(bound) })
    {
        var result = await Interpreter.ExecuteAsync(image);
        if (result is not StringValue text || text.Data != test.Expected)
            throw new InvalidOperationException($"{test.Name} O{level}: {result}");
        checks++;
    }
}
foreach (var level in new[] { 0, 2, 3 })
foreach (var source in new[]
{
    """
    concept Numeric { operation: fn(value: Number) -> Number, };
    fn check(value: Numeric) { 0 };
    let a = scope { operation: fn(value: Number) { value + 1 } };
    let b = scope { operation: fn(value: Number) { value + 2 } };
    let nodes = $std::storage::Buffer::new([a, b]);
    check($std::storage::Buffer::get(nodes, 0));
    a::operation = fn(value: Number) { true };
    """,
    """
    let values = $std::storage::Buffer::new([$std::data::Option::some(null)]);
    let value = $std::storage::Buffer::get(values, 0);
    value::value = 42;
    """,
    "let x = $std::storage::Buffer::new([1]); $std::storage::Buffer::push(x, true);",
    "let x = $std::storage::Buffer::new([scope { red: false }]); $std::storage::Buffer::push(x, scope { red: 1 });",
    "let x = $std::storage::Buffer::new([scope { red: false }]); $std::storage::Buffer::set(x, 0, scope { wrong: false });",
    """
    let nodes = $std::storage::Buffer::new([scope { child?: scope { text: "ok" } }]);
    let a = $std::storage::Buffer::get(nodes, 0);
    let b = $std::storage::Buffer::get(nodes, 0);
    if a::child is not null { b::child = null; a::child::text } else { "WRONG" }
    """,
    """
    let nodes = $std::storage::Buffer::new([
        scope { child?: scope { text: "ok" } }, scope { child?: null }
    ]);
    let a = $std::storage::Buffer::get(nodes, 0);
    let b = $std::storage::Buffer::get(nodes, 1);
    if a::child is not null { b::child::text } else { "WRONG" }
    """,
    """
    fn clear(value) { value::child = null; };
    let nodes = $std::storage::Buffer::new([scope { child?: scope { text: "ok" } }]);
    let a = $std::storage::Buffer::get(nodes, 0);
    let b = $std::storage::Buffer::get(nodes, 0);
    if a::child is not null { clear(b); a::child::text } else { "WRONG" }
    """,
})
{
    try { Compiler.Compile(source, "invalid-indexed-storage.msync", level); }
    catch (CompileException) { checks++; continue; }
    throw new InvalidOperationException("Expected fixed storage/nullability rejection: " + source);
}
CompilerSession session = new();
session.Compile("let buffer = $std::storage::Buffer::new([]);");
session.AcceptSubmission();
try
{
    session.Compile("$std::storage::Buffer::push(buffer, 1); let wrong: Number = true;");
    throw new InvalidOperationException("Expected failed submission");
}
catch (CompileException) { checks++; }
session.Compile("$std::storage::Buffer::push(buffer, \"ok\");");
checks++;
checks += await RuntimeCarrierChecks.RunAsync();
Console.WriteLine($"PASS {checks} indexed storage contracts");
