using System.Reflection;
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
    ("capture-write", "let count = 0; fn walk(n) { count = count + 1; if n == 0 { count } else { walk(n - 1) } }; walk(20)", new NumberValue(21))
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
}

Module deep = Transform(Compiler.Compile("fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; sum(100000, 0)", "deep-tail", 0));
Require(await Run(deep) == new NumberValue(5_000_050_000), "100000-step frame reuse result");

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
]);
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
]);
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
]);
Require(await Run(changingCapture) == new NumberValue(20), "Changing callee capture baseline");
Module captureChanged = Transform(changingCapture);
Artifact.Validate(captureChanged);
Require(!ReferenceEquals(captureChanged.Functions[1], changingCapture.Functions[1]), "Changing closure was not transformed");
Require(await Run(captureChanged) == new NumberValue(20), "Tail jump retained the caller's capture instead of the callee's");
if (args.Contains("--recursive-capture-source", StringComparer.Ordinal))
{
    Module recursiveCapture = Compiler.Compile("let first = fn { 0 }; fn walk(n) { let snapshot = n; if n == 3 { first = fn { snapshot }; }; if n == 0 { null } else { walk(n - 1) } }; walk(3); first()", "fresh-escaped-location", 0);
    Require(await Run(Transform(recursiveCapture)) == new NumberValue(3), "Recursive mutable callable source frame location");
}
Console.WriteLine($"Tail call optimization: {assertions} contracts passed.");
