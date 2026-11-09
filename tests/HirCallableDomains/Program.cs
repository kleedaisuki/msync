using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
const string first = "native fn first(values: Range) -> Number;";
await Number("binding", first + "let operation = fn { [1] }; let pending = operation(); operation = fn { [2] }; first(pending)", 2);
await Number("field", first + "let state = scope { operation: fn { [1] } }; let pending = state::operation(); state::operation = fn { [2] }; first(pending)", 2);
await Number("field-alias", first + "let state = scope { operation: fn { [1] } }; let alias = state; let pending = state::operation(); alias::operation = fn { [2] }; first(pending)", 2);
await Number("parameter-field-write", first + "let state = scope { operation: fn { [1] } }; fn replace(object) { object::operation = fn { [2] }; }; let pending = state::operation(); replace(state); first(pending)", 2);
await Number("captured-binding", first + "let operation = fn { [1] }; fn build() { operation() }; let pending = build(); operation = fn { [2] }; first(pending)", 2);
await Number("alternative-effects", first + "let state = scope { flag: false }; let operation = fn { state::flag = true; [1] }; let pending = operation(); if exists ./__missing_callable_domain__ { operation = fn { state::flag = false; [2] }; }; first(pending); if state::flag { 1 } else { 2 }", 1);
await Number("alternative-event-prefix", "let state = scope { before: false, selected: false }; fn run() { state::before = true; scope Once { value: 0 }; let operation = if exists ./__missing_callable_event__ { fn { state::selected = false; } } else { fn { state::selected = true; } }; operation(); }; run(); if state::before { if state::selected { 1 } else { 2 } } else { 0 }", 1);
await Number("separate-fields", first + "let a = scope { operation: fn { [1] } }; let b = scope { operation: fn { \"before\" } }; let pending = a::operation(); b::operation = fn { \"after\" }; first(pending)", 1);
await Number("value-not-cell-alias", first + "let operation = fn { [1] }; let independent = operation; let pending = operation(); independent = fn { [2] }; first(pending)", 1);
await Number("unreachable-write", first + "let operation = fn { [1] }; let pending = operation(); if false { operation = fn { 1 + true }; }; first(pending)", 1);
await Number("uncalled-writer", first + "let operation = fn { [1] }; fn unused() { operation = fn { 1 + true }; }; let pending = operation(); first(pending)", 1);
await Number("query-only-writer", first + "let operation = fn { [1] }; fn writer() { operation = fn { 1 + true }; 0 }; let query = decltype(writer()); let pending = operation(); first(pending)", 1);
await Number("future-type-denotation", first + "let type = Number; fn select(type: Type) { if type is Number { [1] } else { [2] } }; let pending = select(type); type = String; first(pending)", 2);
await Number("native-identity", "native fn buffer_new(initial: Range) -> Range; native fn buffer_size(items: Range) -> Number; let policy = scope { size: buffer_size }; fn forward(policy) { policy::size }; let selected = if exists ./__missing_native_identity__ { forward(policy) } else { policy::size }; selected(buffer_new([1]))", 1);
await Number("eager-variable-policy", first + "native fn materialize(values: Range) -> Range; let source = 1; let input = materialize([source]); fn identity(value) { value }; let selected = identity(input); source = 2; input = [source]; first(selected)", 1);
await Number("future-eager-callable-policy", first + "native fn materialize(values: Range) -> Range; let operation = fn { [1] }; let pending = operation(); operation = fn { materialize([2]) }; first(pending)", 2);
await Number("scope-bool-policy", first + "native fn materialize(values: Range) -> Range; let state = scope { flag: false }; let source = 1; fn choose(value: Number) { if state::flag { materialize([value]) } else { [value] } }; let pending = choose(source); source = 2; first(pending)", 2);
await Number("policy-bool-cache", first + "native fn materialize(values: Range) -> Range; let state = scope { flag: false }; let source = 1; fn choose(value: Number) { if state::flag { materialize([value]) } else { [value] } }; let pending = choose(source); state::flag = true; source = 2; let snapshot = choose(source); state::flag = false; source = 3; fn identity(value) { value }; let copied = identity(snapshot); snapshot = [4]; first(copied)", 2);
await Number("factory-separate-fields", first + "fn make() { scope { operation: fn { [1] } } }; let a = make(); let b = make(); let pending = a::operation(); b::operation = fn { [\"bad\"] }; first(pending) + 1", 2);
await Number("factory-parameter-writer", first + "fn make() { scope { operation: fn { [1] } } }; let a = make(); let b = make(); fn replace(object) { object::operation = fn { [\"bad\"] }; }; let pending = a::operation(); replace(b); first(pending) + 1", 2);
await Number("factory-captured-writer", first + "fn make() { scope { operation: fn { [1] }, replace: fn { operation = fn { [\"bad\"] }; } } }; let a = make(); let b = make(); let pending = a::operation(); b::replace(); first(pending) + 1", 2);
await Number("wrapped-future-domain", first + "fn make() { scope { operation: fn { [1] } } }; fn wrap(object: Scope) { scope { items: [object::operation()] } }; let a = make(); let b = make(); let holder_a = wrap(a); let holder_b = wrap(b); b::operation = fn { [2] }; first(first(holder_b::items))", 2);
await Number("copied-parameter-domain", first + "let operation = fn { [1] }; fn wrap(copy) { scope { items: [copy()] } }; let holder = wrap(operation); operation = fn { [2] }; first(first(holder::items))", 1);
Reject("fixed-captured-signature", first + "fn maker(value) { fn { [value] } }; let operation = maker(1); let pending = operation(); operation = maker(\"text\"); first(pending)");
await Text("path-string-contract", "let operation = fn { \"before\" }; operation(); operation = fn { ./after }; operation()", "./after");
await Number("contract-query-isolation", first + "let state = scope { writes: 0 }; let operation = fn { [1] }; let pending = operation(); operation = fn { state::writes = state::writes + 10; [2] }; if state::writes == 0 { first(pending); if state::writes == 10 { 1 } else { 0 } } else { -1 }", 1);
await Number("resource-return-eager", """
    native fn buffer_new(initial: Range) -> Range;
    native fn buffer_get(items: Range, index: Number) -> Scope;
    native fn buffer_push(items: Range, value: Number) -> Number;
    native fn buffer_size(items: Range) -> Number;
    let state = scope { changed: false };
    let rows = buffer_new([scope { children: buffer_new([1]) }]);
    fn choose(rows, index: Number) {
        state::changed = true;
        buffer_get(rows, index)::children
    };
    let selected = choose(rows, 0);
    if state::changed { buffer_push(selected, 2); buffer_size(selected) } else { 0 }
    """, 2);
await Number("nested-resource-policy-cache", """
    native fn buffer_new(initial: Range) -> Range;
    let state = scope { changed: false };
    fn select(row) { state::changed = true; row::items };
    let eager = scope { items: buffer_new([1]) };
    let lazy = scope { items: [2] };
    let first = select(eager);
    state::changed = false;
    let second = select(lazy);
    if state::changed { 0 } else { 1 }
    """, 1);
Console.WriteLine($"PASS {checks} callable write-domain journeys");

void Reject(string name, string source)
{
    if (args.Length != 0 && !args.Contains(name, StringComparer.Ordinal)) return;
    try { _ = Compiler.Analyze(source, name + ".msync"); }
    catch (CompileException error) when (error.Diagnostic.Code == "E0213")
    {
        checks++;
        if (error.Diagnostic.Related.Count == 0) throw new InvalidOperationException("Callable contract diagnostic must locate the original promise.");
        return;
    }
    throw new InvalidOperationException(name + ": expected the fixed callable protocol to reject the future target.");
}

async Task Text(string name, string source, string expected)
{
    if (args.Length != 0 && !args.Contains(name, StringComparer.Ordinal)) return;
    HirModule hir = Compiler.Analyze(source, name + ".msync");
    using MemoryStream bytes = new();
    HirArtifact.Write(hir, bytes);
    bytes.Position = 0;
    Value actual = await Interpreter.ExecuteAsync(HirLowerer.Lower(HirArtifact.Read(bytes), new(0)).Module);
    checks++;
    if (actual is not StringValue text || text.Data != expected)
        throw new InvalidOperationException($"{name}: expected a String '{expected}', received {actual.Type} '{actual}'.");
}

async Task Number(string name, string source, double expected)
{
    if (args.Length != 0 && !args.Contains(name, StringComparer.Ordinal)) return;
    HirModule hir = Compiler.Analyze(source, name + ".msync");
    using MemoryStream semantic = new();
    HirArtifact.Write(hir, semantic);
    semantic.Position = 0;
    Module code = HirLowerer.Lower(HirArtifact.Read(semantic), new(0)).Module;
    foreach (bool executable in new[] { false, true })
    {
        Module selected = executable ? Msync.Optimization.Binding.Bind(code,
            new(Environment.CurrentDirectory, Environment.CurrentDirectory)) : code;
        using MemoryStream bytes = new();
        Artifact.Write(selected, bytes);
        bytes.Position = 0;
        Value actual = await Interpreter.ExecuteAsync(Artifact.Read(bytes));
        checks++;
        if (actual is not NumberValue number || number.Data != expected)
            throw new InvalidOperationException($"{name}: expected {expected}, received {actual} (executable={executable})");
    }
}
