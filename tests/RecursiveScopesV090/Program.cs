using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime.Native;
using SourceCompiler = Msync.Compiler.Compiler;

if (args is ["--measure", var measuredSource])
{
    string source = File.ReadAllText(measuredSource);
    long before = GC.GetTotalAllocatedBytes();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var measured = SourceCompiler.Analyze(source, measuredSource);
    watch.Stop();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Seconds = watch.Elapsed.TotalSeconds,
        AllocatedBytes = GC.GetTotalAllocatedBytes() - before, Functions = measured.Functions.Length, Types = measured.Types.Length, Layouts = measured.ScopeLayouts.Length }));
    return;
}

if (args is ["--shapes", .. var shapeOptions] && shapeOptions.Length <= 1)
{
    Console.WriteLine($"PASS {await ShapeRegressionChecks.RunAsync(shapeOptions.FirstOrDefault())} logical-shape source/HIR/artifact contracts");
    return;
}

if (args is ["--ir", var nativeSource, .. var nativeOptions])
{
    int optimization = nativeOptions.Length == 0 ? 2 : int.Parse(nativeOptions[0], System.Globalization.CultureInfo.InvariantCulture);
    Console.Write(Msync.Core.Native.NativeArtifact.ToJson(SourceCompiler.CompileNative(
        File.ReadAllText(nativeSource), nativeSource, optimization)));
    return;
}

if (args is ["--hir", var hirSource])
{
    Console.Write(HirPrinter.Print(SourceCompiler.Analyze(File.ReadAllText(hirSource), hirSource)));
    return;
}

if (args is ["--execute", var executableSource, .. var executionOptions])
{
    string file = Path.GetFullPath(executableSource);
    int optimization = executionOptions.Length == 0 ? 2 : int.Parse(executionOptions[0], System.Globalization.CultureInfo.InvariantCulture);
    var native = SourceCompiler.CompileNative(File.ReadAllText(file), file, optimization);
    using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
    await using NativeInterpreter interpreter = new(native, new(Path.GetDirectoryName(file)!, file)
    { Input = Console.In });
    var result = await interpreter.ExecuteAsync(timeout.Token);
    Console.Error.WriteLine($"RESULT {result}");
    return;
}

if (args is ["--native", .. var nativeFiles])
{
    foreach (string file in nativeFiles)
    {
        _ = SourceCompiler.CompileNative(File.ReadAllText(file), file);
        Console.WriteLine($"PASS native {file}");
    }
    return;
}

if (args is ["--analyze", .. var files])
{
    foreach (string file in files)
    {
        try { _ = SourceCompiler.Analyze(File.ReadAllText(file), file); }
        catch (CompileException error)
        {
            Console.Error.WriteLine(error.Diagnostic.EditorMessage);
            foreach (var related in error.Diagnostic.Related)
                Console.Error.WriteLine($"{related.Message}: {related.SourceName}: {related.Source.Substring(related.Span.Start, related.Span.Length)}");
            throw;
        }
        Console.WriteLine($"PASS {file}");
    }
    return;
}

if (args.Length != 0) throw new ArgumentException("Unknown recursive-layout probe arguments.", nameof(args));

int checks = 0;
foreach (string source in new[]
{
    """
    let row = object { n: 1, Item: Number };
    let other = object { n: 3, Item: Number };
    let view? = $std::ranges::first([row, other]);
    if view is null { 0 } else {
        view::Item = String;
        let Item = row::Item;
        let value: Item = 7;
        value
    }
    """,
    """
    let row = object { n: 1, Item: Number };
    let other = object { n: 3, Item: Number };
    fn alter(view, original) {
        view::Item = String;
        let Item = original::Item;
        let value: Item = 7;
        value
    };
    let view? = $std::ranges::first([row, other]);
    if view is null { 0 } else { alter(view, row) }
    """,
    """
    fn alter(view, original) {
        view::Item = String;
        let Item = original::Item;
        let value: Item = 7;
        value
    };
    let a = object { n: 1, Item: Number };
    let b = object { n: 2, Item: Number };
    let separate = object { n: 3, Item: Number };
    let first? = $std::ranges::first([a, b]);
    if first is not null { alter(first, separate) };
    let c = object { n: 4, Item: Number };
    let d = object { n: 5, Item: Number };
    let second? = $std::ranges::first([c, d]);
    if second is null { 0 } else { alter(second, c) }
    """
})
{
    bool rejected = false;
    try { _ = SourceCompiler.Analyze(source, "alias-written-Type-witness.msync"); }
    catch (CompileException error)
    {
        rejected = error.Message.Contains("requires a statically known Type value", StringComparison.Ordinal);
    }
    Check(rejected, "A may-alias write invalidates the written Type witness, including after disjoint specialization reuse");
}

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

HirBuilder builder = new();
HirTypeId number = builder.Type(TypeRef.Number);
HirTypeId node = builder.RecursiveScope("node:Number", self => new([
    new("payload", number), new("next", builder.Type(builder.Types[self.Value] with
    { RuntimeType = TypeRef.Scope.WithNull, Nullable = true }), true)
]));
HirTypeId current = node;
for (int index = 0; index < 1024; index++)
{
    HirScopeLayout layout = builder.ScopeLayouts[builder.Types[current.Value].Shape];
    Check(layout.Fields[0].StorageType == number, "A recursive edge retains its builtin payload type");
    current = layout.Fields[1].StorageType;
    Check(builder.Types[current.Value].RuntimeType == TypeRef.Scope.WithNull, "The recursive edge retains null admission");
}
int typeCount = builder.Types.Count, layoutCount = builder.ScopeLayouts.Count;
for (int index = 0; index < 128; index++)
    Check(builder.RecursiveScope("node:Number", _ => throw new InvalidOperationException("Must reuse closed schema")) == node,
        "A stable recursive equation reuses its canonical identity");
Check(builder.Types.Count == typeCount && builder.ScopeLayouts.Count == layoutCount,
    "Repeated projection does not unfold recursive types");

int empty = -1;
HirTypeId container = builder.RecursiveScope("container", _ =>
{
    empty = builder.InternScopeLayout(new([]));
    return new([new("payload", number)]);
});
Check(empty != builder.Types[container.Value].Shape, "A reserved recursive row is not an empty-row canonical match");
Check(builder.ScopeLayouts[empty].Fields.Length == 0, "Completing a recursive row does not mutate an interned empty row");

typeCount = builder.Types.Count;
layoutCount = builder.ScopeLayouts.Count;
using (builder.BeginTransaction())
{
    _ = builder.RecursiveScope("rolled-back", self => new([new("next", self)]));
    _ = builder.InternScopeLayout(new([new("other", number)]));
}
Check(builder.Types.Count == typeCount && builder.ScopeLayouts.Count == layoutCount,
    "Generation rollback removes its recursive metadata");
HirTypeId restored = builder.RecursiveScope("rolled-back", self => new([new("next", self)]));
Check(restored.Value >= typeCount, "A rolled-back identity is reconstructed, not a stale index");
typeCount = builder.Types.Count;
layoutCount = builder.ScopeLayouts.Count;
try { _ = builder.RecursiveScope("failed", _ => throw new InvalidOperationException("selected failure")); }
catch (InvalidOperationException error) when (error.Message == "selected failure") { }
Check(builder.Types.Count == typeCount && builder.ScopeLayouts.Count == layoutCount,
    "An incomplete recursive equation leaves no reserved placeholder");

foreach (string source in new[]
{
    """
    fn make(value: Number) { object { payload: value, next?: null } };
    let first = make(1);
    let second = make(2);
    first::next = second;
    second::next = first;
    let current = first;
    let count = 0;
    loop {
        if count == 4 { break; };
        let payload = current::payload;
        let next? = current::next;
        if next is null { break; };
        current = next;
        count = count + 1;
    };
    """,
    """
    fn make(value: String) { object { payload: value, next?: null } };
    let first = make("one");
    let second = make("two");
    first::next = second;
    second::next = first;
    fn read(node) {
        let current = node;
        let count = 0;
        loop {
            if count == 4 { break; };
            let payload = current::payload;
            let next? = current::next;
            if next is null { break; };
            current = next;
            count = count + 1;
        };
    };
    read(first);
    """
})
{
    HirModule module = SourceCompiler.Analyze(source, "recursive-scope-contract.msync");
    TypeRef expected = source.Contains("String", StringComparison.Ordinal) ? TypeRef.String : TypeRef.Number;
    HashSet<HirNodeId> executable = [];
    void Visit(HirNodeId id)
    {
        if (!executable.Add(id)) return;
        foreach (HirNodeId child in module.Children(module.Node(id))) Visit(child);
    }
    foreach (HirFunction function in module.Functions) Visit(function.Body);
    HirNode[] reads = executable.Select(module.Node).Where(item => item.Kind == HirKind.MemberRead && item.Text == "payload").ToArray();
    Check(reads.Length != 0 && reads.All(item => module.RuntimeType(item.Type) == expected),
        $"Recursive Object reads retain {expected} in finalized HIR, found [{string.Join(", ", reads.Select(item => module.RuntimeType(item.Type)))}]");
}

{
    string source = """
    fn parent(node) { let link? = node::parent; if link is null { node } else { link } };
    fn child(node) { let link? = node::left; if link is null { node } else { link } };
    fn make(Item: Type) {
        let key?: Item = null;
        let nil = object { key?: key, left?: null, parent?: null, red: false, nil: true };
        nil::left = nil; nil::parent = nil;
        object { root: nil, nil: nil }
    };
    fn repair(inserted) {
        let index = inserted;
        loop {
            let next = parent(index);
            if not next::red { break; };
            let grand = parent(next);
            let uncle = child(grand);
            if uncle::red { index = grand; } else { break; };
        };
    };
    let tree = make(Number);
    let node = object { key?: 2, left?: tree::nil, parent?: tree::nil, red: true, nil: false };
    tree::root = node;
    repair(node);
    """;
    _ = SourceCompiler.Analyze(source, "recursive-nullable-node-helpers.msync");
    Check(true, "Nullable recursive node schemas survive constructor returns and helper-call loop joins");
}

foreach (string source in new[]
{
    "let row = object { old: 1 }; row = object { fresh: 2 }; row::fresh;",
    "let row? = null; row = object { old: 1 }; row = object { fresh: 2 }; if row is not null { row::fresh; };",
    "let Item = Number; let value?: Item = null; value = 7; value;",
    "let Item = Number?; let value: Item = null; value = 7; value;",
    "fn typed(Item: Type, value?) { let result?: Item = value; object { value?: result } }; let a = typed(Number, null); a::value = 7; a::value;",
    "fn typed(Item: Type, value?) { let result: Item = value; result }; typed(String, \"ok\"); typed(Number, 7);",
    "let Item = Number; fn typed(value) { let result: Item = value; result }; typed(7);"
})
{
    _ = SourceCompiler.Analyze(source, "type-witness-annotation.msync");
    Check(true, "Lexical Type witnesses define fixed storage constraints without an exemplar");
}
foreach (string source in new[]
{
    "let Item = Number; let value: Item = 7; value = \"bad\";",
    "let Item = Number; let value?: Item = null; let row = object { value?: value }; row::value = \"bad\";",
    "let Item = 7; let value: Item = 7;"
})
{
    bool rejected = false;
    try { _ = SourceCompiler.Analyze(source, "invalid-type-witness.msync"); }
    catch (CompileException) { rejected = true; }
    Check(rejected, "An annotation seals its carrier and requires an actual compile-time Type witness");
}

{
    HirModule cast = SourceCompiler.Analyze("""
        fn project(Layout: Type, value: Object) { value as Layout };
        let row = object { payload: 7 };
        let typed = project(decltype(row), row);
        typed::payload
        """, "scope-layout-witness-cast.msync");
    HirNode[] conversions = cast.Nodes.Where(node => node.Kind == HirKind.Convert).ToArray();
    Check(conversions.Any(node => cast.Types[node.Type.Value].Shape >= 0),
        "A lexical Type witness cast retains the target Object schema in HIR");
}

{
    _ = SourceCompiler.Analyze("""
        fn make(seed: Number) {
            let outer = object { inner: object { n: seed } };
            fn() { outer::inner::n = outer::inner::n + 1; outer::inner::n }
        };
        let next = make(4);
        next() * 10 + next()
        """, "nested-captured-scope-write.msync");
    Check(true, "A nested captured-scope write remains accepted; execution is checked by the shape journey");
}

{
    var recursion = SourceCompiler.CompileNative("""
        fn recurse(value) { if value { true } else { recurse(true) } };
        recurse(false)
        """, "recursive-scalar-carrier-key.msync", optimization: 0);
    Check(recursion.Functions.Count(function => function.Name == "recurse" && !function.IsTemplate) == 1,
        "A by-value scalar storage hint does not create another executable specialization");
}

{
    string policy = """
        let Item = decltype(object { ticket: 0, request: object { units: 0 } });
        fn accept(order: $std::concepts::StrictOrder) { true };
        accept(object { Self: Item, sample?: null, less: fn(left, right) { left::ticket < right::ticket } })
        """;
    _ = SourceCompiler.Analyze(policy, "nullable-structural-concept-sample.msync");
    Check(true, "A nullable associated Object witness admits an actual Null without fabricating a record");
    bool rejected = false;
    try
    {
        _ = SourceCompiler.Analyze("""
            let Item = decltype(object { ticket: 0 });
            let wrong?: String = null;
            fn accept(order: $std::concepts::StrictOrder) { true };
            accept(object { Self: Item, sample?: wrong, less: fn(left, right) { left::ticket < right::ticket } })
            """, "incompatible-nullable-concept-carrier.msync");
    }
    catch (CompileException) { rejected = true; }
    Check(rejected, "A Null observation does not disguise an incompatible fixed String carrier as an Object witness");
}

checks += await ShapeRegressionChecks.RunAsync();
Console.WriteLine($"PASS {checks} recursive-layout contracts");
