using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
bool metadataOnly = args.Contains("--meta", StringComparer.Ordinal);
if (metadataOnly)
{
    await Boolean("$std::meta::is_invocable(fn(value) { value + 1 }, Number)", true);
    await Boolean("$std::meta::is_invocable(fn(value) { value + 1 }, Bool)", false);
    await Boolean("fn bad(value) { 1 + true }; $std::meta::is_invocable(bad, Number)", false);
    await Boolean("fn recur(value: Number) -> Number { if value > 0 { recur(value - 1) } else { 0 } }; $std::meta::is_invocable(recur, Number)", true);
    await Boolean("$std::meta::is_invocable_r(String, fn(value) { value }, Path)", true);
    await Boolean("$std::meta::is_invocable_r(Number, fn(value) { value }, Path)", false);
    await Boolean("$std::meta::is_same($std::meta::invoke_result(fn(value) { scope { item: value } }, Number), Scope)", true);
    await Number("$std::meta::parameter_count(fn(first, second: Number) { first })", 2);
    await Boolean("native fn first(values: Range) -> Type; first($std::meta::parameter_types(fn(value: Number) { value })) == Number", true);
    await Boolean("$std::meta::is_variadic(./command)", true);
    await Boolean("$std::meta::has_member(scope { item: 3 }, \"item\")", true);
    await Number("$std::meta::member_count(scope { first: 3, second: true })", 2);
    await Text("native fn first(values: Range) -> String; first($std::meta::member_names(scope { z: 3, a: true }))", "a");
    await Boolean("$std::meta::is_same($std::meta::member_type(scope { item: 3 }, \"item\"), Number)", true);
    await Number("let count = 0; fn effect() { count = 1; 3 }; $std::meta::is_invocable(effect); count", 0);
    await Boolean("let count = 0; $std::meta::has_member({ count = 1; scope { item: true } }, \"item\"); count == 0", true);
    await Number("let count = 0; fn choose() { count = 1; $std::meta::is_invocable }; choose()(fn(value) { value }, Number); count", 1);
    Reject("fn bad(value) { 1 + true }; $std::meta::invoke_result(bad, Number)", "E0222");
    Console.WriteLine($"PASS {checks} dependent metadata HIR journeys");
    return;
}
await Number("1 + 2 * 3", 7);
await Number("-2 ** 2", -4);
await Number("\"2.5\" as Number", 2.5);
await Boolean("let value? = null; value is null", true);
await Boolean("let value?: Number = 3; value = null; value is null", true);
await Boolean("let value? = null; value is not null and value > 0", false);
await Text("fn text(value: String) -> String { value }; text(./sample)", "./sample");
await Number("let object = scope { value: 1 }; let alias = object; alias::value = 7; object::value", 7);
await Number("concept Numeric { item: typename Item }; fn use(value: Numeric) { value::item }; use(scope { item: 4 })", 4);
Reject("null == null", "E0201");
Reject("let item = 1; item = true", "E0203");
Reject("let object = scope { item: 1 }; object::item = true", "E0203");
Reject("null as Number", "E0220");
Reject("concept Numeric { item: Number }; fn use(value: Numeric) { value }; use(scope { item: true })", "E0214");

const string transform = "native fn transform(values: Range, operation: Function) -> Range;";
await Numbers(transform + "transform([1, 2], fn(value) { value + 1 })", [2, 3]);
await Number(transform + "let state = scope { changed: false }; let pending = transform([1], fn(value) { state::changed = true; value }); if state::changed { 1 } else { 0 }", 0);
await Number(transform + "native fn materialize(values: Range) -> Range; let state = scope { changed: false }; let pending = transform([1], fn(value) { state::changed = true; value }); materialize(pending); if state::changed { 1 } else { 0 }", 1);
await Number(transform + "native fn materialize(values: Range) -> Range; let state?: Number = 4; let pending = transform([1], fn(value) { state = null; value }); materialize(pending); if state is null { 1 } else { 0 }", 1);
await Number(transform + "native fn materialize(values: Range) -> Range; let state = scope { changed: false }; fn build() { transform([1], fn(value) { state::changed = true; value }) }; materialize(build()); if state::changed { 1 } else { 0 }", 1);
await Number(transform + "native fn materialize(values: Range) -> Range; let state = scope { changed: false }; materialize(transform([], fn(value) { state::changed = true; value })); if state::changed { 1 } else { 0 }", 0);
await Number(transform + "native fn materialize(values: Range) -> Range; native fn single(value) -> Range; let state = scope { changed: false }; let pending = transform([1], fn(value) { state::changed = true; value }); materialize(single(pending)); if state::changed { 1 } else { 0 }", 0);
const string filter = "native fn filter(values: Range, operation: Function) -> Range;";
await Numbers(filter + "filter([1, 2, 3], fn(value) { value > 1 })", [2, 3]);
Reject(filter + "filter([1], fn(value) { value })", "E0203");
await Numbers("$std::ranges::concat([1], [2])", [1, 2]);
await Numbers("native fn flatten(values: Range) -> Range; flatten([[1], [2]])", [1, 2]);
await Number("native fn zip(left: Range, right: Range) -> Range; native fn first(values: Range) -> Scope; first(zip([3], [4]))::second", 4);
await Number("native fn enumerate_from(values: Range, index: Number) -> Range; native fn first(values: Range) -> Scope; first(enumerate_from([8], 5))::index", 5);
await Numbers("$std::ranges::repeat(4, 2)", [4, 4]);
await Numbers("native fn scan(values: Range, initial: Number, operation: Function) -> Range; scan([1, 2], 0, fn(a, b) { a + b })", [1, 3]);
await Boolean("native fn is_same(first: Type, second: Type) -> Bool; is_same(decltype(3), Number)", true);
const string indexed = """
    native fn buffer_new(initial: Range) -> Range;
    native fn buffer_get(items: Range, index: Number) -> Number;
    native fn buffer_set(items: Range, index: Number, value?) -> Null;
    native fn buffer_snapshot(items: Range) -> Range;
    """;
await Number(indexed + "let items = buffer_new([1]); buffer_set(items, 0, 7); buffer_get(items, 0)", 7);
await Numbers(indexed + "let items = buffer_new([1]); let saved = buffer_snapshot(items); buffer_set(items, 0, 7); saved", [1]);
Reject(indexed + "let items = buffer_new([1]); buffer_set(items, 0, true)", "E0203");
await Boolean(indexed + "let first = scope { red: false }; let second = scope { red: false }; let items = buffer_new([first, second]); let a = buffer_get(items, 0); let b = buffer_get(items, 1); a::red = true; if b::red { true } else { false }", false);
await Boolean(indexed + "let first = scope { red: false }; let items = buffer_new([first]); let a = buffer_get(items, 0); let b = buffer_get(items, 0); a::red = true; b::red = false; if a::red { true } else { false }", false);
await Boolean(indexed + "let first = scope { red: false }; let items = buffer_new([first]); let a = buffer_get(items, 0); a::red = true; if first::red { true } else { false }", true);
Console.WriteLine($"PASS {checks} typed HIR type/operator/native contracts");

async Task<Value> Execute(string source, ArtifactStage stage)
{
    HirModule hir = Compiler.Analyze(source, "hir-typing.msync");
    if (metadataOnly)
    {
        Check(!hir.Calls.Any(call => call.NativeName is { } name && name.StartsWith("meta.", StringComparison.Ordinal)
            && name is not "meta.is_same"), "Dependent metadata must not survive as a runtime native call");
        using MemoryStream graph = new();
        HirArtifact.Write(hir, graph);
        graph.Position = 0;
        hir = HirArtifact.Read(graph);
    }
    Module code = HirLowerer.Lower(hir, new(OptimizationLevel: 0)).Module;
    if (stage == ArtifactStage.Executable)
        code = Msync.Optimization.Binding.Bind(code, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream buffer = new();
    Artifact.Write(code, buffer);
    buffer.Position = 0;
    return await Interpreter.ExecuteAsync(Artifact.Read(buffer));
}

async Task Number(string source, double expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value value = await Execute(source, stage);
        Check(value is NumberValue actual && actual.Data == expected,
            $"{source}: expected Number {expected}, received {value.Type} '{value}' ({stage})");
    }
}
async Task Boolean(string source, bool expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
        Check(await Execute(source, stage) is BoolValue actual && actual.Data == expected, source);
}
async Task Text(string source, string expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value result = await Execute(source, stage);
        Check(result is StringValue actual && actual.Data == expected,
            $"{source}: expected String '{expected}', received {result.Type} '{result}' ({stage})");
    }
}
async Task Numbers(string source, double[] expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value value = await Execute(source, stage);
        Check(value is RangeValue, "Expected Range");
        List<double> actual = [];
        await foreach (Value item in ((RangeValue)value).Items) actual.Add(((NumberValue)item).Data);
        Check(actual.SequenceEqual(expected), source);
    }
}
void Reject(string source, string code)
{
    try { Compiler.Analyze(source, "hir-typing-error.msync"); }
    catch (CompileException error) { Check(error.Diagnostic.Code == code, error.Diagnostic.Message); return; }
    throw new InvalidOperationException("Expected " + code + ": " + source);
}
void Check(bool condition, string description)
{
    checks++;
    if (!condition) throw new InvalidOperationException(description);
}
