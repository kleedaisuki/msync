using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using Msync.Runtime;
using Compiler = Msync.Compiler.Compiler;


internal static class DemandLoadingChecks
{
    public static async Task Run(bool requireSelection = true, string? caseName = null)
    {
        const string listOnly = "let list = $std::containers::List::new([1, 2]); $std::containers::List::size(list)";
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
        foreach (int level in new[] { 0, 2, 3 })
        {
            Console.WriteLine($"Demand loading: {name} O{level}");
            NativeProgram portable = Compiler.Compile(source, name, level);
            foreach (NativeProgram program in Routes(portable))
            {
                if (requireSelection && name == "selected-list" && level == 0)
                    CheckSelectedFamilies(program);
                NativeValue result = await StdlibFixture.Execute(program);
                if (result.ToString() != expected)
                    throw new InvalidOperationException($"{name} O{level} {program.Stage}: expected {expected}, got {result}");
                checks++;
            }
        }
        Console.WriteLine($"PASS {checks} demand-loaded family, dependency, mutable alias and source/mso/mexe journeys");
    }

    private static void CheckSelectedFamilies(NativeProgram module)
    {
        var assembly = typeof(Compiler).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".containers.msync", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream);
        string source = reader.ReadToEnd();
        var declarations = Parser.Parse(source, "msync-stdlib:///0.8.0/containers.msync").Statements.OfType<ScopeDeclaration>().ToArray();
        var instructions = module.Functions.SelectMany(function => function.Instructions).ToArray();
        var operations = instructions
            .Where(instruction => instruction.Op is NativeOp.MakeFunction or NativeOp.Allocate or NativeOp.PublishStatic)
            .Select(instruction => instruction.Source).OfType<Msync.Core.SourceSpan>()
            .Where(span => span.Source == "msync-stdlib:///0.8.0/containers.msync").ToArray();
        foreach (var declaration in declarations)
        {
            bool initialized = operations.Any(span => span.Start >= declaration.Span.Start && span.Start < declaration.Span.End);
            bool selected = declaration.Name is "List" or "SnapshotStorage";
            if (initialized != selected)
                throw new InvalidOperationException($"List-only selection {(selected ? "lost" : "eagerly initialized")} family {declaration.Name}");
            if (selected && !instructions.Any(instruction => instruction is { Op: NativeOp.PublishStatic, Source: { } span }
                && span.Source == "msync-stdlib:///0.8.0/containers.msync"
                && span.Start == declaration.Span.Start && span.Length > 0))
                throw new InvalidOperationException($"Selected family {declaration.Name} lost the source span of its typed static publication");
        }
    }

    private static IEnumerable<NativeProgram> Routes(NativeProgram portable)
    {
        yield return portable;
        yield return Codec(portable);
        yield return Codec(NativePreparation.Prepare(portable, new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory)));
    }

    private static NativeProgram Codec(NativeProgram program)
    {
        using MemoryStream stream = new();
        NativeArtifact.Write(program, stream);
        stream.Position = 0;
        return NativeArtifact.Read(stream);
    }
}
