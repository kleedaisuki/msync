using System.Reflection;
using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

MethodInfo pass = typeof(Optimizer).Assembly.GetType("Msync.Core.TailCallOptimization")!
    .GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic)!;
int assertions = 0;
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
Module Transform(Module module) => Optimizer.FormBlocks(module with
{
    Functions = module.Functions.Select((function, index) =>
        (FunctionCode)pass.Invoke(null, [module, function, index])!).ToArray()
});
async Task<Value> Run(Module module) => await Interpreter.ExecuteAsync(module);

foreach ((string name, string source, Value expected) in new (string, string, Value)[]
{
    ("sum", "fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; sum(80, 0)", new NumberValue(3240)),
    ("parallel-arguments", "fn swap(n, left, right) { if n == 0 { left } else { swap(n - 1, right, left) } }; swap(11, \"left\", \"right\")", new StringValue("right")),
    ("capture-write", "let count = 0; fn walk(n) { count = count + 1; if n == 0 { count } else { walk(n - 1) } }; walk(20)", new NumberValue(21)),
    ("fresh-escaped-location", "let first = fn { 0 }; fn walk(n) { let snapshot = n; if n == 3 { first = fn { snapshot }; }; if n == 0 { null } else { walk(n - 1) } }; walk(3); first()", new NumberValue(3))
})
{
    Console.WriteLine("Tail case: " + name);
    Module original = Compiler.Compile(source, name, 0);
    Module optimized = Transform(original);
    Artifact.Validate(optimized);
    Require(await Run(original) == expected, name + ": original result");
    Require(await Run(optimized) == expected, name + ": optimized result");
    Require(optimized.Functions.Where((function, index) => !ReferenceEquals(function, original.Functions[index])).Any(), name + ": no tail call transformed");
    Require(Transform(optimized).Functions.Zip(optimized.Functions).All(pair => ReferenceEquals(pair.First, pair.Second)), name + ": pass not idempotent");
    using MemoryStream stream = new();
    Artifact.Write(optimized, stream); stream.Position = 0;
    Require(await Run(Artifact.Read(stream)) == expected, name + ": serialized result");
    for (var level = 1; level <= 3; level++)
    {
        Module integrated = Optimizer.Optimize(original, level);
        Artifact.Validate(integrated);
        Require(await Run(integrated) == expected, name + $": integrated O{level} result");
    }
}

Module deepOriginal = Compiler.Compile("fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; sum(100000, 0)", "deep-tail", 0);
Module deep = Transform(deepOriginal);
Require(await Run(deep) == new NumberValue(5_000_050_000), "100000-step frame reuse result");
Require(await Run(Optimizer.Optimize(deepOriginal, 3)) == new NumberValue(5_000_050_000), "100000-step integrated optimization result");

Module escaped = new([new NumberValue(3), new NumberValue(0), new NumberValue(1)],
[
    new("main", 0, 1,
    [new(OpCode.MakeClosure, 2), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1),
     new(OpCode.Constant, 0), new(OpCode.Call, 1, 2), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Call), new(OpCode.Return)], TypeRef.Number),
    new("walk", 1, 3,
    [new(OpCode.LoadLocal, 1), new(OpCode.InitializeLocal, 2), new(OpCode.LoadLocal, 1), new(OpCode.Constant, 0), new(OpCode.Equal),
     new(OpCode.JumpIfFalse, 9), new(OpCode.LoadCell, 2), new(OpCode.MakeClosure, 3, 1), new(OpCode.StoreLocal, 0),
     new(OpCode.LoadLocal, 1), new(OpCode.Constant, 1), new(OpCode.Equal), new(OpCode.JumpIfFalse, 15), new(OpCode.Null), new(OpCode.Return),
     new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.LoadLocal, 1), new(OpCode.Constant, 2), new(OpCode.Subtract),
     new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Null, 1, [TypeRef.Number]),
    new("unset", 0, 0, [new(OpCode.Constant, 1), new(OpCode.Return)], TypeRef.Number),
    new("snapshot", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number, 1)
], OptimizationLevel: 0);
Artifact.Validate(escaped);
Require(await Run(escaped) == new NumberValue(3), "Escaped original frame location");
Module escapedOptimized = Transform(escaped);
Artifact.Validate(escapedOptimized);
Require(await Run(escapedOptimized) == new NumberValue(3), "Tail frame reuse changed an escaped location");

Module effectful = Compiler.Compile("fn write(n) { \"bytes\" -> ./result; if n == 0 { true } else { write(n - 1) } }; write(2)", "owned-effects", 0);
Module retained = Transform(effectful);
Require(retained.Functions.Zip(effectful.Functions).All(pair => ReferenceEquals(pair.First, pair.Second)), "Effectful invocation lifetime was erased");

Module nativeOverride = new([new FunctionValue(-1, NativeName: "bool.identity")],
[
    new("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.True), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Bool),
    new("native-override", 1, 1, [new(OpCode.Constant, 0), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Bool])
], OptimizationLevel: 0);
Module nativeRetained = Transform(nativeOverride);
Require(ReferenceEquals(nativeRetained.Functions[1], nativeOverride.Functions[1]), "Call specialization erased a native identity");
Require(await Run(nativeRetained) == Value.True, "Native identity override result");

Module changingCapture = new([new NumberValue(10), new NumberValue(20), new NumberValue(1), new NumberValue(0)],
[
    new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.Constant, 2), new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Number),
    new("changing-capture", 1, 2,
    [new(OpCode.LoadLocal, 1), new(OpCode.Constant, 3), new(OpCode.Equal), new(OpCode.JumpIfFalse, 6), new(OpCode.LoadLocal, 0), new(OpCode.Return),
     new(OpCode.Constant, 1), new(OpCode.MakeClosure, 1, 1), new(OpCode.LoadLocal, 1), new(OpCode.Constant, 2), new(OpCode.Subtract),
     new(OpCode.Call, 1, 2), new(OpCode.Return)], TypeRef.Number, 1, [TypeRef.Number])
], OptimizationLevel: 0);
Require(await Run(changingCapture) == new NumberValue(20), "Changing callee capture baseline");
Module captureChanged = Transform(changingCapture);
Artifact.Validate(captureChanged);
Require(!ReferenceEquals(captureChanged.Functions[1], changingCapture.Functions[1]), "Changing closure was not transformed");
Require(await Run(captureChanged) == new NumberValue(20), "Tail jump retained the caller's capture instead of the callee's");

Module excessCaptures = new([new NumberValue(7), new NumberValue(8)],
[
    new("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.Null),
    new("small-frame", 0, 0,
    [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.MakeClosure, 2, 2), new(OpCode.Call, 0, 2), new(OpCode.Return)], TypeRef.Null),
    new("large-capture", 0, 2, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, 2)
], OptimizationLevel: 0);
Artifact.Validate(excessCaptures);
async Task<string> Failure(Module module)
{
    try { await Run(module); return "no failure"; }
    catch (Exception error) { return error.GetType().FullName + ":" + error.Message; }
}
string excessError = await Failure(excessCaptures);
Require(excessError.StartsWith(typeof(IndexOutOfRangeException).FullName!, StringComparison.Ordinal), "Excess capture O0 did not retain the frame-overflow trap");
Module excessRetained = Transform(excessCaptures);
Require(ReferenceEquals(excessRetained.Functions[1], excessCaptures.Functions[1]), "Tail transformation erased an excess-capture trap");
Require(await Failure(excessRetained) == excessError, "Tail-pass excess-capture failure changed");
Module excessIntegrated = Optimizer.Optimize(excessCaptures, 3);
Artifact.Validate(excessIntegrated);
Require(await Failure(excessIntegrated) == excessError, "Integrated O3 erased an excess-capture trap");
using MemoryStream legacyBytes = VersionTwoExcessCaptures();
try { Artifact.Read(legacyBytes); throw new InvalidOperationException("Version 2 artifact was accepted"); }
catch (MsyncException error)
{
    Require(error.Message.Contains("format 2", StringComparison.Ordinal), "Obsolete format diagnostic lost the actual version");
    Require(error.Message.Contains("Recompile", StringComparison.Ordinal), "Obsolete format diagnostic omitted migration guidance");
}
Console.WriteLine($"Tail call optimization: {assertions} contracts passed.");

static MemoryStream VersionTwoExcessCaptures()
{
    MemoryStream stream = new();
    using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
    {
        writer.Write("MSYNCBC\0"u8); writer.Write((ushort)2); writer.Write("0.8.0");
        writer.Write((byte)0); writer.Write(0); writer.Write(0); writer.Write(false);
        writer.Write(2); writer.Write((byte)3); writer.Write(7d); writer.Write((byte)3); writer.Write(8d);
        writer.Write(3);
        Function(writer, "main", 0, 0, [(13, 1, 0), (11, 0, 2), (12, 0, 0)]);
        Function(writer, "small-frame", 0, 0, [(0, 0, 0), (0, 1, 0), (13, 2, 2), (11, 0, 2), (12, 0, 0)]);
        Function(writer, "large-capture", 2, 2, [(1, 0, 0), (12, 0, 0)]);
    }
    stream.Position = 0;
    return stream;

    static void Function(BinaryWriter writer, string name, int locals, int captures,
        IReadOnlyList<(byte Op, int A, int B)> instructions)
    {
        writer.Write(name); writer.Write(0); writer.Write(locals); writer.Write(captures);
        writer.Write((ushort)1); writer.Write(false); writer.Write(false);
        writer.Write(instructions.Count);
        foreach (var (op, a, b) in instructions)
        {
            writer.Write(op); writer.Write(a); writer.Write(b); writer.Write(false); writer.Write(false);
        }
    }
}
