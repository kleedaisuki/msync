using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class SingletonChecks
{
    internal static async Task<int> Run(string directory)
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("named-shared", "fn make() { scope Shared { value: 1 }; Shared }; let first = make(); let second = make(); first::value = 7; second::value", 7),
            ("anonymous-fresh", "fn make() { scope { value: 1 } }; let first = make(); let second = make(); first::value = 7; second::value", 1),
            ("named-initializes-once", "let count = 0; fn make() { scope Shared { value: { count = count + 1; count } }; Shared }; make(); make(); count", 1),
            ("named-cross-call-memory", "fn make() { scope Shared { value: 1 }; Shared }; let first = make(); first::value = 3; let second = make(); second::value = second::value + 4; first::value", 7)
        ];
        int checks = 0;
        foreach (var (name, source, expected) in cases)
        {
            Module raw = Compiler.Compile(source, name, 0);
            for (int level = 0; level <= 3; level++)
            {
                Module optimized = Optimizer.Optimize(raw, level);
                foreach (Module module in new[] { optimized, Binding.Bind(optimized, new(directory, directory)) })
                {
                    using MemoryStream stream = new();
                    Artifact.Write(module, stream);
                    stream.Position = 0;
                    Module decoded = Artifact.Read(stream);
                    Value actual = await Interpreter.ExecuteAsync(decoded, new(WorkingDirectory: directory));
                    if (actual is not NumberValue number || number.Data != expected)
                        throw new InvalidOperationException($"{name} O{level} {module.Stage}: expected {expected}, found {actual}");
                    checks++;
                }
            }
        }
        return checks;
    }
}
