using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

const string deepSource = "fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; sum(100000, 0)";
if (args.Contains("--dump", StringComparer.Ordinal))
{
    string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "native-tail-dump");
    Directory.CreateDirectory(directory);
    foreach (int level in new[] { 0, 1, 2, 3 })
    {
        NativeProgram program = Compiler.Compile(deepSource, "deep-tail.msync", level);
        File.WriteAllText(Path.Combine(directory, $"deep-O{level}.json"), NativeArtifact.ToJson(program));
        using FileStream output = File.Create(Path.Combine(directory, $"deep-O{level}.mso"));
        NativeArtifact.Write(program, output);
    }
    Console.WriteLine(directory);
    return;
}

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task<NativeValue> Run(NativeProgram program, bool detached, string? cwd = null)
{
    NativeArtifact.Validate(program);
    if (detached)
    {
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        program = NativeArtifact.Read(bytes);
    }
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
    await using NativeInterpreter interpreter = new(program, new ExecutionOptions(WorkingDirectory: cwd));
    return await interpreter.ExecuteAsync(timeout.Token);
}

(string Name, string Source, NativeValue Expected)[] cases =
[
    ("sum", "fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; sum(80, 0)", NativeValue.FromNumber(3240)),
    ("parallel-arguments", "fn swap(n, left, right) { if n == 0 { left } else { swap(n - 1, right, left) } }; swap(11, \"left\", \"right\")", NativeValue.FromString("right")),
    ("capture-write", "let count=0; fn walk(n) { count = count + 1; if n==0 {count} else {walk(n - 1)} }; walk(20)", NativeValue.FromNumber(21)),
    ("actual-callee-environment", "fn factory(value) { fn read(n) { value + n }; read }; let first = factory(10); let second = factory(20); fn invoke(callable, n) { callable(n) }; invoke(first, 1) + invoke(second, 1)", NativeValue.FromNumber(32)),
    ("fresh-escaped-location", "let first=fn {0}; fn walk(n) { let snapshot=n; if n==3 {first=fn {snapshot};}; if n==0 {null} else {walk(n - 1)} }; walk(3); first()", NativeValue.FromNumber(3))
];
foreach (var item in cases)
    foreach (int level in new[] { 0, 1, 2, 3 })
    {
        Console.WriteLine($"Tail source: {item.Name} O{level}");
        NativeProgram program = Compiler.Compile(item.Source, item.Name + ".msync", level);
        foreach (bool detached in new[] { false, true })
            Check(await Run(program, detached) == item.Expected, $"{item.Name} O{level} detached={detached}");
    }

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "native-tail-effects-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (int level in new[] { 0, 1, 2, 3 })
        foreach (bool detached in new[] { false, true })
        {
            const string source = "fn write(n) { if n == 2 { \"first\" -> ./first; }; if n == 99 { \"never\" -> ./never; }; if n == 0 { \"last\" -> ./last; true } else { write(n - 1) } }; write(2)";
            NativeProgram program = Compiler.Compile(source, "selected-tail-effects.msync", level);
            Check((await Run(program, detached, root)).AsBool(), $"Selected effect result O{level} detached={detached}");
            Check(await File.ReadAllTextAsync(Path.Combine(root, "first")) == "first"
                && await File.ReadAllTextAsync(Path.Combine(root, "last")) == "last"
                && !File.Exists(Path.Combine(root, "never")), $"Selected effect lifetime O{level} detached={detached}");
            File.Delete(Path.Combine(root, "first"));
            File.Delete(Path.Combine(root, "last"));
        }
}
finally { Directory.Delete(root, recursive: true); }

foreach (int level in args.Contains("--small", StringComparer.Ordinal) ? Array.Empty<int>() : new[] { 2, 3 })
{

    Console.WriteLine($"Deep tail recursion O{level}: 100000 source calls");
    NativeProgram program = Compiler.Compile(deepSource, "deep-tail.msync", level);
    foreach (bool detached in new[] { false, true })
        Check((await Run(program, detached)).AsNumber() == 5_000_050_000d,
            $"100000-step tail recursion O{level} detached={detached}");
}
await NativeTailChecks.Run(Check);
Console.WriteLine($"PASS {checks} native tail-recursion source/codec, parallel argument and captured identity contracts");
