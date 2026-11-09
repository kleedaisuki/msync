using Msync.Optimization;
using Msync.Core;
using Msync.Runtime;

internal static class TypeValueChecks
{
    internal static async Task Run()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "type-value-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int assertions = 0;
        const string source = "let target = if exists ./flag { Number } else { String }; if target is Number { $std::cast(\"41\", target) == 41 } else { target is String }";
        foreach (bool present in new[] { false, true })
        {
            string flag = Path.Combine(root, "flag");
            if (present) File.WriteAllText(flag, "present");
            foreach (int level in new[] { 0, 2, 3 })
            {
                var module = Msync.Compiler.Compiler.Compile(source, "type-value-guard.msync", level);
                var bound = Binding.Bind(module, new(root, root));
                foreach (var code in new[] { module, bound })
                {
                    if (await Interpreter.ExecuteAsync(code, new(WorkingDirectory: root)) is not BoolValue { Data: true })
                        throw new InvalidOperationException($"TypeValue guard failed at O{level}, present={present}");
                    assertions++;
                    using MemoryStream stream = new();
                    Artifact.Write(code, stream); stream.Position = 0;
                    if (await Interpreter.ExecuteAsync(Artifact.Read(stream), new(WorkingDirectory: root)) is not BoolValue { Data: true })
                        throw new InvalidOperationException($"TypeValue guard codec failed at O{level}, present={present}");
                    assertions++;
                }
            }
        }
        Console.WriteLine($"TypeValue unknown-denotation guards and typed branch casts: {assertions} assertions passed");
    }
}
