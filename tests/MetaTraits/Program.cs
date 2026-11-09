using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

int checks = 0;
var programs = new (string Name, string Source, Value Expected)[]
{
    ("invocable", """let f = fn(x: Number) { x + 1 }; $std::meta::is_invocable(f, Number)""", Value.True),
    ("not-invocable", """let f = fn(x: Number) { x + 1 }; $std::meta::is_invocable(f, Scope)""", Value.False),
    ("arity-rejection", """let f = fn(x: Number) { x + 1 }; $std::meta::is_invocable(f)""", Value.False),
    ("result-type", """let f = fn(x: Number) { x + 1 }; $std::meta::invoke_result(f, Number)""", new TypeValue(TypeRef.Number)),
    ("implicit-result", """let f = fn(x: String) { x }; $std::meta::is_invocable_r(Path, f, String)""", Value.True),
    ("explicit-only-result", """let f = fn(x: Number) { x }; $std::meta::is_invocable_r(String, f, Number)""", Value.False),
    ("parameter-count", """let f = fn(a: String, b?: Number) { a }; $std::meta::parameter_count(f)""", new NumberValue(2)),
    ("unknown-parameter-count", """$std::meta::parameter_count(3)""", Value.Null),
    ("variadic", """$std::meta::is_variadic($std::ranges::of)""", Value.True),
    ("native-result", """$std::meta::invoke_result($std::string::concat, String, String)""", new TypeValue(TypeRef.String)),
    ("process-result", """$std::meta::invoke_result(./never-launched, String)""", new TypeValue(TypeRef.Process)),
    ("has-member", """$std::meta::has_member(scope { value: 3 }, "value")""", Value.True),
    ("missing-member", """$std::meta::has_member(scope { value: 3 }, "missing")""", Value.False),
    ("member-type", """$std::meta::member_type(scope { value: 3 }, "value")""", new TypeValue(TypeRef.Number)),
    ("missing-member-type", """$std::meta::member_type(scope { value: 3 }, "missing")""", Value.Null),
    ("member-count", """$std::meta::member_count(scope { value: 3, text: "a" })""", new NumberValue(2)),
    ("member-names", """$std::string::join($std::meta::member_names(scope { z: 3, a: "a" }), ",")""", new StringValue("a,z")),
    ("process-members", """$std::meta::member_type(./never-launched(), "stdout")""", new TypeValue(TypeRef.String)),
    ("parameter-types", """let f = fn(a: String, b?: Number) { a }; $std::ranges::at($std::meta::parameter_types(f), 1)""", new TypeValue(TypeRef.Number.WithNull)),
    ("generic-selection-number", """
        fn adjust(value) {
            if $std::meta::is_number(decltype(value)) { value + 1 }
            else { $std::string::concat(value, "!") }
        };
        adjust(1)
        """, new NumberValue(2)),
    ("generic-selection-text", """
        fn adjust(value) {
            if $std::meta::is_number(decltype(value)) { value + 1 }
            else { $std::string::concat(value, "!") }
        };
        adjust("Klee")
        """, new StringValue("Klee!")),
    ("no-runtime-effect", """
        let count = 0;
        let f = fn(value: Number) { count = count + 1; value + 1 };
        $std::meta::is_invocable(f, { count = count + 100; 3 });
        $std::meta::invoke_result(f, Number);
        count
        """, new NumberValue(0)),
    ("no-failed-probe-cache", """
        let f = fn(value: Scope) { value::number + 1 };
        $std::meta::is_invocable(f, scope { text: "wrong" });
        f(scope { number: 4 })
        """, new NumberValue(5)),
    ("no-storage-constraint-leak", """
        let record = scope { value: 1 };
        let invalid = fn(item: Scope) { item::value = "wrong"; };
        $std::meta::is_invocable(invalid, record);
        record::value = 2;
        record::value
        """, new NumberValue(2)),
    ("concept-probe", """
        concept Label { name: String, };
        let accept = fn(value: Label) { value::name };
        $std::meta::is_invocable(accept, scope { name: "Klee" })
        """, Value.True),
    ("concept-probe-rejection", """
        concept Label { name: String, };
        let accept = fn(value: Label) { value::name };
        $std::meta::is_invocable(accept, scope { name: 4 })
        """, Value.False),
    ("alias-query", """
        let query = $std::meta::is_invocable;
        query(fn(value: String) { value }, String)
        """, Value.True),
    ("static-transform", """$std::meta::is_nullable($std::meta::add_null(Number))""", Value.True),
    ("type-producer-effects-retained", """
        let calls = 0;
        fn number_type() { calls = calls + 1; Number };
        $std::meta::is_same(number_type(), Number);
        calls
        """, new NumberValue(1)),
    ("buffer-probe-isolation", """
        let buffer = $std::storage::buffer_new([]);
        let populate = fn { $std::storage::buffer_push(buffer, 1) };
        $std::meta::is_invocable(populate);
        $std::storage::buffer_push(buffer, "Klee");
        $std::storage::buffer_get(buffer, 0)
        """, new StringValue("Klee")),
    ("failed-buffer-probe-isolation", """
        let buffer = $std::storage::buffer_new([]);
        let invalid = fn {
            $std::storage::buffer_push(buffer, 1);
            scope { value: 1 }::missing
        };
        $std::meta::is_invocable(invalid);
        $std::storage::buffer_push(buffer, "Klee");
        $std::storage::buffer_get(buffer, 0)
        """, new StringValue("Klee")),
    ("buffer-memory-view-isolation", """
        let record = scope { flag: true };
        let inspect = fn { $std::storage::buffer_new([record]) };
        $std::meta::invoke_result(inspect);
        if record::flag { "Klee" } else { scope {}::missing }
        """, new StringValue("Klee")),
};

foreach (int level in new[] { 0, 2, 3 })
    foreach (var (name, source, expected) in programs)
    {
        Module original = Compiler.Compile(source, name + ".msync", level);
        foreach (Module module in Stages(original))
        {
            Value actual = await Interpreter.ExecuteAsync(module);
            Require(ValueRepresentation.Comparer.Equals(actual, expected), $"{name}/O{level}: expected {expected}, got {actual}");
        }
    }

foreach (string source in new[]
{
    "$std::meta::invoke_result(fn(value:Number){value}, Scope)",
    "$std::meta::has_member(scope { item: 1 }, 2)",
    "$std::meta::is_invocable_r(1, fn{})",
    "$std::meta::is_invocable()"
})
{
    try { Compiler.Compile(source); throw new InvalidOperationException("Metadata query should be rejected: " + source); }
    catch (CompileException error) { Require(error.Diagnostic.Code == "E0222", error.Diagnostic.Message); }
}
Console.WriteLine($"PASS {checks} metadata query checks");

static IEnumerable<Module> Stages(Module original)
{
    yield return original;
    yield return Reload(original);
    yield return Reload(Binding.Bind(original, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
}
static Module Reload(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
