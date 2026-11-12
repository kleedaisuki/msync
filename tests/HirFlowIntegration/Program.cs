using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

int checks = 0;
string? filter = args.Length == 2 && args[0] == "--case" ? args[1] : null;
if (args.Contains("--btree", StringComparer.Ordinal))
{
    await Text("let tree = $std::containers::BTree::new(0, \"\"); $std::containers::BTree::insert(tree, 1, \"item\"); $std::containers::BTree::get(tree, 1)", "item");
    Console.WriteLine($"PASS {checks} BTree artifact journeys");
    return;
}
await Number("let changed = false; loop { if changed { break 7; } else { changed = true; } }", 7);
await Number("let object? = null; loop { if object is null { object = scope { item: 7 }; } else { break object::item; } }", 7);
await Number("let object? = null; for item in [1, 2] { if object is null { object = scope { item: item }; } else { object::item = 7; } }; if object is null { 0 } else { object::item }", 7);
await Number("let result = 0; for item in [1, 2, 3] { result = result + item; }; result", 6);
await Number("fn run() { let state? = null; loop { if state is null { state = scope { item: 9 }; } else { break state::item; } } }; run()", 9);
await Number("let changed = false; let operation = fn { 1 }; loop { if changed { break operation(); } else { operation = fn { 7 }; changed = true; } }", 7);
await Number("let changed = false; let value: Scope = scope { common: 1, first: true }; loop { if changed { break value::common; } else { value = scope { common: 8, second: false }; changed = true; } }", 8);
await Number("let state = scope { changed: false }; loop { if state::changed { break 7; } else { state::changed = true; } }", 7);
await Number("let state = scope { changed: false }; let alias = state; loop { if state::changed { break 8; } else { alias::changed = true; } }", 8);
await Number("let state = scope { inner: scope { changed: false } }; loop { if state::inner::changed { break 9; } else { state::inner::changed = true; } }", 9);
await Number("let changed = false; let value: Scope = scope { item: scope { first: true }, tag: 1 }; loop { if changed { break value::tag; } else { value = scope { item: value, tag: 7 }; changed = true; } }", 7);
await Number("let value: Scope = scope { item: 1, tag: 1 }; for item in [1, 2, 3] { value = scope { item: value, tag: 8 }; }; value::tag", 8);
await Number("let changed = false; let represented = Number; loop { if changed { if 3 is represented { break 1; } else { break 7; } } else { represented = String; changed = true; } }", 7);
await Number("let count = 0; for represented in [Number, String] { if represented is Number { count = count + 1; } }; count", 1);
await Number("fn check(state) { if 3 is state::represented { 1 } else { 7 } }; let state = scope { represented: Number, changed: false }; loop { if state::changed { break check(state); } else { state::represented = String; state::changed = true; } }", 7);
await Number("fn invoke(state) { state::operation() }; let state = scope { operation: fn { 1 }, changed: false }; loop { if state::changed { break invoke(state); } else { state::operation = fn { 7 }; state::changed = true; } }", 7);
const string conditionalIndexedCarrier = """
    fn conditional_indexed_carrier() {
        scope {
            nodes: $std::storage::vector_new([scope { payload: [7], next: 0 }]),
            index: 1
        }
    };
    fn conditional_indexed_read(self) {
        let index = self::index;
        let total = 0;
        loop {
            if index == 0 { break; };
            let row? = indexed_row(self);
            total = total + $std::ranges::front(row::payload);
            index = row::next;
        };
        total
    };
    if true {
        let instance = conditional_indexed_carrier();
        conditional_indexed_read(instance)
    } else { 0 }
    """;
await Number("fn indexed_row(self) { $std::storage::vector_get(self::nodes, 0) }; "
    + conditionalIndexedCarrier.Replace("let row? =", "let row =", StringComparison.Ordinal), 7);
Reject("fn indexed_row(self) { if $std::io::read_line() is null { null } else { $std::storage::vector_get(self::nodes, 0) } }; "
    + conditionalIndexedCarrier, "E0201");
await Number("let changed = false; let value: Scope = scope { items: $std::storage::vector_new([4]), first: true }; loop { if changed { break $std::storage::vector_get(value::items, 0); } else { value = scope { items: value::items, second: false }; changed = true; } }", 4);
await Number("let old = $std::storage::vector_new([1]); let changed = false; let value: Scope = scope { items: old, first: true }; loop { if changed { break $std::storage::vector_get(value::items, 0) + $std::storage::vector_get(old, 0); } else { value = scope { items: $std::storage::vector_new([7]), second: false }; changed = true; } }", 8);
const string indexedBranch = "native fn is_nan(value: Number) -> Bool;";
await Number(indexedBranch + "let items = $std::storage::vector_empty(Number); if is_nan(0) { $std::storage::vector_push(items, 1); } else { $std::storage::vector_push(items, 2); }; $std::storage::vector_get(items, 0)", 2);
await Number(indexedBranch + "if decltype({ let items = $std::storage::vector_empty(Number); if is_nan(0) { $std::storage::vector_push(items, 1); } else { $std::storage::vector_push(items, 2); }; $std::storage::vector_get(items, 0) }) is Number { 7 } else { 1 }", 7);
Reject(indexedBranch + "let items = $std::storage::vector_empty(Number); if is_nan(0) { $std::storage::vector_push(items, 1); } else { $std::storage::vector_push(items, true); }; $std::storage::vector_push(items, 2)", "E0203");
await Number("native fn is_nan(value: Number) -> Bool; let value?: Scope = null; let changed = false; loop { if value is null { if changed { break 7; } else { value = if is_nan(0) { scope { first: 1 } } else { scope { second: 2 } }; changed = true; } } else { value = null; } }", 7);
const string snapshotCalls = "native fn materialize(values: Range) -> Range; native fn first(values: Range) -> Number; native fn all(values: Range) -> Range;";
await Number(snapshotCalls + "let values = [1]; fn freeze_field() { let state = scope { items: values }; state::items = materialize(values); state::items }; let saved = freeze_field(); values = [9]; first(saved)", 1);
await Number(snapshotCalls + "let values = [1]; fn freeze_identity() { let saved = materialize(values); all(saved) }; let saved = freeze_identity(); values = [9]; first(saved)", 1);
await Number("let changed = false; fn conditional_cell_write(take: Bool) { if take { changed = true; }; }; conditional_cell_write(true); if changed { 7 } else { 1 }", 7);
await Number("let value?: Number = null; fn conditional_nullable_write(take: Bool) { if take { value = 8; }; }; conditional_nullable_write(true); if value is null { 1 } else { value }", 8);
Console.WriteLine($"PASS {checks} structured-flow artifact journeys");

async Task Number(string source, double expected)
{
    if (filter is not null && !source.Contains(filter, StringComparison.Ordinal)) return;
    var hir = Compiler.Analyze(source, "flow-journey.msync");
    foreach (int optimization in new[] { 0, 2 })
    {
        Module module = Optimizer.Optimize(
            HirLowerer.Lower(hir, new(OptimizationLevel: optimization)).Module, optimization);
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        Value value = await Interpreter.ExecuteAsync(Artifact.Read(stream));
        checks++;
        if (value is not NumberValue actual || actual.Data != expected)
            throw new InvalidOperationException($"Expected {expected}, got {value}: {source}");
    }
}

async Task Text(string source, string expected)
{
    var hir = Compiler.Analyze(source, "flow-tree.msync");
    foreach (int optimization in new[] { 0, 2 })
    {
        Module module = Optimizer.Optimize(
            HirLowerer.Lower(hir, new(OptimizationLevel: optimization)).Module, optimization);
        Value value = await Interpreter.ExecuteAsync(module);
        checks++;
        if (value is not StringValue actual || actual.Data != expected)
            throw new InvalidOperationException($"Expected '{expected}', got {value}");
    }
}

void Reject(string source, string code)
{
    if (filter is not null && !source.Contains(filter, StringComparison.Ordinal)) return;
    try { Compiler.Analyze(source, "flow-rejection.msync"); }
    catch (CompileException error)
    {
        checks++;
        if (error.Diagnostic.Code != code) throw new InvalidOperationException(error.Diagnostic.Message);
        return;
    }
    throw new InvalidOperationException("Expected " + code + ": " + source);
}
