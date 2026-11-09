using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
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
Console.WriteLine($"PASS {checks} typed HIR type/operator/native contracts");

async Task<Value> Execute(string source, ArtifactStage stage)
{
    HirModule hir = Compiler.Analyze(source, "hir-typing.msync");
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
        Check(value is NumberValue actual && actual.Data == expected, source + " did not return expected Number");
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
