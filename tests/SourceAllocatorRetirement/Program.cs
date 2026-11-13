using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

int checks = 0;
foreach (string module in new[] { "storage", "arena", "slots" })
{
    Check(!NativeSignatures.All.Any(signature => signature.Name.StartsWith(module + ".", StringComparison.Ordinal)),
        $"Retired {module} native registrations remain public");
    Check(!typeof(Compiler).Assembly.GetManifestResourceNames().Any(name => name.EndsWith("." + module + ".msync", StringComparison.Ordinal)),
        $"Retired {module} source remains embedded in the standard catalog");
    Reject("$std::" + module, module);
}
Reject("let value: Storage = null;", "Storage");
Reject("native fn abs(value: Storage) -> Number;", "Storage");
foreach (int level in new[] { 0, 2 })
{
    foreach ((string name, string source, double expected) in new[]
    {
        ("ordinary-container", "let list = $std::containers::List::new([1, 2]); $std::containers::List::push(list, 3); $std::containers::List::size(list)", 3d),
        ("ordinary-witness-name", "let Storage = Number; let value: Storage = 42; value", 42d)
    })
    {
        NativeProgram program = Compiler.CompileNative(source, name + ".msync", level);
        Check(program.Types.All(type => (type.Kind & TypeKind.Storage) == 0), name + ": source created historical Storage type");
        using MemoryStream artifact = new();
        NativeArtifact.WriteProduced(program, artifact); artifact.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(artifact));
        NativeValue actual = await interpreter.ExecuteAsync();
        Check(actual.Kind == NativeKind.Number && actual.AsNumber() == expected, name + ": native source journey changed");
    }
}
Console.WriteLine($"PASS {checks} source allocator retirement contracts");

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void Reject(string source, string context)
{
    try { _ = Compiler.CompileNative(source, "retired-allocator.msync"); }
    catch (CompileException error)
    {
        Check(error.Message.Contains(context, StringComparison.Ordinal), "Retirement diagnostic omitted the original source name: " + error.Message);
        return;
    }
    throw new InvalidOperationException("Retired allocator source was accepted: " + source);
}
