using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

if (args is ["--allocations", var path])
{
    SemanticAllocationProbe.Run(path);
    return;
}

int checks = TransactionChecks.Run();
BindingRollback();
await Query("decltype(match true { true => 1, _ => 2 }); 7", 7);
await Query("decltype(for item in [1] { item }); 7", 7);
await Query("decltype(scope { item: 1 }); 7", 7);
await Query("fn recurse(value: Number) -> Number { recurse(value) }; decltype(recurse(1)); 7", 7);
await Query("let value = 1; decltype({ value = 9; value }); value", 1);
await Query("let value = 1; decltype(if exists ./query_only { value = 3 } else { value = 4 }); value", 1);
await Query("let value = scope { item: 1 }; decltype({ value::item = 9; value }); value::item", 1, emittedScopes: 1);

SemanticContext parent = new(new(0));
HirLocationId location = new(3);
HirTypeId first = new(1), second = new(2);
parent.Storage[location] = first;
SemanticContext child = parent.QueryFork();
Check(child.Output is null, "A query must not own executable output.");
Check(child.Fork().Output is null, "A flow fork cannot acquire output from a query.");
Check(child.Storage[location] == first, "A query borrows current facts.");
child.Storage[location] = second;
Check(parent.Storage[location] == first, "Query writes must stay in the overlay.");
Check(child.Storage[location] == second, "Query writes shadow borrowed facts.");
long small = QueryAllocation(10);
long large = QueryAllocation(100_000);
Check(large <= small + 128, $"Query allocation grew with borrowed world: {small} -> {large} bytes.");
Console.WriteLine($"Query context allocation: 10 bindings {small} B; 100000 bindings {large} B");
Console.WriteLine($"PASS {checks} query ownership/source-free execution checks");

async Task Query(string source, double expected, int emittedScopes = 0)
{
    BoundProgram bound = new SourceBinder().Bind(source, "query-ownership.msync");
    int functions = bound.Hir.Functions.Count;
    int locations = bound.Hir.Locations.Count;
    int matches = bound.Hir.Matches.Count;
    int fors = bound.Hir.Fors.Count;
    int scopes = bound.Hir.Scopes.Count;
    int ranges = bound.Hir.Ranges.Count;
    int calls = bound.Hir.Calls.Count;
    int closures = bound.Hir.Closures.Count;
    int adaptations = bound.Hir.Adaptations.Count;
    HirModule analyzed = new SemanticAnalyzer(bound).Analyze();
    Check(bound.Hir.Functions.Count == functions, "Query published a function.");
    Check(bound.Hir.Locations.Count == locations, "Query published a location.");
    Check(bound.Hir.Matches.Count == matches, "Query published Match payload.");
    Check(bound.Hir.Fors.Count == fors, "Query published For payload.");
    Check(bound.Hir.Scopes.Count == scopes + emittedScopes, "Query published Scope payload.");
    Check(bound.Hir.Ranges.Count == ranges, "Query published Range payload.");
    Check(bound.Hir.Calls.Count == calls, "Query published Call payload.");
    Check(bound.Hir.Closures.Count == closures, "Query published Closure payload.");
    Check(bound.Hir.Adaptations.Count == adaptations, "Query published callback adaptation.");
    using MemoryStream bytes = new();
    HirArtifact.Write(analyzed, bytes);
    bytes.Position = 0;
    HirModule restored = HirArtifact.Read(bytes);
    Module lowered = HirLowerer.Lower(restored, new(OptimizationLevel: 0)).Module;
    Value result = await Interpreter.ExecuteAsync(lowered);
    Check(result is NumberValue number && number.Data == expected,
        $"Unexpected result for {source}: {result}");
}

long QueryAllocation(int size)
{
    SemanticContext context = new(new(0));
    for (int index = 0; index < size; index++) context.Storage[new(index)] = new(index);
    _ = context.QueryFork();
    long start = GC.GetAllocatedBytesForCurrentThread();
    SemanticContext query = context.QueryFork();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
    GC.KeepAlive(query);
    return allocated;
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void BindingRollback()
{
    BoundProgram accepted = new SourceBinder(isSession: true).Bind("let first = 1; first", "<repl>");
    _ = new SemanticAnalyzer(accepted).Analyze();
    int sources = accepted.Hir.Sources.Count;
    int types = accepted.Hir.Types.Count;
    int functions = accepted.Hir.Functions.Count;
    int locations = accepted.Hir.Locations.Count;
    int calls = accepted.Hir.Calls.Count;
    HirFunction entry = accepted.Hir.Functions[accepted.Entry.Value];
    using (accepted.Hir.BeginTransaction())
    {
        BoundProgram pending = new SourceBinder(accepted).Bind("fn second(value) { value }; second(\"new\")", "<repl>");
        _ = new SemanticAnalyzer(pending).Analyze();
        Check(accepted.Hir.Exports.Any(export => export.Name == "second"), "Pending source generation was not bound.");
    }
    Check(accepted.Hir.Functions[accepted.Entry.Value] == entry, "Editor/failed generation changed accepted entry.");
    Check(accepted.Hir.Sources.Count == sources && accepted.Hir.Types.Count == types, "Pending generation leaked source/types.");
    Check(accepted.Hir.Functions.Count == functions && accepted.Hir.Locations.Count == locations
        && accepted.Hir.Calls.Count == calls, "Pending generation leaked callable executable tables.");
    Check(accepted.Hir.Exports.All(export => export.Name != "second"), "Pending generation leaked lexical exports.");
}
