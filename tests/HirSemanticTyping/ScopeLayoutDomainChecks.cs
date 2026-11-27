using System.Collections.Immutable;
using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

internal static class ScopeLayoutDomainChecks
{
    internal static async Task Run()
    {
        const string divergent = """
            let flag = $std::io::read_line() is null;
            let selected = if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } };
            selected::key
            """;
        await Verify(divergent, [0, 1]);
        await Verify(divergent.Replace("alpha: 0, key: 11", "key: 11, yellow: 0", StringComparison.Ordinal), [0, 0]);
        const string returned = """
            fn choose(flag: Bool) {
                if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } }
            };
            let flag = $std::io::read_line() is null;
            let selected = choose(flag);
            selected::key
            """;
        await Verify(returned, [0, 1]);
        const string fromRange = """
            let flag = $std::io::read_line() is null;
            let item = if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } };
            let selected = $std::ranges::front([item]);
            selected::key
            """;
        await Verify(fromRange, [0, 1]);
        const string captured = """
            let flag = $std::io::read_line() is null;
            let selected = if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } };
            fn read { selected::key };
            read()
            """;
        await Verify(captured, [0, 1]);
        await VerifyStructuralStorage();
        VerifyOpaqueSource();
        Console.WriteLine("PASS actual Object layout domains across joins, returns, Range elements, captures, structural writes and source-free execution");
    }

    private static async Task Verify(string source, int[] expectedOrdinals)
    {
        HirModule hir = Compiler.Analyze(source, "scope-layout-domains");
        hir = hir with { Sources = hir.Sources.Select(item => item with { Text = null }).ToImmutableArray() };
        using MemoryStream stream = new();
        HirArtifact.Write(hir, stream);
        stream.Position = 0;
        HirModule imported = HirArtifact.Read(stream);
        var member = ClosedProjection(imported);
        var receiver = imported.Node(imported.Children(member)[0]);
        var type = imported.Types[receiver.Type.Value];
        var domain = type.ScopeDomain;
        Require(domain.Count == 2 && domain[0] < domain[1]
            && imported.ScopeLayouts[type.Shape].Fields is [{ Name: "key" }]
            && imported.ScopeLayouts[domain[0]].Fields.Length == 2
            && imported.ScopeLayouts[domain[1]].Fields.Length == 2
            && type.Shape != domain[0] && type.Shape != domain[1],
            "A structural intersection was mistaken for a constructor layout, or alternatives were lost.");

        Module portable = HirLowerer.Lower(imported, new(OptimizationLevel: 0)).Module;
        string cwd = Environment.CurrentDirectory;
        foreach (var image in new[] { portable, RoundTrip(portable), RoundTrip(Binding.Bind(portable, new(cwd, cwd))) })
        {
            var projection = Projection(image, "key");
            Require(projection.Cases.Select(field => field.Ordinal).Order().SequenceEqual(expectedOrdinals)
                && projection.Cases.All(field => image.ScopeLayouts[field.Layout].Fields[field.Ordinal].Name == "key"),
                $"{image.Stage}: expected key ordinals [{string.Join(", ", expectedOrdinals)}], "
                + $"got [{string.Join(", ", projection.Cases.Select(field => $"@l{field.Layout}.#{field.Ordinal}"))}].");
            foreach (var (input, expected) in new[] { ("", 11d), ("line\n", 22d) })
            {
                var actual = await Interpreter.ExecuteAsync(image, new() { Input = new StringReader(input) });
                Require(actual is NumberValue { Data: var value } && value == expected,
                    "Selected layouts changed field behavior during source-free lowering or binding.");
            }
        }
    }

    private static async Task VerifyStructuralStorage()
    {
        const string source = """
            let selected = object { key: 11 };
            selected = object { alpha: 0, key: 22 };
            selected::key
            """;
        HirModule hir = Compiler.Analyze(source, "scope-layout-structural-write");
        var member = ClosedProjection(hir);
        var receiver = hir.Node(hir.Children(member)[0]);
        var layout = hir.Types[receiver.Type.Value].ScopeDomain;
        var storage = hir.Types[hir.Locations[receiver.Data].StorageType.Value];
        Require(storage.ScopeDomain.Count == 0
            && layout.Count == 1 && hir.ScopeLayouts[layout[0]].Fields.Length == 2,
            "The structural storage bound must not freeze the first value's physical layout.");
        Module portable = HirLowerer.Lower(hir, new(OptimizationLevel: 0)).Module;
        Require(await Interpreter.ExecuteAsync(RoundTrip(portable)) is NumberValue { Data: 22 },
            "A wider layout must remain admissible to structurally typed storage.");
    }

    private static void VerifyOpaqueSource()
    {
        const string source = """
            let flag = $std::io::read_line() is null;
            let selected = if flag { object { name: "manual", value: "chosen" } }
                else { $std::ranges::front($std::environment::variables()) };
            selected::value
            """;
        HirModule hir = Compiler.Analyze(source, "scope-layout-opaque");
        var member = ClosedProjection(hir, "value");
        var receiver = hir.Node(hir.Children(member)[0]);
        var type = hir.Types[receiver.Type.Value];
        Require(type.Shape >= 0 && type.ScopeDomain.Count == 0,
            "A selected native producer has semantic fields but must not inherit the other arm's construction layout.");
        Module portable = RoundTrip(HirLowerer.Lower(hir, new(OptimizationLevel: 0)).Module);
        Require(Projection(portable, "value").Cases.Count == 0,
            "An opaque receiver must not acquire a typed field table case from the known branch.");
    }

    private static CodeFieldProjection Projection(Module image, string name) =>
        image.Functions.SelectMany(function => function.Instructions).Where(op => op.Op == OpCode.LoadField)
            .Select(op => image.FieldProjections[op.A]).Last(projection => projection.Name == name);

    private static HirNode ClosedProjection(HirModule module, string name = "key")
    {
        Stack<HirNodeId> work = new(module.Functions.Select(function => function.Body));
        while (work.TryPop(out var id))
        {
            var node = module.Node(id);
            if (node.Kind == HirKind.MemberRead && node.Text == name) return node;
            foreach (var child in module.Children(node)) work.Push(child);
        }
        throw new InvalidOperationException("The entry result is not the expected member read.");
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
