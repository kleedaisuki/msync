using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

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

int assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
}

var raw = Compiler.Compile("$std::bool::identity(true)", "module-selection", 0);
Check(raw.Functions.Count < 12, $"A Bool reference eagerly instantiated unrelated modules ({raw.Functions.Count} functions)");
Check(raw.Constants.OfType<FunctionValue>().All(value => value.NativeName is null || value.NativeName.StartsWith("bool.", StringComparison.Ordinal)),
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
        Module portable = Compiler.Compile(source, name, level);
        foreach (bool bound in new[] { false, true })
        {
            Module program = bound ? Binding.Bind(portable, new(Environment.CurrentDirectory, Environment.CurrentDirectory)) : portable;
            using MemoryStream data = new();
            Artifact.Write(program, data); data.Position = 0;
            Value result = await Interpreter.ExecuteAsync(Artifact.Read(data));
            Check(result.ToString() == expected, $"{name} O{level} bound={bound}: expected '{expected}', got '{result}'");
        }
    }
}
Console.WriteLine($"PASS: {assertions} selective module, shared cell, root mutation and binary-stage contracts");
