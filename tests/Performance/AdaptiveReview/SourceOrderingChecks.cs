using Msync.Core;
using Msync.Runtime;
using Compiler = Msync.Compiler.Compiler;
using Module = Msync.Core.Module;

internal static class SourceOrderingChecks
{
    internal static async Task Run()
    {
        const string source = """
            let action = fn(value: Bool) { "A" };
            let replacement = fn(value: Bool) { "B" };
            fn change() { action = replacement; true };
            let first = action(change());
            let second = action(false);
            $std::string::concat(first, second)
            """;
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "adaptive-independent-review", "source-order");
        Directory.CreateDirectory(root);
        for (int level = 0; level <= 3; level++)
        {
            Module module = Compiler.Compile(source, "source-order-review", level);
            await File.WriteAllTextAsync(Path.Combine(root, $"O{level}.ir"), Dump(module));
            Value relocated = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root));
            Module executable = Binding.Bind(module, new(root, root));
            Value bound = await Interpreter.ExecuteAsync(executable, new(WorkingDirectory: root));
            using MemoryStream stream = new();
            Artifact.Write(executable, stream); stream.Position = 0;
            Value codec = await Interpreter.ExecuteAsync(Artifact.Read(stream), new(WorkingDirectory: root));
            Console.WriteLine($"O{level}: relocatable={relocated}, bound={bound}, codec={codec}; expected=AB");
        }
    }

    private static string Dump(Module module) => string.Join('\n', module.Functions.Select((function, index) =>
        $"function {index}: {function.Name} captures={function.CaptureCount} args={function.ParameterCount}\n"
        + string.Join('\n', function.Instructions.Select((instruction, pc) =>
            $"  {pc}: {instruction.Op} {instruction.A} {instruction.B} {instruction.Text}"))));
}
