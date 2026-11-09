using Msync.Optimization;
using System.Runtime.CompilerServices;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.Contains("--type-values", StringComparer.Ordinal)) { await TypeValueChecks.Run(); return; }

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "for-control-tests");
Directory.CreateDirectory(root);
foreach (int level in new[] { 0, 2, 3 })
{
    foreach (var (source, expected) in new[]
    {
        ("let count = 0; let stopped: Null = for item in [1, 2, 3] { count = count + 1; break; }; count == 1 and stopped is null", "true"),
        ("let count = 0; for outer in [1, 2] { loop { for inner in [1, 2] { count = count + 1; break; }; break; }; }; count == 2", "true"),
        ("loop { for item in [1, 2] { break; }; break \"loop value\"; }", "loop value"),
        ("let seen = 0; let values = $std::ranges::transform([1, 2, 3], fn(value) { seen = seen + 1; value }); let (first, second) = values; seen == 2 and first == 1 and second == 2", "true"),
        ("let (first, second) = $std::ranges::repeat(7, 1000000000000); first == 7 and second == 7", "true"),
        ("fn first() { for item in [\"first\", \"second\"] { return item; }; \"empty\" }; first()", "first"),
        ("let file? = ./input; if file is not null { loop { file = null; break; }; }; file is null", "true"),
        ("acquire { for item in [1, 2] { acquire { break; }; }; \"closed\" }", "closed")
    })
    {
        var module = Msync.Compiler.Compiler.Compile(source, "for-control.msync", level);
        foreach (var code in new[] { module, Binding.Bind(module, new(root, root)) })
        {
            Check((await Interpreter.ExecuteAsync(code)).ToString() == expected, source);
            using MemoryStream stream = new();
            Artifact.Write(code, stream); stream.Position = 0;
            Check((await Interpreter.ExecuteAsync(Artifact.Read(stream))).ToString() == expected, "codec: " + source);
        }
    }
    foreach (string source in new[]
    {
        "for item in [1] { break 7; };",
        "let file? = ./input; if file is not null { for item in [1] { if exists ./input { file = null; break; }; }; file -> ./output; };"
    })
    {
        try { _ = Msync.Compiler.Compiler.Compile(source, "for-control-invalid.msync", level); Check(false, "invalid break state accepted"); }
        catch (CompileException error) { Check(error.Diagnostic.Code is "E0207" or "E0203", error.Message); }
    }
}

bool disposed = false;
async IAsyncEnumerable<Value> Infinite([EnumeratorCancellation] CancellationToken token = default)
{
    try { while (true) { token.ThrowIfCancellationRequested(); yield return new NumberValue(1); await Task.Yield(); } }
    finally { disposed = true; }
}
async IAsyncEnumerable<Value> Gate()
{
    Check(disposed, "the next loop ran before the broken iterator was disposed");
    yield return new NumberValue(2);
    await Task.CompletedTask;
}
var lifetime = Msync.Compiler.Compiler.Compile("for item in [1] { break; }; for item in [2] { }; true", "iterator-lifetime.msync", 0);
var constants = lifetime.Constants.ToList();
int firstIndex = constants.Count; constants.Add(new RangeValue(Infinite(), TypeRef.Number));
int secondIndex = constants.Count; constants.Add(new RangeValue(Gate(), TypeRef.Number));
int replacement = 0;
var codeList = lifetime.Functions[0].Instructions.ToArray();
for (int index = 1; index < codeList.Length; index++)
    if (codeList[index] is { Op: OpCode.MakeRange, A: 1 })
    {
        codeList[index - 1] = new(OpCode.Constant, replacement++ == 0 ? firstIndex : secondIndex);
        codeList[index] = new(OpCode.Nop);
    }
lifetime = lifetime with { Constants = constants, Functions = [lifetime.Functions[0] with { Instructions = codeList }] };
Check(await Interpreter.ExecuteAsync(lifetime) is BoolValue { Data: true } && disposed, "iterator lifetime gate failed");
Console.WriteLine($"for control, bounded destructuring and iterator lifetime: {assertions} assertions passed");
