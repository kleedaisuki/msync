using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class CapturedModuleChecks
{
    private const string Read = "fn read(self) { $std::containers::List::size(self) }; ";
    private const string List = "let list = $std::containers::List::new([1, 2]); ";

    public static async Task Run(string[] arguments)
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("replace-root-module", Read + List + """
                let before = read(list);
                let root = $std;
                root::containers = scope { List: scope { size: fn(self) { 99 } } };
                if before == 2 { read(list) } else { 0 }
                """, 99),
            ("replace-module-family", Read + List + """
                let before = read(list);
                let module = $std::containers;
                module::List = scope { size: fn(self) { 77 } };
                if before == 2 { read(list) } else { 0 }
                """, 77),
            ("capture-materialized-root", "let root = $std; " + Read + List + """
                let before = read(list);
                root::containers = scope { List: scope { size: fn(self) { 99 } } };
                if before == 2 { read(list) } else { 0 }
                """, 99),
            ("root-materialization-control", Read + List + """
                let before = read(list);
                let root = $std;
                before + read(list)
                """, 4),
            ("retained-family-alias", Read + List + """
                let old = $std::containers::List;
                let before = read(list);
                let module = $std::containers;
                module::List = scope { size: fn(self) { 77 } };
                if before == 2 { old::size(list) + read(list) } else { 0 }
                """, 79),
            ("callee-before-mutating-argument", Read + List + """
                let module = $std::containers;
                fn change(self) {
                    module::List = scope { size: fn(self) { 99 } };
                    self
                };
                let before = $std::containers::List::size(change(list));
                before * 100 + read(list)
                """, 299),
            ("nested-captured-module", """
                fn factory() { fn(self) { $std::containers::List::size(self) } };
                let read = factory();
                """ + List + """
                let before = read(list);
                let module = $std::containers;
                module::List = scope { size: fn(self) { 77 } };
                if before == 2 { read(list) } else { 0 }
                """, 77),
            ("lexical-std-shadow", """
                let std = scope { containers: scope { List: scope {
                    size: fn(self) { 2 }
                } } };
                fn read(self) { $std::containers::List::size(self) };
                let before = read(0);
                std::containers = scope { List: scope { size: fn(self) { 99 } } };
                before * 100 + read(0)
                """, 299)
        ];
        int checks = 0;
        int caseIndex = Array.IndexOf(arguments, "--case");
        string? selected = caseIndex >= 0 ? arguments[caseIndex + 1] : null;
        foreach (int level in new[] { 0, 2, 3 })
        {
            foreach (var (name, source, expected) in cases)
            {
                if (selected is not null && selected != name) continue;
                Console.WriteLine($"Captured modules: {name} O{level} compile");
                Module portable = Compiler.Compile(source, name, level);
                foreach (Module image in Images(portable))
                {
                    Console.WriteLine($"Captured modules: {name} O{level} {image.Stage}");
                    Value value = await Interpreter.ExecuteAsync(image);
                    Check(value, expected, $"{name} O{level} {image.Stage}");
                    checks++;
                }
            }
            if (selected is null or "session")
            {
                Console.WriteLine($"Captured modules: session O{level}");
                await Session(level);
                checks += 3;
            }
        }
        Console.WriteLine($"PASS {checks} captured standard-module identity, replacement, alias and staged/session contracts");
    }

    private static async Task Session(int level)
    {
        CompilerSession compiler = new(optimization: level);
        ExecutionSession runtime = new();
        async Task<Value> Submit(string source)
        {
            Value value = await runtime.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(runtime.GetLocalValue);
            return value;
        }
        await Submit(Read + List);
        Check(await Submit("read(list)"), 2, $"session initial O{level}");
        await Submit("let family = $std::containers::List; let root = $std; root::containers = scope { List: scope { size: fn(self) { 99 } } };");
        Check(await Submit("read(list)"), 99, $"session replacement O{level}");
        Check(await Submit("family::size(list)"), 2, $"session retained alias O{level}");
    }

    private static void Check(Value value, double expected, string context)
    {
        if (value is not NumberValue number || number.Data != expected)
            throw new InvalidOperationException($"{context}: expected {expected}, got {value}");
    }

    private static IEnumerable<Module> Images(Module module)
    {
        yield return module;
        yield return Codec(module);
        yield return Codec(Binding.Bind(module, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
    }

    private static Module Codec(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }
}
