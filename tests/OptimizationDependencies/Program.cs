using Msync.Optimization;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args.Contains("--measure", StringComparer.Ordinal))
{
    foreach (bool changing in new[] { true, false })
    {
        Module input = Library(96, 16, changing);
        for (int warmup = 0; warmup < 3; warmup++) _ = Optimizer.Optimize(input, 2);
        double[] milliseconds = new double[9];
        long[] bytes = new long[9];
        Module result = input;
        for (int sample = 0; sample < milliseconds.Length; sample++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            result = Optimizer.Optimize(input, 2);
            milliseconds[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        Value value = await Interpreter.ExecuteAsync(result, new(Output: TextWriter.Null, Error: TextWriter.Null));
        if (value is not ScopeValue scope || scope.Count != (changing ? 113 : 96))
            throw new InvalidOperationException("Measured family changed");
        Console.WriteLine(JsonSerializer.Serialize(new { changing, milliseconds, bytes }));
        GC.KeepAlive(result);
    }
    return;
}

Type dependencies = typeof(Optimizer).Assembly.GetType("Msync.Optimization.OptimizationDependencies")!;
MethodInfo capture = dependencies.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!;
MethodInfo matches = dependencies.GetMethod("Matches", BindingFlags.Instance | BindingFlags.NonPublic)!;
int checks = 0;
FunctionCode literal = new("literal", 0, 0, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool);
FunctionCode target = new("target", 0, 0, [new(OpCode.False), new(OpCode.Return)], TypeRef.Bool);
FunctionCode call = new("caller", 0, 0, [new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.Bool);
Module original = new([], [call, target, literal], OptimizationLevel: 0);
InstructionEffect[] effects = [InstructionEffect.None, InstructionEffect.Read, InstructionEffect.Write];
object snapshot = Capture(call, original, effects);
Check(Matches(snapshot, original, effects), "Unchanged direct-call dependency missed");
Check(Matches(snapshot, original, [effects[0], effects[1], InstructionEffect.None]), "Unrelated callee invalidated a caller");
Check(!Matches(snapshot, original, [effects[0], InstructionEffect.None, effects[2]]), "Changed direct effect reused old proof");
Module changedLayout = original with { Functions = [call, target with { LocalCount = 1 }, literal] };
Check(!Matches(snapshot, changedLayout, effects), "Changed direct capture layout reused old proof");
Module changedBody = original with { Functions = [call, target with { Instructions = [new(OpCode.True), new(OpCode.Return)] }, literal] };
Check(!Matches(snapshot, changedBody, effects), "Changed body identity reused its former context");
Check(Matches(snapshot, original with { Constants = [new NumberValue(1)] }, effects), "Appending a constant invalidated a fixed callee context");
Check(Matches(snapshot, original with { Functions = [call, target, literal, literal] }, [.. effects, InstructionEffect.MayFail]), "An unrelated new function invalidated a caller");
FunctionCode native = new("native-read", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Return)], TypeRef.Bool,
    ParameterTypes: [TypeRef.Path]);
object independent = Capture(native, original, effects);
Check(Matches(independent, changedLayout, [InstructionEffect.Write, InstructionEffect.None, InstructionEffect.None]), "A native-only body depended on arbitrary function summaries");
FunctionCode unknown = call with { Instructions = [new(OpCode.Call), new(OpCode.Return)] };
Check(Matches(Capture(unknown, original, effects), changedLayout, [InstructionEffect.Write, InstructionEffect.None, InstructionEffect.None]),
    "An unresolved call acquired unsupported summary facts");
FunctionCode dispatch = call with { Instructions = [new(OpCode.Call, Text: "0:1;1:2"), new(OpCode.Return)] };
object dispatched = Capture(dispatch, original, effects);
Check(!Matches(dispatched, original, [effects[0], effects[1], InstructionEffect.None]), "A changed dispatch target reused its context");
Check(!Matches(dispatched, changedLayout, effects), "A changed dispatch target layout reused its context");

foreach (int level in new[] { 0, 2, 3 })
{
    Module raw = Library(12, 5);
    Module optimized = Optimizer.Optimize(raw, level);
    Artifact.Validate(optimized);
    foreach (Module image in Images(optimized))
    {
        Value value = await Interpreter.ExecuteAsync(image, new(Output: TextWriter.Null, Error: TextWriter.Null));
        Check(value is ScopeValue { Count: 18 }, "Exported callable family changed across optimization or serialization");
    }
    foreach (bool pending in new[] { false, true })
    {
        Module body = ObservableCall(pending);
        Module changed = Optimizer.Optimize(body, level);
        Artifact.Validate(changed);
        foreach (Module image in Images(changed))
            Check(await Interpreter.ExecuteAsync(image, new(Output: TextWriter.Null, Error: TextWriter.Null)) is NumberValue { Data: 7 },
                "Selected call result changed across optimization or serialization");
        Check(changed.Functions.SelectMany(f => f.Instructions).Count(i => i.Op == OpCode.NativeCall && i.Text == "fs.exists") == (level == 0 || pending ? 1 : 0),
            "Selected effect preservation differs from reachability");
    }
}
Console.WriteLine($"PASS {checks} optimization dependency contracts");

object Capture(FunctionCode function, Module module, IReadOnlyList<InstructionEffect> summaries) => capture.Invoke(null, [function, module, summaries])!;
bool Matches(object entry, Module module, IReadOnlyList<InstructionEffect> summaries) => (bool)matches.Invoke(entry, [module, summaries])!;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
static Module[] Images(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes); bytes.Position = 0;
    Module restored = Artifact.Read(bytes);
    Module executable = Binding.Bind(restored, new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    using MemoryStream bound = new(); Artifact.Write(executable, bound); bound.Position = 0;
    return [module, restored, Artifact.Read(bound)];
}
static Module ObservableCall(bool selected)
{
    Instruction[] code = [selected ? new(OpCode.True) : new(OpCode.False), new(OpCode.JumpIfFalse, 5), new(OpCode.Constant, 0),
        new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Pop), new(OpCode.Constant, 1), new(OpCode.Return)];
    return new Module([new PathValue("./.temp/optimization-dependencies/not-created"), new NumberValue(7)],
        [new("entry", 0, 0, [new(OpCode.Constant, 2), new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.Number),
         new("conditional-read", 0, 0, code, TypeRef.Number)]) with
    { Constants = [new PathValue("./.temp/optimization-dependencies/not-created"), new NumberValue(7), new FunctionValue(1)], OptimizationLevel = 0 };
}
static Module Library(int stableCount, int chainLength, bool changing = true)
{
    List<Value> constants = [new PathValue("./never-observed")];
    List<FunctionCode> functions = [];
    List<Instruction> entry = [];
    string[] names = new string[changing ? stableCount + chainLength + 1 : stableCount];
    functions.Add(new("entry", 0, 0, entry, TypeRef.Scope));
    for (int i = 0; i < stableCount; i++)
    {
        int id = functions.Count;
        functions.Add(new("stable" + i, 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Return)],
            TypeRef.Bool, ParameterTypes: [TypeRef.Path]));
        entry.Add(new(OpCode.MakeClosure, id)); names[i] = "stable" + i;
    }
    if (!changing)
    {
        entry.Add(new(OpCode.MakeScope, names.Length, Text: string.Join('\0', names))); entry.Add(new(OpCode.Return));
        return new(constants, functions, OptimizationLevel: 0);
    }
    int leaf = functions.Count;
    functions.Add(new("changing0", 0, 0,
        [new(OpCode.False), new(OpCode.JumpIfFalse, 6), new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"),
         new(OpCode.Pop), new(OpCode.Nop), new(OpCode.False), new(OpCode.Return)], TypeRef.Bool));
    entry.Add(new(OpCode.MakeClosure, leaf)); names[stableCount] = "changing0";
    for (int i = 1; i <= chainLength; i++)
    {
        int id = functions.Count;
        int constant = constants.Count; constants.Add(new FunctionValue(id - 1));
        functions.Add(new("changing" + i, 0, 0,
            [new(OpCode.Constant, constant), new(OpCode.Call, 0, id), new(OpCode.Pop), new(OpCode.False), new(OpCode.Return)], TypeRef.Bool));
        entry.Add(new(OpCode.MakeClosure, id)); names[stableCount + i] = "changing" + i;
    }
    entry.Add(new(OpCode.MakeScope, names.Length, Text: string.Join('\0', names))); entry.Add(new(OpCode.Return));
    return new(constants, functions, OptimizationLevel: 0);
}
