using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
await Session();
await Source("native fn first(values: Range) -> String; let text = \"before\"; fn once(value: String) { [value] }; let delayed = once(text); text = \"after\"; first(delayed)", "after");
await Source("native fn first(values: Range) -> String; native fn materialize(values: Range) -> Range; let text = \"before\"; fn eager(value: String) { materialize([value]) }; let saved = eager(text); text = \"after\"; first(saved)", "before");
await Source("native fn first(values: Range) -> String; let text = \"before\"; fn once(value: String) { let items = [value]; items }; let delayed = once(text); text = \"after\"; first(delayed)", "after");
await Source("native fn first(values: Range) -> String; let text = \"before\"; fn provider() { fn(value: String) { [value] } }; let delayed = provider()(text); text = \"after\"; first(delayed)", "after");
await Source("native fn first(values: Range) -> String; let text = \"before\"; fn provider(value: String) { fn { [value] } }; let delayed = provider(text)(); text = \"after\"; first(delayed)", "after");
await Source("native fn first(values: Range) -> String; let text = \"before\"; fn once(value: String) { [value] }; fn provider(operation) { fn(value: String) { operation(value) } }; let delayed = provider(once)(text); text = \"after\"; first(delayed)", "after");
await Source("native fn first(values: Range) -> String; native fn materialize(values: Range) -> Range; let text = \"before\"; fn snapshot(value: String) { materialize([value]) }; fn provider(operation) { fn(value: String) { operation(value) } }; let saved = provider(snapshot)(text); text = \"after\"; first(saved)", "before");
await Source("native fn first(values: Range) -> String; let selected = true; fn choose(value: Bool) { if value { [\"yes\"] } else { [\"no\"] } }; let delayed = choose(selected); selected = false; first(delayed)", "no");
await Source("native fn first(values: Range) -> String; let flag = true; let delayed = [if flag { \"yes\" } else { \"no\" }]; flag = false; first(delayed)", "no");
await Source("native fn first(values: Range) -> String; let state = scope { flag: true }; let delayed = [if state::flag { \"yes\" } else { \"no\" }]; state::flag = false; first(delayed)", "no");
await Source("native fn first(values: Range) -> String; let state = scope { flag: true }; fn build() { if state::flag { [\"yes\"] } else { [\"no\"] } }; let delayed = build(); state::flag = false; first(delayed)", "no");
await Source("native fn first(values: Range) -> String; let value?: String = \"before\"; let delayed = [if value is not null { value } else { \"fallback\" }]; value = null; first(delayed)", "fallback");
Reject("native fn first(values: Range) -> String; let input: Range | String = [\"before\"]; fn identity(value) { value }; let delayed = identity(input); input = \"after\"; first(delayed)");
HirModule callbacks = Compiler.Analyze("fn use(operations) { operations::get() }; let first = scope { get: fn { 1 } }; let second = scope { get: fn { 2 } }; use(first) * 10 + use(second)", "callable-shape.msync");
Check(await Interpreter.ExecuteAsync(HirLowerer.Lower(callbacks, new(0)).Module) is NumberValue { Data: 12 },
    "A pure unknown scheme cannot erase a specialized callable's result type.");
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "hir-call-input-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string before = Path.Combine(root, "before.txt"), after = Path.Combine(root, "after.txt");
    await File.WriteAllTextAsync(before, "before\n");
    await File.WriteAllTextAsync(after, "after\n");
    string prefix = $"let file = {StringLiteral(before)};";
    string suffix = $"file = {StringLiteral(after)}; $std::ranges::first(saved)";
    await Source(prefix + "let saved = $std::io::lines(file);" + suffix, "after");
    await Source($"let file: Path | String | Range = {StringLiteral(before)};" + "fn lines(value: Path | String | Range) { if value is Range { value } else { $std::io::read_lines(value) } }; let saved = lines(file);" + suffix, "after");
    await Source(prefix + "let saved = $std::ranges::cache($std::io::lines(file));" + suffix, "before");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS {checks} call evaluation policy journeys");

async Task Source(string source, string expected)
{
    HirModule module = Compiler.Analyze(source, "call-policy.msync");
    using MemoryStream stream = new();
    HirArtifact.Write(module, stream);
    stream.Position = 0;
    Module lowered = HirLowerer.Lower(HirArtifact.Read(stream), new(0)).Module;
    Value actual = await Interpreter.ExecuteAsync(lowered);
    Check(actual is StringValue text && text.Data == expected, $"Expected {expected}, got {actual}");
}

async Task Session()
{
    CompilerSession compiler = new(optimization: 0);
    await using ExecutionSession runtime = new();
    async Task<Value> Submit(string source)
    {
        Module module = compiler.Compile(source);
        Value value = await runtime.ExecuteAsync(module);
        compiler.AcceptSubmission(runtime.GetLocalValue);
        return value;
    }
    await Submit("let text = \"before\"; fn once(value: String) { [value] }; let delayed = once(text);");
    await Submit("text = \"after\";");
    Value actual = await Submit("$std::ranges::first(delayed)");
    Check(actual is StringValue text && text.Data == "after", $"REPL deferred argument: {actual}");
}

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

static string StringLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

void Reject(string source)
{
    try { _ = Compiler.Analyze(source, "call-domain.msync"); }
    catch (CompileException error) when (error.Diagnostic.Code == "E0203")
    {
        checks++;
        return;
    }
    throw new InvalidOperationException("A writable Range | String argument cannot promise a future Range.");
}
