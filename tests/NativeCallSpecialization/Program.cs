using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

int checks = 0;
BindingContext context = new(Environment.CurrentDirectory, Environment.CurrentDirectory);
const string buffers = """
    let items = $std::storage::Buffer::new([10, 20]);
    let before = $std::storage::Buffer::get(items, 0);
    $std::storage::Buffer::set(items, 1, 99);
    let after = $std::storage::Buffer::get(items, 0);
    let first_size = $std::storage::Buffer::size(items);
    $std::storage::Buffer::set(items, 1, 77);
    let second_size = $std::storage::Buffer::size(items);
    before + after + first_size + second_size
    """;
const string order = """
    let selected = $std::math::abs;
    fn argument() {
        global selected;
        selected = $std::math::ceil;
        -1.5
    };
    let first = selected(argument());
    let second = selected(-1.5);
    first * 10 + second
    """;
const string nativeAndSource = """
    let selected = $std::math::abs;
    fn argument() {
        global selected;
        selected = fn(value: Number) { value - 10 };
        -1.5
    };
    let first = selected(argument());
    let second = selected(-1.5);
    first * 10 + second
    """;
const string captured = """
    native fn abs(value: Number) -> Number;
    fn positive(value: Number) -> Number { abs(value) };
    positive(-3)
    """;
const string nested = """
    let selected = $std::math::abs;
    let state = 0;
    fn receiver() { global state; state = state * 10 + 1; selected };
    fn argument() { global state; state = state * 10 + 2; -3 };
    let result = receiver()(argument());
    state * 10 + result
    """;
foreach (var (name, source, expected) in new (string, string, double)[]
    { ("public-buffer", buffers, 24), ("native-replacement", order, 14), ("source-replacement", nativeAndSource, 3.5),
      ("captured-native", captured, 3), ("nested-evaluation", nested, 123) })
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module module = Compiler.Compile(source, name + ".msync", level);
        Check(!module.Functions.SelectMany(function => function.Instructions).Any(instruction =>
            instruction.Text?.StartsWith("@msync-", StringComparison.Ordinal) == true), name + " leaked compiler marker");
        if (name == "public-buffer" && level >= 2)
        {
            var main = module.Functions[module.EntryPoint];
            Check(main.Instructions.Count(instruction => instruction.Op == OpCode.NativeCall && instruction.Text == "storage.buffer_get") == 1,
                "Public Buffer get did not reach indexed read reuse");
            Check(main.Instructions.Count(instruction => instruction.Op == OpCode.NativeCall && instruction.Text == "storage.buffer_size") == 1,
                "Public Buffer size did not reach length reuse");
            Check(!main.Instructions.Any(instruction => instruction.Op == OpCode.Call), "Buffer retained a source method frame");
        }
        foreach (Module image in Images(module))
        {
            Value result = await Interpreter.ExecuteAsync(image);
            Check(result is NumberValue value && value.Data == expected, $"{name} O{level}: expected {expected}, got {result}");
        }
    }
}

const string invalid = """
    let buffer = $std::storage::Buffer::new([1]);
    $std::storage::Buffer::get(buffer, 9)
    """;
foreach (int level in new[] { 0, 2, 3 })
{
    Module module = Compiler.Compile(invalid, "native-failure.msync", level);
    foreach (Module image in Images(module))
    {
        try { await Interpreter.ExecuteAsync(image); throw new InvalidOperationException("Missing native bounds failure"); }
        catch (MsyncException error)
        {
            Check(error.Span?.Source == "native-failure.msync" && error.Span?.Start == invalid.IndexOf("$std::storage::Buffer::get", StringComparison.Ordinal),
                $"O{level}: native error lost actual call site: {error.Span}");
        }
    }
}

CompilerSession compiler = new();
await using ExecutionSession execution = new();
async ValueTask<Value> Submit(string source)
{
    Module module = compiler.Compile(source);
    Value result = await execution.ExecuteAsync(module);
    compiler.AcceptSubmission(execution.GetLocalValue);
    return result;
}
await Submit("let selected = $std::math::abs; fn positive(value: Number) { selected(value) }; positive(-2)");
Check(await Submit("selected = $std::math::ceil; positive(-1.5)") is NumberValue { Data: -1 }, "REPL native replacement did not relink");
Check(await Submit("selected = fn(value: Number) { value - 10 }; positive(-1.5)") is NumberValue { Data: -11.5 }, "REPL source replacement did not relink");
Console.WriteLine($"PASS {checks} native call specialization contracts");

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
IEnumerable<Module> Images(Module module)
{
    yield return module;
    yield return RoundTrip(module);
    yield return RoundTrip(Binding.Bind(module, context));
}
Module RoundTrip(Module module)
{
    using MemoryStream bytes = new(); Artifact.Write(module, bytes); bytes.Position = 0;
    return Artifact.Read(bytes);
}
