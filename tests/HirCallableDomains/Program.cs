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
await Number("separate-fields", first + "let a = scope { operation: fn { [1] } }; let b = scope { operation: fn { \"before\" } }; let pending = a::operation(); b::operation = fn { \"after\" }; first(pending)", 1);
await Number("value-not-cell-alias", first + "let operation = fn { [1] }; let independent = operation; let pending = operation(); independent = fn { [2] }; first(pending)", 1);
await Number("unreachable-write", first + "let operation = fn { [1] }; let pending = operation(); if false { operation = fn { 1 + true }; }; first(pending)", 1);
await Number("uncalled-writer", first + "let operation = fn { [1] }; fn unused() { operation = fn { 1 + true }; }; let pending = operation(); first(pending)", 1);
await Number("query-only-writer", first + "let operation = fn { [1] }; fn writer() { operation = fn { 1 + true }; 0 }; let query = decltype(writer()); let pending = operation(); first(pending)", 1);
await Number("future-type-denotation", first + "let type = Number; fn select(type: Type) { if type is Number { [1] } else { [2] } }; let pending = select(type); type = String; first(pending)", 2);
await Number("factory-separate-fields", first + "fn make() { scope { operation: fn { [1] } } }; let a = make(); let b = make(); let pending = a::operation(); b::operation = fn { [\"bad\"] }; first(pending) + 1", 2);
await Number("factory-parameter-writer", first + "fn make() { scope { operation: fn { [1] } } }; let a = make(); let b = make(); fn replace(object) { object::operation = fn { [\"bad\"] }; }; let pending = a::operation(); replace(b); first(pending) + 1", 2);
await Number("factory-captured-writer", first + "fn make() { scope { operation: fn { [1] }, replace: fn { operation = fn { [\"bad\"] }; } } }; let a = make(); let b = make(); let pending = a::operation(); b::replace(); first(pending) + 1", 2);
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
