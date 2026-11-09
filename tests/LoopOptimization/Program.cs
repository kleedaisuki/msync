using Msync.Optimization;
using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

var optimize = typeof(Optimizer).Assembly.GetType("Msync.Optimization.LoopOptimization")!.GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
var root = Path.Combine(Environment.CurrentDirectory, ".temp", "loop-optimization-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assertions = 0;
try
{
    foreach (var source in new[]
    {
        "let value = 0; for item in [] { value = 19; }; value",
        "let value = 0; for item in [1] { value = value + item; }; value",
        "let value = 0; for item in [1, 2, 3, 4] { value = value + item; }; value",
        "let value = 9007199254740992; for item in [1, 1, 1] { value = value + item; }; value",
        "let value = 0; for item in [1, 2, 3] { if item == 2 { value = value + 8; } else { value = value + item; }; }; value",
        "let value = 0; for outer in [1, 2] { for inner in [3, 4] { value = value + outer * inner; }; }; value",
        "let first = fn { \"unset\" }; let last = fn { \"unset\" }; for item in [\"first\", \"last\"] { let snapshot = item; if item == \"first\" { first = fn { snapshot }; } else { last = fn { item }; }; }; first() == \"first\" and last() == \"last\"",
        "for file in [./a, ./b, ./c] { \"written\" -> file; }; true",
        "fn total() { for item in [1, 2, 3] { if item == 2 { return 17; }; }; 29 }; total()"
    })
    {
        var baseline = Compiler.Compile(source, "loop-oracle", 0);
        var constants = baseline.Constants.ToList();
        var changed = baseline with { Functions = baseline.Functions.Select(function => Rewrite(baseline, function, constants)).ToArray(), Constants = constants };
        Artifact.Validate(changed);
        var expected = await Outcome(baseline);
        var actual = await Outcome(changed);
        Require(expected == actual, source + "\n" + expected + " != " + actual);
        foreach (var stage in new[] { RoundTrip(changed), RoundTrip(Binding.Bind(changed, new(root, root))) })
            Require(await Outcome(stage) == expected, "Artifact loop result differs");
        for (var level = 1; level <= 3; level++)
        {
            var integrated = Optimizer.Optimize(baseline, level);
            Artifact.Validate(integrated);
            Require(await Outcome(integrated) == expected, "Integrated O" + level + " loop result differs: " + source);
        }
        Require(changed.Functions.Sum(function => function.Instructions.Count(i => i.Op == OpCode.IteratorNext)) == 0, "Small finite literal loop not expanded: " + source + "\n" + string.Join("\n", baseline.Functions.SelectMany(function => function.Instructions.Select((instruction, pc) => pc + ": " + instruction))));
    }

    // Invariants are proven from stable Number parameter types; the range is
    // unknown until invocation, so the pass cannot replace this by literal unrolling.
    var invariant = DynamicLoop();
    var invariantConstants = invariant.Constants.ToList();
    var hoisted = invariant with { Functions = invariant.Functions.Select(function => Rewrite(invariant, function, invariantConstants)).ToArray() };
    Artifact.Validate(hoisted);
    var body = hoisted.Functions[1].Instructions;
    var header = body.ToList().FindIndex(instruction => instruction.Op == OpCode.IteratorNext);
    Require(body.Take(header).Any(instruction => instruction.Op == OpCode.Add), "Total numeric invariant was not hoisted to preheader");
    Require(!body.Skip(header).Any(instruction => instruction.Op == OpCode.Add), "Invariant remained in loop body");
    Require(await Outcome(invariant) == await Outcome(hoisted), "LICM changed actual result");
    Require(await Outcome(hoisted) == "9", "LICM result did not use the invariant value");
    var empty = DynamicLoop(true);
    var emptyConstants = empty.Constants.ToList();
    var emptyHoisted = empty with { Functions = empty.Functions.Select(function => Rewrite(empty, function, emptyConstants)).ToArray() };
    Require(await Outcome(emptyHoisted) == "3", "LICM hoisted the per-iteration store across an empty range");
    var changing = invariant with
    {
        Functions = [invariant.Functions[0], invariant.Functions[1] with
    {
        Instructions = invariant.Functions[1].Instructions.Select((instruction, pc) => pc == 10 ? instruction with { A = 1 }
            : pc == 16 ? instruction with { A = 1 } : instruction).ToArray()
    }]
    };
    var changingConstants = changing.Constants.ToList();
    var notHoisted = changing with { Functions = changing.Functions.Select(function => Rewrite(changing, function, changingConstants)).ToArray() };
    Require(notHoisted.Functions[1].Instructions.Skip(6).Any(instruction => instruction.Op == OpCode.Add), "Loop-carried write treated as invariant");
    Require(await Outcome(notHoisted) == "11", "Loop-carried write order changed");

    var many = Compiler.Compile("let total = 0; for item in [" + string.Join(',', Enumerable.Range(1, 200)) + "] { total = total + item; }; total", "large-literal", 0);
    var manyConstants = many.Constants.ToList();
    var kept = many with { Functions = many.Functions.Select(function => Rewrite(many, function, manyConstants)).ToArray() };
    Require(kept.Functions.Any(function => function.Instructions.Any(instruction => instruction.Op == OpCode.IteratorNext)), "Code-size policy expanded a large table disproportionately");
    Require(await Outcome(many) == await Outcome(kept), "Retained large loop changed result");

    // A potentially failing field read must remain conditional on iteration.
    var trap = new Module([new NumberValue(17)], [new("dead-trap", 0, 2,
        [new(OpCode.MakeRange, 0), new(OpCode.GetIterator), new(OpCode.StoreLocal, 0), new(OpCode.IteratorNext, 0, 9),
         new(OpCode.InitializeLocal, 1), new(OpCode.MakeScope, 0, Text: ""), new(OpCode.GetMember, Text: "missing"),
         new(OpCode.Pop), new(OpCode.Jump, 3), new(OpCode.Nop), new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
    var trapConstants = trap.Constants.ToList();
    var noTrap = trap with { Functions = trap.Functions.Select(function => Rewrite(trap, function, trapConstants)).ToArray() };
    Require(await Outcome(noTrap) == "17", "Empty loop speculated a selected error");
    Require(noTrap.Functions.All(function => function.Instructions.All(instruction => instruction.Op != OpCode.GetMember)), "Known empty loop retained dead field read");
    Console.WriteLine($"Loop optimization: {assertions} contracts passed.");
    if (args.Contains("--performance", StringComparer.Ordinal)) await LoopPerformance.RunAsync();
}
finally { Directory.Delete(root, true); }

FunctionCode Rewrite(Module module, FunctionCode function, List<Value> constants) =>
    (FunctionCode)optimize.Invoke(null, [module, function, constants, 3])!;

void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task<string> Outcome(Module module)
{
    foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
    var value = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root));
    return value + string.Concat(Directory.EnumerateFiles(root).Order().Select(file => "|" + Path.GetFileName(file) + ":" + File.ReadAllText(file)));
}

Module RoundTrip(Module module)
{
    using var stream = new MemoryStream();
    Artifact.Write(module, stream); stream.Position = 0; return Artifact.Read(stream);
}

Module DynamicLoop(bool empty = false) => new([new NumberValue(2), new NumberValue(3), new NumberValue(7), new FunctionValue(1)],
[
    new("main", 0, 0,
    empty ? [new(OpCode.Constant, 3), new(OpCode.MakeRange, 0), new(OpCode.Constant, 2), new(OpCode.Call, 2, 2), new(OpCode.Return)]
        : [new(OpCode.Constant, 3), new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.MakeRange, 2), new(OpCode.Constant, 2), new(OpCode.Call, 2, 2), new(OpCode.Return)], TypeRef.Number),
    new("invariant", 2, 5,
    [new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 4), new(OpCode.LoadLocal, 0), new(OpCode.GetIterator), new(OpCode.StoreLocal, 2), new(OpCode.IteratorNext, 2, 16),
     new(OpCode.InitializeLocal, 3), new(OpCode.LoadLocal, 1), new(OpCode.Constant, 0), new(OpCode.Add), new(OpCode.StoreLocal, 4),
     new(OpCode.LoadLocal, 4), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Pop), new(OpCode.Jump, 5), new(OpCode.LoadLocal, 4), new(OpCode.Return)],
     TypeRef.Number, ParameterTypes: [new(TypeKind.Range, TypeRef.Number), TypeRef.Number])
], OptimizationLevel: 0);
