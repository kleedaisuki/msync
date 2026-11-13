using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using Msync.Runtime;


if (args.Contains("--query-modules", StringComparer.Ordinal))
{
    await CapturedModuleChecks.RunQueries();
    return;
}

if (args.Contains("--captured-modules", StringComparer.Ordinal))
{
    await CapturedModuleChecks.Run(args);
    return;
}

if (args.Contains("--demand-loading", StringComparer.Ordinal))
{
    int selected = Array.IndexOf(args, "--case");
    await DemandLoadingChecks.Run(caseName: selected < 0 ? null : args[selected + 1]);
    return;
}

await CapturedModuleChecks.RunQueries();

int assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
}

var raw = Compiler.Compile("$std::bool::identity(true)", "module-selection", 0);
Check(raw.Functions.Count < 12, $"A Bool reference eagerly instantiated unrelated modules ({raw.Functions.Count} functions)");
Check(raw.Functions.SelectMany(function => function.Instructions).Where(instruction => instruction.Op is NativeOp.MakeFunction or NativeOp.CallNative).All(instruction => instruction.Symbol is null || instruction.Symbol.StartsWith("bool.", StringComparison.Ordinal)),
    "A Bool reference populated unrelated native capabilities");
foreach ((string name, string source, string expected) in new[]
{
    ("bool", "$std::bool::identity(true)", "true"),
    ("cross-module", "$std::path::filename(./folder/input.txt)", "input.txt"),
    ("explicit-cast", "42 as String", "42"),
    ("root-module-replacement", "fn read() { $std::bool::identity(false) }; let root = $std; root::bool = scope { identity: fn(value: Bool) { not value } }; read()", "true"),
    ("module-member-replacement", "let boolean = $std::bool; let root = $std; boolean::identity = fn(value: Bool) { not value }; root::bool::identity(false)", "true"),
    ("root-before-function", "let root = $std; fn read() { $std::bool::identity(false) }; root::bool = scope { identity: fn(value: Bool) { not value } }; read()", "true"),
    ("user-shadow", "let std = scope { bool: scope { identity: fn(value: Bool) { not value } } }; $std::bool::identity(false)", "true"),
    ("shadow-after-definition", "fn read() { $std::bool::identity(true) }; let std = scope { bool: scope { identity: fn(value: Bool) { not value } } }; read()", "true"),
    ("functional", "$std::functional::compose(fn(value: Number) { value * 2 }, fn(value: Number) { value + 1 })(20)", "42")
})
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        NativeProgram portable = Compiler.Compile(source, name, level);
        Console.WriteLine($"Module loading: {name} O{level}");
        foreach (bool bound in new[] { false, true })
        {
            NativeProgram program = bound ? NativePreparation.Prepare(portable, new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory)) : portable;
            using MemoryStream data = new();
            try { NativeArtifact.Write(program, data); }
            catch
            {
                string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "stdlib-loading-failures");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"{name}-O{level}-{bound}.json"), NativeArtifact.ToJson(program));
                throw;
            }
            data.Position = 0;
            NativeValue result = await StdlibFixture.Execute(NativeArtifact.Read(data));
            Check(result.ToString() == expected, $"{name} O{level} bound={bound}: expected '{expected}', got '{result}'");
        }
    }
}
Console.WriteLine($"PASS: {assertions} selective module, shared cell, root mutation and binary-stage contracts");
