using System.Reflection;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

MethodInfo method = typeof(Optimizer).Assembly.GetType("Msync.Core.LocalSlotCompaction")!
    .GetMethod("Compact", BindingFlags.Static | BindingFlags.NonPublic)!;
Module Compact(Module module) => (Module)method.Invoke(null, [module])!;
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

Module original = new([new StringValue("before"), new StringValue("after")],
[
    new("main", 0, 1000,
    [
        new(OpCode.Constant, 0), new(OpCode.StoreLocal, 998),
        new(OpCode.LoadCell, 998), new(OpCode.MakeClosure, 1, 1), new(OpCode.StoreLocal, 999),
        new(OpCode.Constant, 1), new(OpCode.StoreLocal, 998),
        new(OpCode.LoadLocal, 999), new(OpCode.Call, 0, 2), new(OpCode.Return)
    ], TypeRef.String),
    new("read", 0, 700, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String, CaptureCount: 1)
], OptimizationLevel: 0);
Module compact = Compact(original);
Check(compact.Functions[0].LocalCount == 2 && compact.Functions[1].LocalCount == 1, "Unused storage survived compaction");
Check(compact.Functions[0].Instructions.Count == original.Functions[0].Instructions.Count, "Slot compaction changed control flow");
Check(compact.Functions[0].Instructions[2].A == 0 && compact.Functions[0].Instructions[4].A == 1, "Storage aliases were not relocated consistently");
foreach (Module program in new[] { original, compact })
{
    Artifact.Validate(program);
    Check(await Interpreter.ExecuteAsync(program) is StringValue { Data: "after" }, "Closure no longer reads the shared live location");
    using MemoryStream bytes = new();
    Artifact.Write(program, bytes); bytes.Position = 0;
    Check(await Interpreter.ExecuteAsync(Artifact.Read(bytes)) is StringValue { Data: "after" }, "Serialization changed relocated capture storage");
}

Module abi = new([], [new("identity", 2, 1000,
    [new(OpCode.LoadLocal, 2), new(OpCode.StoreLocal, 900), new(OpCode.LoadStorage, 900), new(OpCode.Return)],
    TypeRef.Any, CaptureCount: 1)], OptimizationLevel: 0);
FunctionCode identity = Compact(abi).Functions[0];
Check(identity.LocalCount == 4 && identity.Instructions[0].A == 2 && identity.Instructions[1].A == 3,
    "Capture/parameter ABI prefix changed");

Module loop = new([new NumberValue(1)], [new("loop", 0, 800,
[
    new(OpCode.Constant, 0), new(OpCode.MakeRange, 1), new(OpCode.GetIterator), new(OpCode.StoreLocal, 799),
    new(OpCode.IteratorNext, 799, 7), new(OpCode.Pop), new(OpCode.Jump, 4), new(OpCode.Null), new(OpCode.Return)
], TypeRef.Null)], OptimizationLevel: 0);
Module compactLoop = Compact(loop);
Check(compactLoop.Functions[0].LocalCount == 1 && compactLoop.Functions[0].Instructions[4] is { A: 0, B: 7 },
    "Iterator storage or branch target was incorrectly relocated");
Artifact.Validate(compactLoop);
Check(await Interpreter.ExecuteAsync(compactLoop) is NullValue, "Relocated iterator changed execution");

Module persistent = Compact(original with { PreserveLocals = true });
Check(ReferenceEquals(persistent.Functions[0], original.Functions[0]), "REPL externally owned slots were relocated");
Check(persistent.Functions[1].LocalCount == 1, "Private function frame remained oversized in a persistent session");
Module empty = Compact(new([], [new("empty", 0, 4096, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)]));
Check(empty.Functions[0].LocalCount == 0, "Dead-only function retains allocation-sized local array");
Module again = Compact(compact);
Check(again.Functions.Zip(compact.Functions).All(pair => ReferenceEquals(pair.First, pair.Second)), "Slot compaction is not idempotent");
foreach (int level in new[] { 1, 2, 3 })
{
    Module program = Optimizer.Optimize(original, level);
    Check(await Interpreter.ExecuteAsync(program) is StringValue { Data: "after" }, $"O{level} pipeline changed relocated mutable captures");
    Module dead = Optimizer.Optimize(new([new NumberValue(1)], [new("dead", 0, 4096,
        [new(OpCode.Constant, 0), new(OpCode.StoreLocal, 4095), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)]), level);
    Check(dead.Functions[0].LocalCount == 0, $"O{level} production pipeline retains dead frame storage");
}
Console.WriteLine($"PASS: {assertions} local-slot ABI, capture, iterator, serialization and persistent-session contracts");
