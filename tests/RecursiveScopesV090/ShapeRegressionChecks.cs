using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class ShapeRegressionChecks
{
    internal static async Task<int> RunAsync(string? selected = null)
    {
        int checks = 0;
        (string Name, string Source, double Expected)[] cases =
        [
            ("alias-untouched-Type-frame", """
                let row = scope { n: 1, Item: Number };
                let other = scope { n: 3, Item: Number };
                let values = [row, other];
                let view? = $std::ranges::first(values);
                if view is null { 0 } else {
                    view::n = 2;
                    let Item = row::Item;
                    let value: Item = 7;
                    value
                }
                """, 7),
            ("nested-captured-scope-write", """
                fn make(seed: Number) {
                    let outer = scope { inner: scope { n: seed } };
                    fn() { outer::inner::n = outer::inner::n + 1; outer::inner::n }
                };
                let next = make(4);
                next() * 10 + next()
                """, 56),
            ("alias-disjoint-Type-frame", """
                let row = scope { n: 1, Item: Number };
                let other = scope { n: 3, Item: Number };
                let untouched = scope { n: 4, Item: Number };
                fn alter(view, original) {
                    view::Item = String;
                    let Item = original::Item;
                    let value: Item = 7;
                    value
                };
                fn forward(view, original) { alter(view, original) };
                let view? = $std::ranges::first([row, other]);
                if view is null { 0 } else { forward(view, untouched) }
                """, 7),
            ("returned-alias-view-write", """
                let a = scope { n?: 1 };
                let b = scope { n?: 2 };
                fn select(values) { $std::ranges::first(values) };
                let selected? = select([a, b]);
                if selected is not null { selected::n = null };
                let first? = a::n;
                let second? = b::n;
                if first is null { if second is null { 0 } else { second + 5 } } else { 0 }
                """, 7),
            ("escaped-alias-view-write", """
                let a = scope { n?: 1 };
                let b = scope { n?: 2 };
                fn make(values) {
                    let selected? = $std::ranges::first(values);
                    fn() { if selected is not null { selected::n = null }; 0 }
                };
                let clear = make([a, b]);
                clear();
                let first? = a::n;
                let second? = b::n;
                if first is null { if second is null { 0 } else { second + 5 } } else { 0 }
                """, 7),
            ("nullable-storage-flow", """
                fn next(value?: Number or Null) { if value is null { 0 } else { value + 1 } };
                let initial?: Number = null;
                let node = scope { key?: initial };
                fn from_node(node) {
                    let key? = node::key;
                    if key is null { 0 } else { key + 1 }
                };
                let empty = from_node(node);
                node::key = 7;
                next(null) + next(3) + empty + from_node(node)
                """, 12),
            ("associated-scope-sample", """
                let Tree = $std::containers::BTree;
                let records = Tree::with_policy(scope { group: "", id: 0 }, 0, scope {
                    sample: scope { group: "", id: 0 },
                    less: fn(left: Scope, right: Scope) {
                        if left::group == right::group { left::id < right::id }
                        else { left::group < right::group }
                    }
                });
                Tree::insert(records, scope { group: "a", id: 1 }, 11);
                Tree::insert(records, scope { group: "a", id: 2 }, 22);
                let selected? = Tree::get(records, scope { group: "a", id: 1 });
                if selected is null { 0 } else { selected::value }
                """, 11),
            ("nested-captured-family", """
                let family = $std::containers::Set;
                fn inner(tree) { family::snapshot(tree) };
                fn outer(tree) { inner(tree) };
                let tree = family::new(Number);
                family::insert(tree, 1);
                $std::ranges::size(outer(tree))
                """, 1),
            ("recursive-future-capture", """
                let target = fn(n) { 0 };
                fn factory(value) {
                    fn walk(n) {
                        if n == 0 { value } else { target(n - 1) }
                    };
                    walk
                };
                let first = factory(10);
                target = factory(20);
                first(3)
                """, 20),
            ("distinct-live-captures", """
                fn factory(value) { fn() { value::number } };
                let target = factory(scope { number: 10 });
                let first = target;
                target = factory(scope { number: 20 });
                first() * 100 + target()
                """, 1020)
        ];
        if (selected is not null && !cases.Any(item => item.Name == selected))
            throw new ArgumentException($"Unknown logical-shape journey '{selected}'.", nameof(selected));
        foreach (var item in cases.Where(item => selected is null || item.Name == selected))
        {
            Console.WriteLine($"Logical-shape journey: {item.Name}");
            HirModule hir = SourceCompiler.Analyze(item.Source, item.Name + ".msync");
            using MemoryStream high = new();
            HirArtifact.Write(hir with { Sources = [.. hir.Sources.Select(source => source with { Text = null })] }, high);
            high.Position = 0;
            HirModule detached = HirArtifact.Read(high);
            foreach (int level in new[] { 0, 2 })
                foreach (HirModule input in new[] { hir, detached })
                {
                    Console.WriteLine($"  O{level} {(ReferenceEquals(input, hir) ? "source" : "stripped HIR")}");
                    NativeProgram program = SourceCompiler.LowerInstructions(input, level);
                    try { NativeArtifact.Validate(program); }
                    catch
                    {
                        string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "logical-shape-failures");
                        Directory.CreateDirectory(directory);
                        File.WriteAllText(Path.Combine(directory, $"{item.Name}-O{level}.json"), NativeArtifact.ToJson(program));
                        throw;
                    }
                    using MemoryStream low = new();
                    NativeArtifact.Write(program, low);
                    low.Position = 0;
                    NativeProgram portable = NativeArtifact.Read(low);
                    NativeProgram bound = NativePreparation.Prepare(portable,
                        new BindingContext(Environment.CurrentDirectory, HostContext.HomeDirectory, null));
                    foreach (NativeProgram executable in new[] { program, portable, bound })
                    {
                        await using NativeInterpreter interpreter = new(executable,
                            new() { Tiers = new(NativeJitPolicy.Off) });
                        NativeValue result = await interpreter.ExecuteAsync();
                        if (result.Kind != NativeKind.Number || result.AsNumber() != item.Expected)
                            throw new InvalidOperationException($"{item.Name} O{level}: expected {item.Expected}, got {result}");
                        checks++;
                    }
                }
        }
        foreach (string source in new[]
        {
            """
            fn check(order: $std::concepts::StrictOrder) { true };
            check(scope { sample: scope { group: "" }, less: fn(left: Scope, right: Scope) { left::missing < right::missing } })
            """,
            """
            fn check(order: $std::concepts::StrictOrder) { true };
            check(scope { sample: scope { group: "" }, less: fn(left: Scope, right: Scope) { left::group } })
            """
        })
        {
            bool rejected = false;
            try { _ = SourceCompiler.Analyze(source, "invalid-associated-scope-policy.msync"); }
            catch (CompileException error) { rejected = error.Diagnostic.Code == "E0214"; }
            if (!rejected) throw new InvalidOperationException("An invalid associated Scope policy escaped concept checking");
            checks++;
        }
        bool incompatibleCapture = false;
        try
        {
            _ = SourceCompiler.Analyze("""
                fn factory(value) { fn() { value::number } };
                let operation = factory(scope { number: 10 });
                let pending = $std::ranges::transform([true], fn(_) -> Number { operation() });
                operation = factory(scope { number: "wrong" });
                $std::ranges::first(pending)
                """, "incompatible-future-capture.msync");
        }
        catch (CompileException error) { incompatibleCapture = error.Diagnostic.Code == "E0203"; }
        if (!incompatibleCapture)
            throw new InvalidOperationException("A fresh factory capture changed the deferred callable's established result ABI");
        checks++;
        return checks;
    }
}
