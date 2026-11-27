using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

var cases = new (string Name, string Source, string Expected)[]
{
    ("heap-order-writes", """
        let storage = $std::storage::vector_new([5, 1, 2]);
        let policy = object {
            less: fn(a: Number, b: Number) -> Bool {
                if a == 2 and b == 1 { $std::storage::vector_set(storage, 1, 99); };
                a < b
            },
        };
        $std::containers::Heap::sift_down(object { items: storage, order: policy }, 0);
        $std::text::format("{} {}", $std::storage::vector_get(storage, 0),
            $std::storage::vector_get(storage, 1))
        """, "5 99"),
    ("distinct", """
        let nodes = $std::storage::vector_new([object { red: false }, object { red: false }]);
        let a = $std::storage::vector_get(nodes, 0);
        let b = $std::storage::vector_get(nodes, 1);
        a::red = true;
        if b::red { "WRONG" } else { "ok" }
        """, "ok"),
    ("alias", """
        let nodes = $std::storage::vector_new([object { red: false }]);
        let a = $std::storage::vector_get(nodes, 0);
        let b = $std::storage::vector_get(nodes, 0);
        a::red = true;
        b::red = false;
        if a::red { "WRONG" } else { "ok" }
        """, "ok"),
    ("external", """
        let a = object { red: false };
        let b = object { red: false };
        let nodes = $std::storage::vector_new([a, b]);
        let stored = $std::storage::vector_get(nodes, 0);
        stored::red = true;
        if a::red and not b::red { "ok" } else { "WRONG" }
        """, "ok"),
    ("callables", """
        let one = fn(value: Number) { value + 1 };
        let two = fn(value: Number) { value + 2 };
        let values = $std::storage::vector_new([one]);
        $std::storage::vector_push(values, two);
        let first = $std::storage::vector_get(values, 0);
        let second = $std::storage::vector_get(values, 1);
        if first(4) == 5 and second(4) == 6 { "ok" } else { "WRONG" }
        """, "ok"),
    ("field-callable", """
        let old = fn(value: Number) { value + 1 };
        let objects = $std::storage::vector_new([object { operation: old }]);
        let a = $std::storage::vector_get(objects, 0);
        let b = $std::storage::vector_get(objects, 0);
        a::operation = fn(value: Number) { value + 2 };
        let c = $std::storage::vector_get(objects, 0);
        if b::operation(4) == 6 and c::operation(4) == 6 { "ok" } else { "WRONG" }
        """, "ok"),
    ("snapshot", """
        let values = $std::storage::vector_new([1, 2]);
        let snapshot = $std::storage::vector_snapshot(values);
        $std::storage::vector_set(values, 0, 7);
        if $std::ranges::at(snapshot, 0) == 1 and $std::storage::vector_get(values, 0) == 7 { "ok" } else { "WRONG" }
        """, "ok"),
    ("nullable-member", """
        let nodes = $std::storage::vector_new([object { child?: object { text: "ok" } }]);
        let a = $std::storage::vector_get(nodes, 0);
        if a::child is not null { a::child::text } else { "WRONG" }
        """, "ok"),
    ("nullable-branch", """
        let nodes = $std::storage::vector_new([object { child?: object { text: "ok" }, flag: false }]);
        let a = $std::storage::vector_get(nodes, 0);
        let b = $std::storage::vector_get(nodes, 0);
        if a::child is not null {
            if a::flag { b::child = null; "WRONG" } else { a::child::text }
        } else { "WRONG" }
        """, "ok"),
    ("nested", """
        let nodes = $std::storage::vector_new([object { nested: object { red: false } }]);
        let a = $std::storage::vector_get(nodes, 0);
        let b = $std::storage::vector_get(nodes, 0);
        a::nested::red = true;
        b::nested::red = false;
        if a::nested::red { "WRONG" } else { "ok" }
        """, "ok"),
    ("typed-empty", """
        let values = $std::storage::vector_empty(Number);
        $std::storage::vector_push(values, 7);
        if $std::storage::vector_get(values, 0) == 7 { "ok" } else { "WRONG" }
        """, "ok"),
    ("slot-nested-owner", """
        let row = object { keys: $std::storage::vector_empty(String) };
        let rows = $std::slots::new(decltype(row));
        let index = $std::slots::allocate(rows, row);
        let alias = $std::slots::get(rows, index);
        $std::storage::vector_push(alias::keys, "ok");
        $std::storage::vector_get(row::keys, 0)
        """, "ok"),
    ("query-alias-isolation", """
        concept Numeric { operation: fn(value: Number) -> Number, };
        fn check(value: Numeric) { 0 };
        let a = object { operation: fn(value: Number) { value + 1 } };
        let b = object { operation: fn(value: Number) { value + 2 } };
        let nodes = $std::storage::vector_new([a, b]);
        let invalid = fn {
            check($std::storage::vector_get(nodes, 0));
            a::operation = fn(value: Number) { true };
        };
        let allowed = $std::meta::is_invocable(invalid);
        a::operation = fn(value: Number) { true };
        if not allowed { "ok" } else { "WRONG" }
        """, "ok"),
    ("nullable-adt", """
        let values = $std::storage::vector_new([$std::data::Option::some(null)]);
        let value = $std::storage::vector_get(values, 0);
        value::Item = Number;
        if $std::data::Option::is_some(value) { "ok" } else { "WRONG" }
        """, "ok"),
    ("path-conversion", """
        let paths = $std::storage::vector_new([./before]);
        $std::storage::vector_push(paths, "./after");
        if decltype($std::storage::vector_get(paths, 1)) is Path { "ok" } else { "WRONG" }
        """, "ok"),
};
int checks = 0;
bool decisionsOnly = args.Contains("--decisions-only", StringComparer.Ordinal);
foreach (var level in new[] { 0, 2, 3 })
foreach (var test in cases.Where(test => !decisionsOnly || test.Name is "heap-order-writes"
    or "field-callable" or "typed-empty" or "slot-nested-owner" or "snapshot"))
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
if (decisionsOnly)
{
    checks += await RuntimeCarrierChecks.RunAsync();
    Console.WriteLine($"PASS: {checks} cohort, Heap policy and snapshot ownership decisions");
    return;
}
foreach (var level in new[] { 0, 2, 3 })
foreach (var source in new[]
{
    """
    concept Numeric { operation: fn(value: Number) -> Number, };
    fn check(value: Numeric) { 0 };
    let a = object { operation: fn(value: Number) { value + 1 } };
    let b = object { operation: fn(value: Number) { value + 2 } };
    let nodes = $std::storage::vector_new([a, b]);
    check($std::storage::vector_get(nodes, 0));
    a::operation = fn(value: Number) { true };
    """,
    """
    let values = $std::storage::vector_new([$std::data::Option::some(null)]);
    let value = $std::storage::vector_get(values, 0);
    value::value = 42;
    """,
    "let x = $std::storage::vector_new([1]); $std::storage::vector_push(x, true);",
    "$std::storage::vector_get([1], 0);",
    "let x = $std::storage::vector_new([]); $std::storage::vector_push(x, 1);",
    "let x = $std::storage::vector_new([object { red: false }]); $std::storage::vector_push(x, object { red: 1 });",
    "let x = $std::storage::vector_new([object { red: false }]); $std::storage::vector_set(x, 0, object { wrong: false });",
    """
    let nodes = $std::storage::vector_new([object { child?: object { text: "ok" } }]);
    let a = $std::storage::vector_get(nodes, 0);
    let b = $std::storage::vector_get(nodes, 0);
    if a::child is not null { b::child = null; a::child::text } else { "WRONG" }
    """,
    """
    let nodes = $std::storage::vector_new([
        object { child?: object { text: "ok" } }, object { child?: null }
    ]);
    let a = $std::storage::vector_get(nodes, 0);
    let b = $std::storage::vector_get(nodes, 1);
    if a::child is not null { b::child::text } else { "WRONG" }
    """,
    """
    fn clear(value) { value::child = null; };
    let nodes = $std::storage::vector_new([object { child?: object { text: "ok" } }]);
    let a = $std::storage::vector_get(nodes, 0);
    let b = $std::storage::vector_get(nodes, 0);
    if a::child is not null { clear(b); a::child::text } else { "WRONG" }
    """,
})
{
    try { Compiler.Compile(source, "invalid-indexed-storage.msync", level); }
    catch (CompileException) { checks++; continue; }
    throw new InvalidOperationException("Expected fixed storage/nullability rejection: " + source);
}
CompilerSession session = new();
session.Compile("let vector = $std::storage::vector_empty(Number);");
session.AcceptSubmission();
try
{
    session.Compile("$std::storage::vector_push(vector, 1); let wrong: Number = true;");
    throw new InvalidOperationException("Expected failed submission");
}
catch (CompileException) { checks++; }
session.Compile("$std::storage::vector_push(vector, 2);");
checks++;
checks += await RuntimeCarrierChecks.RunAsync();
Console.WriteLine($"PASS {checks} indexed storage contracts");
