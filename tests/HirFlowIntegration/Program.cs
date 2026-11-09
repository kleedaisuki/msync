using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
await Number("let changed = false; loop { if changed { break 7; } else { changed = true; } }", 7);
await Number("let object? = null; loop { if object is null { object = scope { item: 7 }; } else { break object::item; } }", 7);
await Number("let object? = null; for item in [1, 2] { if object is null { object = scope { item: item }; } else { object::item = 7; } }; if object is null { 0 } else { object::item }", 7);
await Number("let result = 0; for item in [1, 2, 3] { result = result + item; }; result", 6);
await Number("fn run() { let state? = null; loop { if state is null { state = scope { item: 9 }; } else { break state::item; } } }; run()", 9);
await Number("let changed = false; let operation = fn { 1 }; loop { if changed { break operation(); } else { operation = fn { 7 }; changed = true; } }", 7);
await Number("let changed = false; let value: Scope = scope { common: 1, first: true }; loop { if changed { break value::common; } else { value = scope { common: 8, second: false }; changed = true; } }", 8);
Console.WriteLine($"PASS {checks} structured-flow artifact journeys");

async Task Number(string source, double expected)
{
    var hir = Compiler.Analyze(source, "flow-journey.msync");
    foreach (int optimization in new[] { 0, 2 })
    {
        Module module = HirLowerer.Lower(hir, new(OptimizationLevel: optimization)).Module;
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        Value value = await Interpreter.ExecuteAsync(Artifact.Read(stream));
        checks++;
        if (value is not NumberValue actual || actual.Data != expected)
            throw new InvalidOperationException($"Expected {expected}, got {value}: {source}");
    }
}
