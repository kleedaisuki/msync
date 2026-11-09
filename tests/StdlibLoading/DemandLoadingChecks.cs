using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;
using Compiler = Msync.Compiler.Compiler;
using Module = Msync.Core.Module;

internal static class DemandLoadingChecks
{
    public static async Task Run(bool requireSelection = true, string? caseName = null)
    {
        const string listOnly = "let list = $std::containers::List::new([1, 2]); $std::containers::List::size(list)";
        if (requireSelection && caseName is null or "selected-list")
            CheckSelectedFamilies(Compiler.Compile(listOnly, "selected-list", 0));
        (string Name, string Source, string Expected)[] cases =
        [
            ("selected-list", listOnly, "2"),
            ("late-root-selection", """
                let family = $std::containers::List;
                let list = family::new([1, 2]);
                family::size = fn(self) { 88 };
                let root = $std;
                root::containers::List::size(list)
                """, "88"),
            ("late-root-module-replacement", """
                fn read(self) { $std::containers::List::size(self) };
                let list = $std::containers::List::new([1, 2]);
                let before = read(list);
                let root = $std;
                root::containers = scope { List: scope { size: fn(self) { 99 } } };
                if before == 2 { read(list) } else { 0 }
                """, "99"),
            ("late-module-family-replacement", """
                fn read(self) { $std::containers::List::size(self) };
                let list = $std::containers::List::new([1, 2]);
                let before = read(list);
                let module = $std::containers;
                module::List = scope { size: fn(self) { 77 } };
                if before == 2 { read(list) } else { 0 }
                """, "77"),
            ("retained-family-alias", """
                let family = $std::containers::List;
                let list = family::new([1, 2]);
                let module = $std::containers;
                module::List = scope { size: fn(self) { 77 } };
                family::size(list)
                """, "2"),
            ("shared-family-method", """
                let family = $std::containers::List;
                let list = family::new([1, 2]);
                family::size = fn(self) { 88 };
                $std::containers::List::size(list)
                """, "88"),
            ("declared-storage-dependency", """
                let storage = $std::containers::SnapshotStorage;
                storage::snapshot = fn(values: Range) {
                    $std::ranges::take(values, 1)
                };
                let list = $std::containers::List::new([1, 2]);
                $std::containers::List::size(list)
                """, "1"),
            ("later-unrelated-family", """
                let family = $std::containers::List;
                let list = family::new([1, 2]);
                family::size = fn(self) { 88 };
                let unrelated = $std::containers::Set;
                $std::containers::List::size(list)
                """, "88"),
        ];
        int checks = 0;
        foreach (var (name, source, expected) in cases.Where(item => caseName is null || item.Name == caseName))
        foreach (int level in source.Contains("let root = $std;", StringComparison.Ordinal)
            || source.Contains("let module = $std::containers;", StringComparison.Ordinal) ? new[] { 0 } : new[] { 0, 2, 3 })
        {
            Console.WriteLine($"Demand loading: {name} O{level}");
            Module portable = Compiler.Compile(source, name, level);
            foreach (Module program in Routes(portable))
            {
                Value result = await Interpreter.ExecuteAsync(program);
                if (result.ToString() != expected)
                    throw new InvalidOperationException($"{name} O{level} {program.Stage}: expected {expected}, got {result}");
                checks++;
            }
        }
        Console.WriteLine($"PASS {checks} demand-loaded family, dependency, mutable alias and source/mso/mexe journeys");
    }

    private static void CheckSelectedFamilies(Module module)
    {
        var assembly = typeof(Compiler).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".containers.msync", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        string source = reader.ReadToEnd();
        var declarations = Parser.Parse(source, "msync-stdlib:///0.8.0/containers.msync").Statements.OfType<ScopeDeclaration>().ToArray();
        var operations = module.Functions.SelectMany(function => function.Instructions)
            .Where(instruction => instruction.Op is OpCode.MakeClosure or OpCode.MakeScope or OpCode.SetSingleton)
            .Select(instruction => instruction.Span).OfType<Msync.Core.SourceSpan>()
            .Where(span => span.Source == "msync-stdlib:///0.8.0/containers.msync").ToArray();
        foreach (var declaration in declarations)
        {
            bool initialized = operations.Any(span => span.Start >= declaration.Span.Start && span.Start < declaration.Span.End);
            bool selected = declaration.Name is "List" or "SnapshotStorage";
            if (initialized != selected)
                throw new InvalidOperationException($"List-only selection {(selected ? "lost" : "eagerly initialized")} family {declaration.Name}");
        }
    }

    private static IEnumerable<Module> Routes(Module portable)
    {
        yield return portable;
        yield return Codec(portable);
        yield return Codec(Binding.Bind(portable, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
    }

    private static Module Codec(Module program)
    {
        using MemoryStream stream = new();
        Artifact.Write(program, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
