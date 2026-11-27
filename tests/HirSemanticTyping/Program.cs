using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

if (args.Contains("--recursive-captures", StringComparer.Ordinal))
{
    await RecursiveCaptureChecks.Run();
    return;
}
if (args.Contains("--direct-native-projection", StringComparer.Ordinal))
{
    await DirectNativeProjectionChecks.Run();
    return;
}
if (args.Contains("--scope-layout-domains", StringComparer.Ordinal))
{
    await ScopeLayoutDomainChecks.Run();
    return;
}

int checks = 0;
bool metadataOnly = args.Contains("--meta", StringComparer.Ordinal);
bool typeIdentityOnly = args.Contains("--type-is", StringComparer.Ordinal);
bool resourceUnionOnly = args.Contains("--indexed-union", StringComparer.Ordinal);
bool indexedCastOnly = args.Contains("--indexed-cast", StringComparer.Ordinal);
bool indexedCurrentOnly = args.Contains("--indexed-current", StringComparer.Ordinal);
bool indexedFreshOnly = args.Contains("--indexed-fresh", StringComparer.Ordinal);
bool rangeIdentityOnly = args.Contains("--range-identity", StringComparer.Ordinal);
bool rangeViewsOnly = args.Contains("--range-views", StringComparer.Ordinal);
bool nestedEffectsOnly = args.Contains("--nested-effects", StringComparer.Ordinal);
bool nativeEffectsOnly = args.Contains("--native-effects", StringComparer.Ordinal);
bool conceptPendingOnly = args.Contains("--concept-pending", StringComparer.Ordinal);
if (conceptPendingOnly)
{
    const string contract = "concept Order { sample?: typename Self, less: fn(a: Self, b: Self) -> Bool }; fn need(value: Order) { value }; ";
    const string policy = "let policy = object { Self: Number, sample?: null, less: fn(a: Number, b: Number) { a < b } }; ";
    await Number(contract + policy + "need(policy); policy::sample = 3; policy::sample", 3);
    await Number(contract + "let policy = object { sample?: null, less: fn(a: Number, b: Number) { a < b } }; need(policy); policy::sample = 3; policy::sample", 3);
    RejectCarrier(contract + policy + "let alias = policy; need(policy); alias::sample = true;");
    RejectCarrier(contract + policy + "fn wrap(value) { need(value); value }; wrap(policy); wrap(policy); policy::sample = true;");
    await Boolean(contract + policy + "$std::meta::is_invocable(fn { need(policy) }); policy::sample = true; policy::sample", true);
    RejectCarrier(contract + "let policy = object { Self: Number, sample?: \"fixed\", less: fn(a: Number, b: Number) { a < b } }; policy::sample = null; need(policy);");
    RejectCarrier(contract + "let policy = object { Self: Number, sample?: if exists ./concept-selector { true } else { 1 }, less: fn(a: Number, b: Number) { a < b } }; policy::sample = null; need(policy);");
    await Text(contract + "fn make() { object { Self: Number, sample?: null, less: fn(a, b) { a < b } } }; let first = make(); need(first); let second = make(); second::Self = String; need(second); second::sample = \"independent\"; second::sample", "independent");
    Console.WriteLine($"PASS {checks} concept pending storage journeys");
    return;
}
if (nativeEffectsOnly)
{
    Check(NativeSignatures.All.All(signature => signature.Behavior is not null),
        "Native effect metadata must be complete before semantic analysis.");
    HirModule process = Compiler.Analyze("let child = ./msync-hir-never-executed(); child::stdout", "process-effect-check.msync");
    Check(process.Nodes.Any(node => node.Kind == HirKind.MemberRead && node.Text == "stdout"
        && (node.Effects & (HirEffects.ReadExternal | HirEffects.Invoke | HirEffects.MayFail))
            == (HirEffects.ReadExternal | HirEffects.Invoke | HirEffects.MayFail)),
        "A Process stdout read must retain external wait effects without executing it during analysis.");
    _ = HirLowerer.Lower(process, new(OptimizationLevel: 0));
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        try
        {
            _ = await Execute("native fn cast(value, target: Type) -> Number; cast(\"not-a-number\", Number); 3", stage, 2);
        }
        catch (MsyncException error)
        {
            Check(error.Message.Contains("Cannot cast", StringComparison.Ordinal)
                && error.Message.Contains("not-a-number", StringComparison.Ordinal),
                "The discarded invalid native cast must preserve its actual failure after O2.");
            continue;
        }
        throw new InvalidOperationException("O2 erased a native cast failure.");
    }
    Console.WriteLine($"PASS {checks} native semantic effect journeys");
    return;
}
if (nestedEffectsOnly)
{
    const string native = "native fn transform(values: Range, operation: Function) -> Range; native fn single(value) -> Range; native fn first(values: Range) -> Range; native fn materialize(values: Range) -> Range; ";
    const string inner = "let state = object { changed: false }; let inner = transform([1], fn(value) { state::changed = true; value }); let selected = first(single(inner)); ";
    await Number(native + inner + "if state::changed { 1 } else { 0 }", 0);
    await Number(native + inner + "materialize(selected); if state::changed { 1 } else { 0 }", 1);
    Console.WriteLine($"PASS {checks} nested Range effect journeys");
    return;
}
if (rangeIdentityOnly || rangeViewsOnly)
{
    if (!rangeViewsOnly)
    {
        await Numbers("native fn ranges.identity(values: Range) -> Range; ranges.identity([1, 2])", [1, 2]);
        await Numbers("$std::ranges::identity([1, 2])", [1, 2]);
        await Numbers("$std::ranges::all([1, 2])", [1, 2]);
        await Number("let child = $std::storage::vector_new([1]); let rows = $std::storage::vector_new([object { items: child }]); let view = $std::ranges::reverse($std::storage::vector_snapshot(rows)); let row = $std::ranges::first(view); $std::storage::vector_push(row::items, 2); $std::storage::vector_size(child)", 2);
    }
    const string snapshot = "let values = $std::storage::vector_new([1]); let saved = $std::storage::vector_snapshot(values); ";
    Reject(snapshot + "$std::storage::vector_push($std::ranges::reverse(saved), 2)", "E0203");
    Reject(snapshot + "$std::storage::vector_push($std::ranges::filter(saved, fn(value) { true }), 2)", "E0203");
    Reject(snapshot + "$std::storage::vector_push($std::ranges::materialize(saved), 2)", "E0203");
    Reject(snapshot + "$std::ranges::identity(values)", "E0203");
    Console.WriteLine($"PASS {checks} native Range identity journeys");
    return;
}
if (indexedFreshOnly)
{
    await Number("let rows = $std::storage::vector_new([object { children: $std::storage::vector_new([1]) }]); $std::storage::vector_push($std::storage::vector_get(rows, 0)::children, 3)", 1);
    await Number("fn source() { [object { children: $std::storage::vector_new([1]) }] }; let rows = $std::storage::vector_new(source()); $std::storage::vector_push($std::storage::vector_get(rows, 0)::children, 3)", 1);
    Console.WriteLine($"PASS {checks} fresh deferred resource graph journeys");
    return;
}
if (indexedCurrentOnly)
{
    await Text("let values = $std::storage::vector_empty(Number); $std::meta::type_name(decltype(values))", "Storage<Number>");
    await Text("let values = $std::storage::vector_empty(Number); let alias = values; let holder = object { items: alias }; $std::storage::vector_push(values, 42); $std::meta::type_name(decltype(holder::items))", "Storage<Number>");
    await Text("let values = $std::storage::vector_empty(Number); let saved = $std::storage::vector_snapshot(values); $std::storage::vector_push(values, 42); $std::meta::type_name(decltype(saved))", "Range<Number>");
    await Number("let values = $std::storage::vector_empty(Number); $std::storage::vector_push(values, 42); let sum = 0; for value in $std::storage::vector_snapshot(values) { sum = sum + value; }; sum", 42);
    Reject("let values = $std::storage::vector_empty(Number); for value in values { value; };", "E0203");
    Console.WriteLine($"PASS {checks} fixed indexed carrier HIR journeys");
    return;
}
if (indexedCastOnly)
{
    const string vectors = "let paths = $std::storage::vector_new([./first]); let texts = $std::storage::vector_new([\"first\"]); let rows = $std::storage::vector_new([object { items: paths }, object { items: texts }]); ";
    await PathResult(vectors + "$std::storage::vector_push($std::storage::vector_get(rows, 0)::items, \"./next\"); $std::storage::vector_get(paths, 1)", "./next");
    await Text(vectors + "$std::storage::vector_push($std::storage::vector_get(rows, 1)::items, ./next); $std::storage::vector_get(texts, 1)", "./next");
    await PathResult(vectors + "$std::storage::vector_set($std::storage::vector_get(rows, 0)::items, 0, \"./next\"); $std::storage::vector_get(paths, 0)", "./next");
    await Text(vectors + "$std::storage::vector_insert($std::storage::vector_get(rows, 1)::items, 0, ./next); $std::storage::vector_get(texts, 0)", "./next");
    await IndexedIdentity();
    Console.WriteLine($"PASS {checks} indexed target conversion journeys");
    return;
}
if (resourceUnionOnly)
{
    await Number("let first = object { children: $std::storage::vector_new([1]) }; let second = object { children: $std::storage::vector_new([2]) }; let nodes = $std::storage::vector_new([first, second]); $std::storage::vector_push($std::storage::vector_get(nodes, 0)::children, 3)", 1);
    await Number("let first = object { children: $std::storage::vector_new([1]) }; let second = object { children: $std::storage::vector_new([2]) }; let nodes = $std::storage::vector_new([first, second]); fn select(nodes, index: Number) { $std::storage::vector_get(nodes, index)::children }; $std::storage::vector_push(select(nodes, 0), 3)", 1);
    await Boolean("let state = object { changed: false }; let rows = $std::storage::vector_new([object { children: $std::storage::vector_new([1]) }]); fn choose(rows) { state::changed = true; $std::storage::vector_get(rows, 0)::children }; let selected = choose(rows); state::changed", true);
    await Number("let first = object { children: $std::storage::vector_new([1]) }; let second = object { children: $std::storage::vector_new([2]) }; let rows = $std::storage::vector_new([first, second]); let selector = 0; fn choose(rows) { $std::storage::vector_get(rows, selector)::children }; $std::storage::vector_push(choose(rows), { selector = 1; 3 }); $std::storage::vector_size(first::children)", 2);
    Reject("let candidate = if $std::fs::exists(./resource-choice) { $std::storage::vector_new([1]) } else { [2] }; $std::storage::vector_push(candidate, 3)", "E0203");
    Console.WriteLine($"PASS {checks} indexed resource union HIR journeys");
    return;
}
if (typeIdentityOnly)
{
    await Number("let count = 0; for type in [Number, String] { if 3 is type { count = count + 1; }; }; count", 1);
    await Number("let count = 0; for type in [Number, String] { if type is Number { count = count + 1; }; }; count", 1);
    await Number("let state = 0; let matches = { state = 1; 3 } is { if state == 1 { state = 2; Number } else { String } }; if matches { state } else { 0 }", 2);
    await Boolean("decltype(~/.codex) is Path", true);
    await Boolean("let target = Number; 3 is { target = String; target }", false);
    Reject("3 is 3", "E0203");
    Console.WriteLine($"PASS {checks} dynamic Type identity HIR journeys");
    return;
}
if (metadataOnly)
{
    await Boolean("$std::meta::is_invocable(fn(value) { value + 1 }, Number)", true);
    await Boolean("$std::meta::is_invocable(fn(value) { value + 1 }, Bool)", false);
    await Boolean("fn bad(value) { 1 + true }; $std::meta::is_invocable(bad, Number)", false);
    await Boolean("fn unused(operation) { $std::meta::is_invocable(operation, Number) }; $std::meta::is_invocable(fn(value) { value }, Number)", true);
    await Boolean("fn recur(value: Number) -> Number { if value > 0 { recur(value - 1) } else { 0 } }; $std::meta::is_invocable(recur, Number)", true);
    await Boolean("$std::meta::is_invocable_r(String, fn(value) { value }, Path)", true);
    await Boolean("$std::meta::is_invocable_r(Number, fn(value) { value }, Path)", false);
    await Boolean("$std::meta::is_same($std::meta::invoke_result(fn(value) { object { item: value } }, Number), Object)", true);
    await Number("$std::meta::parameter_count(fn(first, second: Number) { first })", 2);
    await Boolean("native fn first(values: Range) -> Type; first($std::meta::parameter_types(fn(value: Number) { value })) == Number", true);
    await Boolean("$std::meta::is_variadic(./command)", true);
    await Boolean("$std::meta::has_member(object { item: 3 }, \"item\")", true);
    await Number("$std::meta::member_count(object { first: 3, second: true })", 2);
    await Text("native fn first(values: Range) -> String; first($std::meta::member_names(object { z: 3, a: true }))", "a");
    await Boolean("$std::meta::is_same($std::meta::member_type(object { item: 3 }, \"item\"), Number)", true);
    await Number("let count = 0; fn effect() { count = 1; 3 }; $std::meta::is_invocable(effect); count", 0);
    await Boolean("let count = 0; $std::meta::has_member({ count = 1; object { item: true } }, \"item\"); count == 0", true);
    await Number("let count = 0; fn choose() { count = 1; $std::meta::is_invocable }; choose()(fn(value) { value }, Number); count", 1);
    await Boolean("concept Numeric { op: fn(Number) -> Number }; fn require_numeric(value: Numeric) { value }; let mutable = object { op: fn(value: Number) { value } }; let invalid = fn { require_numeric(mutable); mutable::op = fn(value: Number) { true }; }; $std::meta::is_invocable(invalid)", false);
    await Boolean("concept Numeric { op: fn(Number) -> Number }; fn require_numeric(value: Numeric) { value }; let mutable = object { op: fn(value: Number) { value } }; let invalid = fn { require_numeric(mutable); mutable::op = fn(value: Number) { true }; }; $std::meta::is_invocable(invalid); mutable::op = fn(value: Number) { true }; mutable::op(3)", true);
    Reject("fn bad(value) { 1 + true }; $std::meta::invoke_result(bad, Number)", "E0222");
    Console.WriteLine($"PASS {checks} dependent metadata HIR journeys");
    return;
}
await Number("1 + 2 * 3", 7);
await Number("-2 ** 2", -4);
await Number("\"2.5\" as Number", 2.5);
await Boolean("let value? = null; value is null", true);
await Boolean("let value?: Number = 3; value = null; value is null", true);
await Boolean("let value? = null; value is not null and value > 0", false);
await Text("fn text(value: String) -> String { value }; text(./sample)", "./sample");
await Number("let record = object { value: 1 }; let alias = record; alias::value = 7; record::value", 7);
await Number("concept Numeric { item: typename Item }; fn use(value: Numeric) { value::item }; use(object { item: 4 })", 4);
Reject("null == null", "E0201");
Reject("let item = 1; item = true", "E0203");
Reject("let record = object { item: 1 }; record::item = true", "E0203");
Reject("null as Number", "E0220");
Reject("concept Numeric { item: Number }; fn use(value: Numeric) { value }; use(object { item: true })", "E0214");

const string transform = "native fn transform(values: Range, operation: Function) -> Range;";
await Numbers(transform + "transform([1, 2], fn(value) { value + 1 })", [2, 3]);
await Number(transform + "let state = object { changed: false }; let pending = transform([1], fn(value) { state::changed = true; value }); if state::changed { 1 } else { 0 }", 0);
await Number(transform + "native fn materialize(values: Range) -> Range; let state = object { changed: false }; let pending = transform([1], fn(value) { state::changed = true; value }); materialize(pending); if state::changed { 1 } else { 0 }", 1);
await Number(transform + "native fn materialize(values: Range) -> Range; let state?: Number = 4; let pending = transform([1], fn(value) { state = null; value }); materialize(pending); if state is null { 1 } else { 0 }", 1);
await Number(transform + "native fn materialize(values: Range) -> Range; let state = object { changed: false }; fn build() { transform([1], fn(value) { state::changed = true; value }) }; materialize(build()); if state::changed { 1 } else { 0 }", 1);
await Number(transform + "native fn materialize(values: Range) -> Range; let state = object { changed: false }; materialize(transform([], fn(value) { state::changed = true; value })); if state::changed { 1 } else { 0 }", 0);
await Number(transform + "native fn materialize(values: Range) -> Range; native fn single(value) -> Range; let state = object { changed: false }; let pending = transform([1], fn(value) { state::changed = true; value }); materialize(single(pending)); if state::changed { 1 } else { 0 }", 0);
const string filter = "native fn filter(values: Range, operation: Function) -> Range;";
await Numbers(filter + "filter([1, 2, 3], fn(value) { value > 1 })", [2, 3]);
Reject(filter + "filter([1], fn(value) { value })", "E0203");
await Numbers("$std::ranges::concat([1], [2])", [1, 2]);
await Numbers("native fn flatten(values: Range) -> Range; flatten([[1], [2]])", [1, 2]);
await Number("native fn zip(left: Range, right: Range) -> Range; native fn first(values: Range) -> Object; first(zip([3], [4]))::second", 4);
await Number("native fn enumerate_from(values: Range, index: Number) -> Range; native fn first(values: Range) -> Object; first(enumerate_from([8], 5))::index", 5);
await Numbers("$std::ranges::repeat(4, 2)", [4, 4]);
await Numbers("native fn scan(values: Range, initial: Number, operation: Function) -> Range; scan([1, 2], 0, fn(a, b) { a + b })", [1, 3]);
await Boolean("native fn is_same(first: Type, second: Type) -> Bool; is_same(decltype(3), Number)", true);
await Number("let items = $std::storage::vector_new([1]); $std::storage::vector_set(items, 0, 7); $std::storage::vector_get(items, 0)", 7);
await Numbers("let items = $std::storage::vector_new([1]); let saved = $std::storage::vector_snapshot(items); $std::storage::vector_set(items, 0, 7); saved", [1]);
Reject("let items = $std::storage::vector_new([1]); $std::storage::vector_set(items, 0, true)", "E0203");
await Boolean("let first = object { red: false }; let second = object { red: false }; let items = $std::storage::vector_new([first, second]); let a = $std::storage::vector_get(items, 0); let b = $std::storage::vector_get(items, 1); a::red = true; if b::red { true } else { false }", false);
await Boolean("let first = object { red: false }; let items = $std::storage::vector_new([first]); let a = $std::storage::vector_get(items, 0); let b = $std::storage::vector_get(items, 0); a::red = true; b::red = false; if a::red { true } else { false }", false);
await Boolean("let first = object { red: false }; let items = $std::storage::vector_new([first]); let a = $std::storage::vector_get(items, 0); a::red = true; if first::red { true } else { false }", true);
Console.WriteLine($"PASS {checks} typed HIR type/operator/native contracts");

async Task<Value> Execute(string source, ArtifactStage stage, int optimizationLevel = 0)
{
    HirModule hir = Compiler.Analyze(source, "hir-typing.msync");
    if (metadataOnly)
    {
        Check(!HasRuntimeDependentMeta(hir), "Dependent metadata must not survive as a runtime native call");
    }
    if (metadataOnly || typeIdentityOnly || resourceUnionOnly || indexedCastOnly || indexedCurrentOnly || indexedFreshOnly || rangeIdentityOnly || nestedEffectsOnly || conceptPendingOnly)
    {
        using MemoryStream graph = new();
        HirArtifact.Write(hir, graph);
        graph.Position = 0;
        hir = HirArtifact.Read(graph);
    }
    Module code = HirLowerer.Lower(hir, new(OptimizationLevel: optimizationLevel)).Module;
    if (stage == ArtifactStage.Executable)
        code = Msync.Optimization.Binding.Bind(code, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream buffer = new();
    Artifact.Write(code, buffer);
    buffer.Position = 0;
    return await Interpreter.ExecuteAsync(Artifact.Read(buffer));
}

async Task Number(string source, double expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value value = await Execute(source, stage);
        Check(value is NumberValue actual && actual.Data == expected,
            $"{source}: expected Number {expected}, received {value.Type} '{value}' ({stage})");
    }
}
async Task Boolean(string source, bool expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
        Check(await Execute(source, stage) is BoolValue actual && actual.Data == expected, source);
}
async Task Text(string source, string expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value result = await Execute(source, stage);
        Check(result is StringValue actual && actual.Data == expected,
            $"{source}: expected String '{expected}', received {result.Type} '{result}' ({stage})");
    }
}
async Task PathResult(string source, string expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value value = await Execute(source, stage);
        Check(value is PathValue path && path.NativeText == expected
            && path.WorkingDirectory == Environment.CurrentDirectory,
            $"{source}: expected anchored Path '{expected}', received {value.Type} '{value}' ({stage})");
    }
}
async Task IndexedIdentity()
{
    RuntimeContext context = new();
    async ValueTask<Value> Invoke(string operation, params Value[] values) =>
        await NativeRegistry.InvokeAsync("storage." + operation, values, context,
            static (_, _, _) => throw new InvalidOperationException("Storage must not invoke a callback."));
    var owner = (StorageValue)await Invoke("vector_empty", new TypeValue(TypeRef.String));
    var snapshot = (RangeValue)await Invoke("vector_snapshot", owner);
    int before = owner.GetHashCode();
    HashSet<StorageValue> identities = [owner];
    _ = await Invoke("vector_push", owner, new StringValue("first"));
    Check(owner.ElementType == TypeRef.String && snapshot.ItemType == TypeRef.String,
        "The carrier is established before the first write, including on an empty snapshot.");
    await using (var items = snapshot.Items.GetAsyncEnumerator())
        Check(!await items.MoveNextAsync(), "An existing snapshot must not observe a later append.");
    Check(owner.GetHashCode() == before && identities.Contains(owner),
        "Mutation must retain the owner's hash identity.");
    _ = await Invoke("vector_push", owner, new PathValue("./next", context.WorkingDirectory));
    Check(await Invoke("vector_get", owner, new NumberValue(1)) is StringValue { Data: "./next" },
        "The actual vector target owns the Path-to-String conversion.");
    var nullable = (StorageValue)await Invoke("vector_new", new RangeValue(new Value[] { Value.Null, new StringValue("first") }, TypeRef.String.WithNull));
    _ = await Invoke("vector_push", nullable, new PathValue("./next", context.WorkingDirectory));
    Check(await Invoke("vector_get", nullable, new NumberValue(2)) is StringValue { Data: "./next" },
        "Null admission must not remove the target's text conversion.");
}
async Task Numbers(string source, double[] expected)
{
    foreach (var stage in new[] { ArtifactStage.Relocatable, ArtifactStage.Executable })
    {
        Value value = await Execute(source, stage);
        Check(value is RangeValue, "Expected Range");
        List<double> actual = [];
        await foreach (Value item in ((RangeValue)value).Items) actual.Add(((NumberValue)item).Data);
        Check(actual.SequenceEqual(expected), source);
    }
}
void Reject(string source, string code)
{
    try { Compiler.Analyze(source, "hir-typing-error.msync"); }
    catch (CompileException error) { Check(error.Diagnostic.Code == code, error.Diagnostic.Message); return; }
    throw new InvalidOperationException("Expected " + code + ": " + source);
}
void RejectCarrier(string source)
{
    try { Compiler.Analyze(source, "hir-concept-storage-error.msync"); }
    catch (CompileException error)
    {
        Check(error.Diagnostic.Code is "E0203" or "E0214"
            && error.Diagnostic.Message.Contains("Number", StringComparison.Ordinal), error.Diagnostic.Message);
        return;
    }
    throw new InvalidOperationException("Expected a fixed Number carrier rejection: " + source);
}
void Check(bool condition, string description)
{
    checks++;
    if (!condition) throw new InvalidOperationException(description);
}
static bool HasRuntimeDependentMeta(HirModule module)
{
    bool[] visited = new bool[module.Nodes.Length];
    Stack<HirNodeId> pending = new(module.Functions.Where(function => !function.IsOpen).Select(function => function.Body));
    while (pending.TryPop(out var id))
    {
        if (visited[id.Value]) continue;
        visited[id.Value] = true;
        HirNode node = module.Node(id);
        if (node.Kind == HirKind.Call && module.Calls[node.Data].NativeName is
            "meta.is_invocable" or "meta.is_invocable_r" or "meta.invoke_result" or
            "meta.parameter_count" or "meta.parameter_types" or "meta.is_variadic" or
            "meta.has_member" or "meta.member_type" or "meta.member_names" or "meta.member_count") return true;
        foreach (var child in module.Children(node)) pending.Push(child);
        if (node.Kind == HirKind.Match)
            foreach (var arm in module.Matches[node.Data].Arms) pending.Push(arm.Value);
        if (node.Kind is HirKind.Closure or HirKind.TemplateClosure)
        {
            var captures = node.Kind == HirKind.Closure ? module.Closures[node.Data].Captures : module.TemplateClosures[node.Data].Captures;
            if (captures.IsDefault) continue;
            foreach (var capture in captures)
                if (capture.Mode == HirCaptureMode.Value) pending.Push(capture.SourceValue);
        }
    }
    return false;
}
