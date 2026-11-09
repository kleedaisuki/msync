using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
foreach (var test in new (string Name, string Source, double Expected, string Function, int Instances)[]
{
    ("scalar-code-sharing", "fn add(x) { x + 1 }; add(2); add(7)", 8, "add", 1),
    ("bool-code-sharing", "fn choose(x) { if x { 3 } else { 8 } }; choose(true); choose(false)", 8, "choose", 1),
    ("scope-result-projection", "fn wrap(x) { scope { value: x } }; let a = scope { n: 1 }; let b = scope { n: 9 }; let first = wrap(a); let second = wrap(b); second::value::n", 9, "wrap", 1),
    ("captured-location", "let n = 1; let get = fn { n }; get(); n = 9; get()", 9, "<anonymous>", -1),
    ("alias-sensitive-flow", "fn choose(a, b) { a::item = null; if b::item? { 0 } else { b::item::n } }; let a = scope { item?: scope { n: 1 } }; let b = scope { item?: scope { n: 2 } }; choose(a, b); let c = scope { item?: scope { n: 3 } }; choose(c, c)", 0, "choose", 2),
    ("nullable-field-effects", "fn clear(x) { x::item = null }; let a = scope { item?: scope { n: 1 } }; clear(a); if a::item? { 7 } else { a::item::n }", 7, "clear", 1),
    ("nullable-capture-effects", "let value? = scope { n: 1 }; fn clear() { value = null }; clear(); if value? { 7 } else { value::n }", 7, "clear", 1),
    ("fresh-field-capture-identity", "fn make() { scope { value?: 1, get: fn { value } } }; let object = make(); object::value = null; if object::get()? { 7 } else { 9 }", 7, "make", 1),
    ("fresh-field-capture-separation", "fn make() { scope { value?: 1, get: fn { value } } }; let first = make(); let second = make(); first::value = null; if second::get()? { 9 } else { 7 }", 7, "make", 1),
    ("scope-write-projection", "fn set(x) { x::n = 7 }; let a = scope { n: 1 }; let b = scope { n: 2 }; set(a); set(b); b::n", 7, "set", 1),
    ("returned-closure-projection", "fn make(x) { fn { x } }; let first = make(2); let second = make(9); first(); second()", 9, "make", 1),
    ("recursive-inferred-signature", "fn count(x) { if x == 0 { 0 } else { count(x - 1) } }; count(3)", 0, "count", 1),
    ("query-does-not-publish", "fn add(x) { x + 1 }; let t = decltype(add(2)); 3", 3, "add", 0),
    ("recursive-query-promise", "fn count(x: Number) -> Number { if x == 0 { 0 } else { count(x - 1) } }; let t = decltype(count(2)); count(2)", 0, "count", 1)
})
{
    HirModule module = Compiler.Analyze(test.Source, test.Name + ".msync");
    using MemoryStream artifact = new();
    HirArtifact.Write(module, artifact);
    artifact.Position = 0;
    module = HirArtifact.Read(artifact);
    int count = module.Functions.Count(function => function.Name == test.Function);
    Require(test.Instances < 0 || count == test.Instances, $"{test.Name}: {count} instances, expected {test.Instances}");
    Value value = await Interpreter.ExecuteAsync(HirLowerer.Lower(module).Module);
    Require(value is NumberValue number && number.Data == test.Expected, $"{test.Name}: {value}, expected {test.Expected}");
}
foreach (var (source, expected) in new[]
{
    ("fn text(value: String) -> String { value }; text(./sample)", "./sample"),
    ("fn make(value: String) { fn { value } }; let result = make(./sample); result()", "./sample")
})
{
    var module = Compiler.Analyze(source, "coercion.msync");
    var value = await Interpreter.ExecuteAsync(HirLowerer.Lower(module).Module);
    Require(value is StringValue text && text.Data == expected, $"Implicit parameter coercion returned {value}");
}
Console.WriteLine($"PASS {checks} generic instance/provenance source journeys");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
