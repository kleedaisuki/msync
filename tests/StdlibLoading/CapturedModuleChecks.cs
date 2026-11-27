using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;
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
                root::containers = object { List: object { size: fn(self) { 99 } } };
                if before == 2 { read(list) } else { 0 }
                """, 99),
            ("replace-module-family", Read + List + """
                let before = read(list);
                let module = $std::containers;
                module::List = object { size: fn(self) { 77 } };
                if before == 2 { read(list) } else { 0 }
                """, 77),
            ("capture-materialized-root", "let root = $std; " + Read + List + """
                let before = read(list);
                root::containers = object { List: object { size: fn(self) { 99 } } };
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
                module::List = object { size: fn(self) { 77 } };
                if before == 2 { old::size(list) + read(list) } else { 0 }
                """, 79),
            ("callee-before-mutating-argument", Read + List + """
                let module = $std::containers;
                fn change(self) {
                    module::List = object { size: fn(self) { 99 } };
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
                module::List = object { size: fn(self) { 77 } };
                if before == 2 { read(list) } else { 0 }
                """, 77),
            ("lexical-std-shadow", """
                let std = object { containers: object { List: object {
                    size: fn(self) { 2 }
                } } };
                fn read(self) { $std::containers::List::size(self) };
                let before = read(0);
                std::containers = object { List: object { size: fn(self) { 99 } } };
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
                NativeProgram portable = Compiler.Compile(source, name, level);
                foreach (NativeProgram image in Images(portable))
                {
                    Console.WriteLine($"Captured modules: {name} O{level} {image.Stage}");
                    NativeValue value = await StdlibFixture.Execute(image);
                    Check(value, expected, $"{name} O{level} {image.Stage}");
                    checks++;
                }
            }
            if (selected is null or "session")
            {
                Console.WriteLine($"Captured modules: session O{level}");
                await Session(level);
                await RootPublication(level);
                checks += 7;
            }
        }
        Console.WriteLine($"PASS {checks} captured standard-module identity, replacement, alias and staged/session contracts");
    }

    public static async Task RunQueries()
    {
        const string prefix = """
            let Shape = decltype({
                let root = $std;
                root::bool = object { identity: fn(value: Bool) { not value } };
                object { value: root::bool::identity(false) }
            });
            """;
        const string valid = prefix + "let value: Shape = object { value: true }; if value::value and not $std::bool::identity(false) { 7 } else { 0 }";
        const string invalid = prefix + "let value: Shape = object { other: true }; value";
        const string publisher = "let root = $std; root::bool = object { identity: fn(value: Bool) { not value } }; if root::bool::identity(false) { 7 } else { 0 }";
        int routes = 0;
        foreach (int level in new[] { 0, 2 })
        {
            CompilerSession editor = new(optimization: level);
            if (editor.AnalyzeEditor(valid).Diagnostic is not null)
                throw new InvalidOperationException("A query imposed its current module schema as a storage ABI");
            var rejected = editor.AnalyzeEditor(invalid).Diagnostic;
            if (rejected is not { Code: "E0205" } || !rejected.Message.Contains("'value'", StringComparison.Ordinal))
                throw new InvalidOperationException("A query lost the returned Object's actual required field");
            if (editor.BindingNames.Count != 0)
                throw new InvalidOperationException("A module query published candidate bindings into the accepted editor session");
            HirModule hir = Compiler.Analyze(valid, "query-module-storage.msync");
            using MemoryStream high = new();
            HirArtifact.Write(hir with { Sources = [.. hir.Sources.Select(source => source with { Text = null })] }, high);
            high.Position = 0;
            NativeProgram[] inputs =
            [
                Compiler.LowerInstructions(hir, level),
                Compiler.LowerInstructions(HirArtifact.Read(high), level),
                Compiler.Compile(publisher, "published-module-storage.msync", level)
            ];
            foreach (NativeProgram image in inputs.SelectMany(Images))
            {
                Check(await StdlibFixture.Execute(image), 7, $"query/publisher module storage O{level} {image.Stage}");
                routes++;
            }
        }
        Console.WriteLine($"PASS {routes} query/decltype module replacement native routes and editor schema controls");
    }

    private static async Task Session(int level)
    {
        CompilerSession compiler = new(optimization: level);
        NativeExecutionSession runtime = new();
        async Task<NativeValue> Submit(string source)
        {
            NativeValue value = await runtime.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(runtime.GetLocalValue);
            return value;
        }
        await Submit(Read + List);
        Check(await Submit("read(list)"), 2, $"session initial O{level}");
        await Submit("let family = $std::containers::List; let root = $std; root::containers = object { List: object { size: fn(self) { 99 } } };");
        Check(await Submit("read(list)"), 99, $"session replacement O{level}");
        Check(await Submit("family::size(list)"), 2, $"session retained alias O{level}");
    }

    private static async Task RootPublication(int level)
    {
        CompilerSession compiler = new(optimization: level);
        await using NativeExecutionSession runtime = new();
        async Task<NativeValue> Submit(string source)
        {
            NativeValue value = await runtime.ExecuteAsync(compiler.Compile(source));
            compiler.AcceptSubmission(runtime.GetLocalValue);
            return value;
        }
        Check(await Submit("let boolean = $std::bool; fn read() { $std::bool::identity(false) }; if read() { 1 } else { 0 }"),
            0, $"module capture before root publication O{level}");
        NativeValue root = await Submit("let root = $std; root");
        if (root.AsAddress().Allocation.Layout.Fields.Any(field => field.Name.StartsWith('_')))
            throw new InvalidOperationException("Public root exposed a private helper module");
        Check(await Submit("if root::bool::identity(false) or read() { 1 } else { 0 }"),
            0, $"root publication retained canonical initialized cells O{level}");
        Check(await Submit("root::bool::identity = fn(value: Bool) { not value }; (if read() { 10 } else { 0 }) + (if boolean::identity(false) { 1 } else { 0 })"),
            11, $"root member mutation retained shared namespace identity O{level}");
        Check(await Submit("root::bool = object { identity: fn(value: Bool) { false } }; (if read() { 10 } else { 0 }) + (if boolean::identity(false) { 1 } else { 0 })"),
            1, $"module replacement updated the canonical cell without moving retained aliases O{level}");
    }

    private static void Check(NativeValue value, double expected, string context)
    {
        if (value.Kind != NativeKind.Number || value.AsNumber() != expected)
            throw new InvalidOperationException($"{context}: expected {expected}, got {value}");
    }

    private static IEnumerable<NativeProgram> Images(NativeProgram module)
    {
        yield return module;
        yield return Codec(module);
        yield return Codec(NativePreparation.Prepare(module, new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory)));
    }

    private static NativeProgram Codec(NativeProgram module)
    {
        using MemoryStream bytes = new();
        NativeArtifact.Write(module, bytes);
        bytes.Position = 0;
        return NativeArtifact.Read(bytes);
    }
}
