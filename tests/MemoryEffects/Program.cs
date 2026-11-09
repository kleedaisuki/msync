using System.Collections;
using System.Reflection;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;
using Module = Msync.Core.Module;

MethodInfo analyze = typeof(Optimizer).Assembly.GetType("Msync.Core.MemoryEffects")!
    .GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static)!;
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
object[] Summaries(Module module) => ((IEnumerable)analyze.Invoke(null, [module])!).Cast<object>().ToArray();
object? Property(object item, string name) => item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item);
string[] Accesses(object summary, string name) => ((IEnumerable)Property(summary, name)!).Cast<object>().Select(item => item.ToString()!).Order(StringComparer.Ordinal).ToArray();
bool Flag(object summary, string name) => (bool)Property(summary, name)!;
void Has(object summary, string category, params string[] expected)
{
    var actual = Accesses(summary, category);
    Check(actual.SequenceEqual(expected.Order(StringComparer.Ordinal)), $"{category}: expected [{string.Join(',', expected)}], received [{string.Join(',', actual)}]");
}
FunctionCode Function(string name, int parameters, int locals, TypeRef result, Instruction[] code, int captures = 0, TypeRef[]? types = null) =>
    new(name, parameters, locals, code, result, captures, types);
Module Program(params FunctionCode[] functions) => new([], functions, OptimizationLevel: 0);
var get = Function("get", 1, 1, TypeRef.Number, [new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "x"), new(OpCode.Return)], types: [TypeRef.Scope]);
var set = Function("set", 1, 1, TypeRef.Null, [new(OpCode.LoadLocal, 0), new(OpCode.True), new(OpCode.SetMember, Text: "y"), new(OpCode.Return)], types: [TypeRef.Scope]);
object summary = Summaries(Program(get))[0];
Has(summary, "Reads", "Parameter[0]::x"); Has(summary, "Writes"); Has(summary, "ReturnAliases");
Check(!Flag(summary, "UnknownReads") && !Flag(summary, "UnknownWrites"), "Known field read became global memory effect");
summary = Summaries(Program(set))[0]; Has(summary, "Writes", "Parameter[0]::y"); Has(summary, "Reads");

var relay = Function("relay", 2, 2, TypeRef.Number,
    [new(OpCode.MakeClosure, 2), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 3), new(OpCode.Pop),
     new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "x"), new(OpCode.Return)], types: [TypeRef.Scope, TypeRef.Scope]);
summary = Summaries(Program(get, relay, set))[1];
Has(summary, "Reads", "Parameter[0]::x"); Has(summary, "Writes", "Parameter[1]::y");
Check(!Flag(summary, "UnknownWrites"), "Known unrelated callee clobbers global memory");

var nested = Function("nested", 1, 1, TypeRef.Scope,
    [new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "child"), new(OpCode.GetMember, Text: "next"), new(OpCode.Return)], types: [TypeRef.Scope]);
summary = Summaries(Program(nested))[0];
Has(summary, "Reads", "Parameter[0]::child", "Parameter[0]::child::next"); Has(summary, "ReturnAliases", "Parameter[0]::child::next");

var capturedSet = Function("captured-field", 0, 1, TypeRef.Null,
    [new(OpCode.LoadLocal, 0), new(OpCode.True), new(OpCode.SetMember, Text: "x"), new(OpCode.Return)], captures: 1);
summary = Summaries(Program(capturedSet))[0];
Has(summary, "Reads", "Capture[0]"); Has(summary, "Writes", "Capture[0]::x");
var capturedRebind = Function("captured-rebind", 1, 2, TypeRef.Null,
    [new(OpCode.LoadLocal, 1), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], captures: 1, types: [TypeRef.Scope]);
summary = Summaries(Program(capturedRebind))[0]; Has(summary, "Writes", "Capture[0]");

var closureRelay = Function("closure-relay", 1, 1, TypeRef.Null,
    [new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.Call, 0, 2), new(OpCode.Return)], types: [TypeRef.Scope]);
summary = Summaries(Program(closureRelay, capturedSet))[0]; Has(summary, "Writes", "Parameter[0]::x");
Check(!Flag(summary, "UnknownWrites"), "Private capture cell lost its pointee identity");

var opaqueCapture = Function("opaque-capture", 1, 2, TypeRef.Null,
    [new(OpCode.LoadLocal, 0), new(OpCode.GetCapture, 0), new(OpCode.InitializeLocal, 1),
     new(OpCode.True), new(OpCode.StoreLocal, 1), new(OpCode.Null), new(OpCode.Return)], types: [TypeRef.Function]);
summary = Summaries(Program(opaqueCapture))[0]; Check(Flag(summary, "UnknownWrites"), "Unknown raw captured cell write was ignored");

var io = Function("io", 1, 1, TypeRef.Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Return)], types: [TypeRef.Path]);
summary = Summaries(Program(io))[0];
Has(summary, "Writes"); Check(!Flag(summary, "UnknownWrites") && !Flag(summary, "UnknownReads"), "Filesystem observation incorrectly clobbers Scope memory");
var copy = Function("copy", 2, 2, TypeRef.Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Transfer), new(OpCode.Return)], types: [TypeRef.Path, TypeRef.Path]);
summary = Summaries(Program(copy))[0]; Has(summary, "Enumerates"); Check(!Flag(summary, "UnknownWrites"), "Scalar Path transfer invoked deferred memory");
var rangeCopy = copy with { ParameterTypes = [new(TypeKind.Range), TypeRef.Path] };
summary = Summaries(Program(rangeCopy))[0]; Has(summary, "Enumerates", "Parameter[0]"); Check(!Flag(summary, "UnknownWrites"), "Rooted enumeration was collapsed into unconditional unknown write");

var recursive = Function("recursive", 1, 1, TypeRef.Null,
    [new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "x"), new(OpCode.Pop), new(OpCode.MakeClosure, 0),
     new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "next"), new(OpCode.Call, 1, 1), new(OpCode.Return)], types: [TypeRef.Scope]);
summary = Summaries(Program(recursive))[0];
Check(Accesses(summary, "Reads").Contains("Parameter[0]::next::*", StringComparer.Ordinal), "Recursive path growth did not widen to rooted subtree");
Check(Accesses(summary, "Reads").Length < 5, "Recursive summary did not converge to finite path antichain");

var loop = Function("cursor", 2, 2, TypeRef.Scope,
    [new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 7), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "next"),
     new(OpCode.StoreLocal, 0), new(OpCode.Jump, 0), new(OpCode.Nop), new(OpCode.LoadLocal, 0), new(OpCode.Return)], types: [TypeRef.Scope, TypeRef.Bool]);
summary = Summaries(Program(loop))[0];
Check(Accesses(summary, "ReturnAliases").Contains("Parameter[0]::*", StringComparer.Ordinal), "Loop pointer advancement did not converge by path widening");

var retain = Function("retain", 1, 2, TypeRef.Null,
    [new(OpCode.LoadLocal, 1), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], captures: 1, types: [TypeRef.Scope]);
summary = Summaries(Program(retain))[0]; Has(summary, "Escapes", "Parameter[0]::*");

var sharedField = Function("shared-field", 0, 1, TypeRef.Null,
    [new(OpCode.MakeClosure, 1), new(OpCode.LoadStorage, 0), new(OpCode.MakeScope, 1, Text: "y"), new(OpCode.Call, 1, 2), new(OpCode.Return)], captures: 1);
summary = Summaries(Program(sharedField, set))[0];
Has(summary, "Writes", "Capture[0]"); Check(!Flag(summary, "UnknownWrites"), "Field-cell write lost capture identity");

var identityFn = Function("identity-fn", 1, 1, TypeRef.Function,
    [new(OpCode.LoadLocal, 0), new(OpCode.Return)], types: [TypeRef.Function]);
var retainThroughIdentity = Function("retain-through-identity", 1, 2, TypeRef.Null,
    [new(OpCode.MakeClosure, 1), new(OpCode.LoadCell, 1), new(OpCode.MakeClosure, 2, 1), new(OpCode.Call, 1, 2),
     new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], captures: 1, types: [TypeRef.Scope]);
summary = Summaries(Program(retainThroughIdentity, identityFn, capturedSet))[0];
Check(Accesses(summary, "Escapes").Contains("Parameter[0]::*", StringComparer.Ordinal), "Returning an actual closure lost its captured Scope escape");

var afterUnknown = Function("after-unknown", 2, 2, TypeRef.Scope,
    [new(OpCode.LoadLocal, 1), new(OpCode.Call), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "child"), new(OpCode.Return)], types: [TypeRef.Scope, TypeRef.Function]);
summary = Summaries(Program(afterUnknown))[0];
Check(Flag(summary, "UnknownWrites") && Flag(summary, "UnknownReturnAlias"), "Unknown callback left an unseen entry-child alias proof");

var invalidPrivate = Function("missing-private", 1, 1, TypeRef.Scope,
    [new(OpCode.LoadLocal, 0), new(OpCode.MakeScope, 1, Text: "known"), new(OpCode.GetMember, Text: "missing"), new(OpCode.Return)], types: [TypeRef.Scope]);
summary = Summaries(Program(invalidPrivate))[0];
Check(Flag(summary, "UnknownReturnAlias"), "An invalid private member was recursively expanded as its owner's shape");

var recursiveReturn = Function("recursive-return", 2, 2, TypeRef.Scope,
    [new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 4), new(OpCode.LoadLocal, 0), new(OpCode.Return),
     new(OpCode.MakeClosure, 0), new(OpCode.LoadLocal, 0), new(OpCode.GetMember, Text: "next"), new(OpCode.True), new(OpCode.Call, 2, 1), new(OpCode.Return)], types: [TypeRef.Scope, TypeRef.Bool]);
summary = Summaries(Program(recursiveReturn))[0];
Check(Accesses(summary, "ReturnAliases").Length < 4, "Recursive returned access paths did not converge");
Check(Accesses(summary, "ReturnAliases").Any(alias => alias.Contains("::*", StringComparison.Ordinal)), "Recursive returned access paths missed semantic widening");

var processCallFunction = Function("process-call-function", 0, 1, TypeRef.Null,
    [new(OpCode.LoadStorage, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.ProcessCall), new(OpCode.Return)], captures: 1);
summary = Summaries(Program(processCallFunction, capturedSet))[0];
Has(summary, "Writes", "Capture[0]::x");

var filesystemRange = Function("filesystem-range", 1, 1, new(TypeKind.Range, TypeRef.Path),
    [new(OpCode.LoadLocal, 0), new(OpCode.SelectPath, Text: ".+"), new(OpCode.Return)], types: [TypeRef.Path]);
summary = Summaries(Program(filesystemRange))[0];
Check(Flag(summary, "ReturnsCallbackFreeRange") && !Flag(summary, "HasDeferredInvocation"), "Filesystem paths were mistaken for source callbacks");

foreach ((string name, string source, bool unknownWrite, bool latent) in new[]
{
    ("lazy-creation", "fn make(object) { $std::ranges::transform([1], fn(item) { object::x = item; item }) }; make(scope { x: 0 });", false, true),
    ("callback-execution", "fn change(object) { $std::ranges::any([true], fn(item) { object::x = item; item }) }; change(scope { x: false });", true, false)
})
{
    Module module = SourceCompiler.Compile(source, name, 0);
    var all = Summaries(module);
    int index = module.Functions.Select((function, index) => (function, index)).First(pair => pair.function.Name == (name == "lazy-creation" ? "make" : "change")).index;
    summary = all[index];
    Check(Flag(summary, "UnknownWrites") == unknownWrite, name + " unknown memory classification differs");
    Check(Flag(summary, "HasDeferredInvocation") == latent, name + " latent invocation classification differs");
    if (!latent) Check(Flag(summary, "UnknownWrites") || Accesses(summary, "Writes").Any(access => access.EndsWith("::x", StringComparison.Ordinal)), "Eager callback mutation disappeared from summary");
    else Check(Accesses(summary, "Writes").Length == 0, "Lazy callback was treated as executed at construction");
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module optimized = Optimizer.Optimize(module, level);
        Artifact.Validate(optimized);
        await Interpreter.ExecuteAsync(optimized);
        using MemoryStream bytes = new(); Artifact.Write(optimized, bytes); bytes.Position = 0;
        await Interpreter.ExecuteAsync(Artifact.Read(bytes));
    }
}
var eager = Function("eager", 1, 2, TypeRef.Bool,
    [new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 2, 1), new(OpCode.StoreLocal, 1),
     new(OpCode.True), new(OpCode.MakeRange, 1), new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 2, Text: "ranges.any"), new(OpCode.Return)], types: [TypeRef.Scope]);
var mutate = Function("predicate", 1, 2, TypeRef.Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.SetMember, Text: "x"), new(OpCode.Pop), new(OpCode.LoadLocal, 1), new(OpCode.Return)], captures: 1, types: [TypeRef.Bool]);
var main = Function("main", 0, 0, TypeRef.Bool,
    [new(OpCode.MakeClosure, 1), new(OpCode.False), new(OpCode.MakeScope, 1, Text: "x"), new(OpCode.Call, 1, 2), new(OpCode.Return)]);
Module eagerModule = Program(main, eager, mutate);
summary = Summaries(eagerModule)[1];
Has(summary, "Writes", "Parameter[0]::x");
Check(!Flag(summary, "UnknownWrites"), "Known eager callback needlessly became global memory clobber");
Artifact.Validate(eagerModule);
Check(await Interpreter.ExecuteAsync(eagerModule) is BoolValue { Data: true }, "Known eager callback execution differs");
using (MemoryStream bytes = new())
{
    Artifact.Write(eagerModule, bytes); bytes.Position = 0;
    Check(await Interpreter.ExecuteAsync(Artifact.Read(bytes)) is BoolValue { Data: true }, "Known eager callback binary differs");
}
const int chainLength = 1500;
var chain = Enumerable.Range(0, chainLength).Select(index => index + 1 == chainLength ? get
    : Function("relay" + index, 1, 1, TypeRef.Number,
        [new(OpCode.MakeClosure, index + 1), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, index + 2), new(OpCode.Return)], types: [TypeRef.Scope])).ToArray();
var chainSummaries = Summaries(Program(chain));
Check(chainSummaries.Length == chainLength, "Long call graph lost summaries");
Has(chainSummaries[0], "Reads", "Parameter[0]::x");
Check(!Flag(chainSummaries[0], "UnknownWrites"), "Long call graph introduced an unknown clobber");

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "memory-effects-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await File.WriteAllBytesAsync(Path.Combine(root, "input"), [0, 1, 2, 255]);
    const string source = "fn write(other) { other::flag = true; exists ./marker; }; let left = scope { file: ./input }; let other = scope { flag: false }; let before = left::file; if exists ./choose { write(other); } else { write(other); }; let after = left::file; after -> ./result; left";
    Module raw = SourceCompiler.Compile(source, "memory-field-io", 0);
    var footprints = Summaries(raw);
    int writer = raw.Functions.Select((function, index) => (function, index)).Single(pair => pair.function.Name == "write").index;
    Has(footprints[writer], "Writes", "Parameter[0]::flag");
    Check(!Flag(footprints[writer], "UnknownWrites"), "Real filesystem read globally clobbered distinct fields");
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module optimized = Optimizer.Optimize(raw, level);
        using MemoryStream objectBytes = new(); Artifact.Write(optimized, objectBytes); objectBytes.Position = 0;
        Module loaded = Artifact.Read(objectBytes);
        Module bound = Binding.Bind(loaded, new(root, root));
        using MemoryStream executableBytes = new(); Artifact.Write(bound, executableBytes); executableBytes.Position = 0;
        foreach (Module program in new[] { optimized, loaded, Artifact.Read(executableBytes) })
        {
            Value result = await Interpreter.ExecuteAsync(program, new(WorkingDirectory: root, Output: TextWriter.Null));
            Check(result is ScopeValue { Members.Count: 1 }, $"O{level} escaped Scope changed shape");
            Check((await File.ReadAllBytesAsync(Path.Combine(root, "result"))).AsSpan().SequenceEqual(new byte[] { 0, 1, 2, 255 }), $"O{level} field/call/branch I/O bytes differ");
        }
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS: {assertions} precise memory/alias/recursive/deferred contracts plus actual source and artifact execution");
