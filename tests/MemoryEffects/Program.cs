using Msync.Optimization;
using System.Collections;
using System.Reflection;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;
using Module = Msync.Core.Module;

const int Null = 0, Bool = 1, Number = 2, PathType = 3, Callable = 4, Owner = 5, Range = 6, PathRange = 7,
    NumberScope = 8, BoolScope = 9, EitherY = 10, OwnerScope = 11, OpaqueScope = 12;
const int NumberLayout = 0, BoolLayout = 1, YLayout = 2, OwnerLayout = 3, PrivateLayout = 4;
const int X = 0, Y = 1, Child = 2, Next = 3, Items = 4, Missing = 5;
CodeType[] codeTypes =
[
    new(TypeKind.Null), new(TypeKind.Bool), new(TypeKind.Number), new(TypeKind.Path),
    new(TypeKind.Function), new(TypeKind.Storage), new(TypeKind.Range), new(TypeKind.Range, PathType),
    new(TypeKind.Scope, ScopeLayouts: [NumberLayout]), new(TypeKind.Scope, ScopeLayouts: [BoolLayout]),
    new(TypeKind.Scope, ScopeLayouts: [NumberLayout, YLayout]), new(TypeKind.Scope, ScopeLayouts: [OwnerLayout]),
    new(TypeKind.Scope)
];
CodeLayout[] layouts =
[
    new([new("x", Number), new("y", Bool), new("child", NumberScope), new("next", NumberScope)]),
    new([new("x", Bool)]), new([new("y", Bool)]), new([new("items", Owner)]), new([new("known", BoolScope)])
];
CodeFieldProjection[] projections =
[
    new("x", [new(NumberLayout, 0), new(BoolLayout, 0)]),
    new("y", [new(NumberLayout, 1), new(YLayout, 0)]),
    new("child", [new(NumberLayout, 2)]), new("next", [new(NumberLayout, 3)]),
    new("items", [new(OwnerLayout, 0)]), new("missing", [])
];

MethodInfo analyze = typeof(Optimizer).Assembly.GetType("Msync.Optimization.MemoryEffects")!
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
TypeRef RuntimeType(int type) => new(codeTypes[type].Kind,
    codeTypes[type].Element < 0 ? null : RuntimeType(codeTypes[type].Element));
FunctionCode Function(string name, int locals, int result, Instruction[] code, CodeCapture[]? captures = null, int[]? types = null)
{
    types ??= [];
    captures ??= [];
    return new(name, types.Length, locals, code, RuntimeType(result), captures.Length, types.Select(RuntimeType).ToArray())
    { CodeSignature = new(types, result, captures) };
}
Module Program(params FunctionCode[] functions) => new([], functions, OptimizationLevel: 0)
{ CodeTypes = codeTypes, ScopeLayouts = layouts, FieldProjections = projections };
Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}
IEnumerable<Module> Images(Module module)
{
    yield return module;
    yield return RoundTrip(module);
    yield return RoundTrip(Binding.Bind(module, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
}
Check(NativeSignatures.All.All(signature => signature.Behavior.HasValue),
    "The native registry must publish complete behavior before consumers analyze it");
var get = Function("get", 1, Number, [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, X), new(OpCode.Return)], types: [NumberScope]);
var set = Function("set", 1, Null, [new(OpCode.LoadLocal, 0), new(OpCode.True), new(OpCode.StoreField, Y), new(OpCode.Return)], types: [EitherY]);
object summary = Summaries(Program(get))[0];
Has(summary, "Reads", "Parameter[0]::x"); Has(summary, "Writes"); Has(summary, "ReturnAliases");
Check(!Flag(summary, "UnknownReads") && !Flag(summary, "UnknownWrites"), "Known field read became global memory effect");
summary = Summaries(Program(set))[0]; Has(summary, "Writes", "Parameter[0]::y"); Has(summary, "Reads");

var relay = Function("relay", 2, Number,
    [new(OpCode.MakeClosure, 2), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 3), new(OpCode.Pop),
     new(OpCode.LoadLocal, 0), new(OpCode.LoadField, X), new(OpCode.Return)], types: [NumberScope, NumberScope]);
summary = Summaries(Program(get, relay, set))[1];
Has(summary, "Reads", "Parameter[0]::x"); Has(summary, "Writes", "Parameter[1]::y");
Check(!Flag(summary, "UnknownWrites"), "Known unrelated callee clobbers global memory");

var nested = Function("nested", 1, NumberScope,
    [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Child), new(OpCode.LoadField, Next), new(OpCode.Return)], types: [NumberScope]);
summary = Summaries(Program(nested))[0];
Has(summary, "Reads", "Parameter[0]::child", "Parameter[0]::child::next"); Has(summary, "ReturnAliases", "Parameter[0]::child::next");

var capturedSet = Function("captured-field", 1, Null,
    [new(OpCode.LoadLocal, 0), new(OpCode.AdmitScope, BoolScope), new(OpCode.True), new(OpCode.StoreField, X), new(OpCode.Return)],
    captures: [new(BoolScope, true)]);
summary = Summaries(Program(capturedSet))[0];
Has(summary, "Reads", "Capture[0]"); Has(summary, "Writes", "Capture[0]::x");
var capturedRebind = Function("captured-rebind", 2, Null,
    [new(OpCode.LoadLocal, 1), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], captures: [new(BoolScope, true)], types: [BoolScope]);
summary = Summaries(Program(capturedRebind))[0]; Has(summary, "Writes", "Capture[0]");

var closureRelay = Function("closure-relay", 1, Null,
    [new(OpCode.LoadCell, 0, BoolScope + 1), new(OpCode.MakeClosure, 1, 1), new(OpCode.Call, 0, 2), new(OpCode.Return)], types: [BoolScope]);
summary = Summaries(Program(closureRelay, capturedSet))[0]; Has(summary, "Writes", "Parameter[0]::x");
Check(!Flag(summary, "UnknownWrites"), "Private capture cell lost its pointee identity");

var opaqueCapture = Function("opaque-capture", 2, Null,
    [new(OpCode.LoadLocal, 0), new(OpCode.GetCapture, 0), new(OpCode.InitializeLocal, 1),
     new(OpCode.True), new(OpCode.StoreLocal, 1), new(OpCode.Null), new(OpCode.Return)], types: [Callable]);
summary = Summaries(Program(opaqueCapture))[0]; Check(Flag(summary, "UnknownWrites"), "Unknown raw captured cell write was ignored");

var io = Function("io", 1, Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Return)], types: [PathType]);
summary = Summaries(Program(io))[0];
Has(summary, "Writes"); Check(!Flag(summary, "UnknownWrites") && !Flag(summary, "UnknownReads"), "Filesystem observation incorrectly clobbers Object memory");
var copy = Function("copy", 2, Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Transfer), new(OpCode.Return)], types: [PathType, PathType]);
summary = Summaries(Program(copy))[0]; Has(summary, "Enumerates"); Check(!Flag(summary, "UnknownWrites"), "Scalar Path transfer invoked deferred memory");
var rangeCopy = Function("range-copy", 2, Bool, [.. copy.Instructions], types: [Range, PathType]);
summary = Summaries(Program(rangeCopy))[0]; Has(summary, "Enumerates", "Parameter[0]"); Check(!Flag(summary, "UnknownWrites"), "Rooted enumeration was collapsed into unconditional unknown write");

var recursive = Function("recursive", 1, Null,
    [new(OpCode.LoadLocal, 0), new(OpCode.LoadField, X), new(OpCode.Pop), new(OpCode.MakeClosure, 0),
     new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Next), new(OpCode.Call, 1, 1), new(OpCode.Return)], types: [NumberScope]);
summary = Summaries(Program(recursive))[0];
Check(Accesses(summary, "Reads").Contains("Parameter[0]::next::*", StringComparer.Ordinal), "Recursive path growth did not widen to rooted subtree");
Check(Accesses(summary, "Reads").Length < 5, "Recursive summary did not converge to finite path antichain");

var loop = Function("cursor", 2, NumberScope,
    [new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 7), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Next),
     new(OpCode.StoreLocal, 0), new(OpCode.Jump, 0), new(OpCode.Nop), new(OpCode.LoadLocal, 0), new(OpCode.Return)], types: [NumberScope, Bool]);
summary = Summaries(Program(loop))[0];
Check(Accesses(summary, "ReturnAliases").Contains("Parameter[0]::*", StringComparer.Ordinal), "Loop pointer advancement did not converge by path widening");

var retain = Function("retain", 2, Null,
    [new(OpCode.LoadLocal, 1), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], captures: [new(BoolScope, true)], types: [BoolScope]);
summary = Summaries(Program(retain))[0]; Has(summary, "Escapes", "Parameter[0]::*");

var sharedEntry = Function("shared-entry", 1, Bool,
    [new(OpCode.False), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, Bool + 1), new(OpCode.Pop),
     new(OpCode.MakeClosure, 1), new(OpCode.LoadStorage, 0), new(OpCode.NewScope, YLayout), new(OpCode.Call, 1, 2),
     new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Return)]);
var sharedField = Function("shared-field", 1, Null,
    [new(OpCode.MakeClosure, 1), new(OpCode.LoadStorage, 0), new(OpCode.NewScope, YLayout), new(OpCode.Call, 1, 2), new(OpCode.Return)], captures: [new(Bool, true)]);
summary = Summaries(Program(sharedField, set))[0];
Has(summary, "Writes", "Capture[0]"); Check(!Flag(summary, "UnknownWrites"), "Field-cell write lost capture identity");
foreach (Module image in Images(Program(sharedEntry, set)))
    Check(await Interpreter.ExecuteAsync(image) is BoolValue { Data: true },
        "Writing the selected field ordinal did not reach its captured Cell");

Module indexed = Program(
        Function("index-read", 1, Number,
            [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.NativeCall, 2, Text: "storage.vector_get"), new(OpCode.Return)],
            types: [Owner]),
        Function("index-size", 1, Number,
            [new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "storage.vector_size"), new(OpCode.Return)],
            types: [Owner]),
        Function("index-set", 1, Null,
            [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.Constant, 1),
                new(OpCode.Add), new(OpCode.NativeCall, 3, Text: "storage.vector_set"), new(OpCode.Return)],
            types: [Owner]),
        Function("index-captured", 3, Null,
            [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.NativeCall, 2, Text: "storage.vector_get"),
                new(OpCode.InitializeLocal, 1),
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.NativeCall, 2, Text: "storage.vector_get"),
                new(OpCode.InitializeLocal, 2),
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.LoadLocal, 2),
                new(OpCode.NativeCall, 3, Text: "storage.vector_set"), new(OpCode.Pop),
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.LoadLocal, 1),
                new(OpCode.NativeCall, 3, Text: "storage.vector_set"), new(OpCode.Return)], captures: [new(Owner, true)]),
        Function("index-relay", 1, Null,
            [new(OpCode.LoadCell, 0, Owner + 1), new(OpCode.MakeClosure, 3, 1), new(OpCode.Call, 0, 4), new(OpCode.Return)],
            types: [Owner]),
        Function("index-child-relay", 1, Null,
            [new(OpCode.MakeClosure, 2), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Items),
                new(OpCode.Call, 1, 3), new(OpCode.Return)], types: [OwnerScope]),
        Function("index-grow", 1, Number,
            [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.NativeCall, 2, Text: "storage.vector_push"), new(OpCode.Return)],
            types: [Owner])
    ) with { Constants = [new NumberValue(0), new NumberValue(1)] };
var indexedSummaries = Summaries(indexed);
Has(indexedSummaries[0], "IndexedReads", "Parameter[0]::contents[0]", "Parameter[0]::length");
Has(indexedSummaries[0], "IndexedWrites");
Has(indexedSummaries[1], "IndexedReads", "Parameter[0]::length");
Has(indexedSummaries[1], "Reads");
Has(indexedSummaries[2], "IndexedReads", "Parameter[0]::length");
Has(indexedSummaries[2], "IndexedWrites", "Parameter[0]::contents[1]");
Has(indexedSummaries[3], "IndexedReads", "Capture[0]::contents[0]", "Capture[0]::contents[1]", "Capture[0]::length");
Has(indexedSummaries[3], "IndexedWrites", "Capture[0]::contents[0]", "Capture[0]::contents[1]");
Has(indexedSummaries[4], "IndexedWrites", "Parameter[0]::contents[0]", "Parameter[0]::contents[1]");
Has(indexedSummaries[5], "IndexedWrites", "Parameter[0]::items::contents[1]");
Has(indexedSummaries[6], "IndexedWrites", "Parameter[0]::contents[*]", "Parameter[0]::length");

var identityFn = Function("identity-fn", 1, Callable,
    [new(OpCode.LoadLocal, 0), new(OpCode.Return)], types: [Callable]);
var retainThroughIdentity = Function("retain-through-identity", 2, Null,
    [new(OpCode.MakeClosure, 1), new(OpCode.LoadCell, 1, BoolScope + 1), new(OpCode.MakeClosure, 2, 1), new(OpCode.Call, 1, 2),
     new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], captures: [new(Callable, true)], types: [BoolScope]);
summary = Summaries(Program(retainThroughIdentity, identityFn, capturedSet))[0];
Check(Accesses(summary, "Escapes").Contains("Parameter[0]::*", StringComparer.Ordinal), "Returning an actual closure lost its captured Object escape");

var afterUnknown = Function("after-unknown", 2, NumberScope,
    [new(OpCode.LoadLocal, 1), new(OpCode.Call), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Child), new(OpCode.Return)], types: [NumberScope, Callable]);
summary = Summaries(Program(afterUnknown))[0];
Check(Flag(summary, "UnknownWrites") && Flag(summary, "UnknownReturnAlias"), "Unknown callback left an unseen entry-child alias proof");

var missingEntry = Function("missing-entry", 0, OpaqueScope,
    [new(OpCode.MakeClosure, 1), new(OpCode.False), new(OpCode.NewScope, BoolLayout), new(OpCode.Call, 1, 2), new(OpCode.Return)]);
var invalidPrivate = Function("missing-private", 1, OpaqueScope,
    [new(OpCode.LoadLocal, 0), new(OpCode.NewScope, PrivateLayout), new(OpCode.LoadField, Missing), new(OpCode.Return)], types: [BoolScope]);
Module missing = Program(missingEntry, invalidPrivate);
summary = Summaries(missing)[1];
Check(Flag(summary, "UnknownReturnAlias"), "An invalid private member was recursively expanded as its owner's shape");
foreach (Module image in Images(missing))
{
    try
    {
        await Interpreter.ExecuteAsync(image);
        Check(false, "A missing opaque field projection unexpectedly returned");
    }
    catch (MsyncException error)
    {
        Check(error.Message == "Object has no member 'missing'", $"Unexpected opaque field error: {error.Message}");
    }
}

var recursiveReturn = Function("recursive-return", 2, NumberScope,
    [new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 4), new(OpCode.LoadLocal, 0), new(OpCode.Return),
     new(OpCode.MakeClosure, 0), new(OpCode.LoadLocal, 0), new(OpCode.LoadField, Next), new(OpCode.True), new(OpCode.Call, 2, 1), new(OpCode.Return)], types: [NumberScope, Bool]);
summary = Summaries(Program(recursiveReturn))[0];
Check(Accesses(summary, "ReturnAliases").Length < 4, "Recursive returned access paths did not converge");
Check(Accesses(summary, "ReturnAliases").Any(alias => alias.Contains("::*", StringComparison.Ordinal)), "Recursive returned access paths missed semantic widening");

var processCallFunction = Function("process-call-function", 1, Null,
    [new(OpCode.LoadStorage, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.ProcessCall), new(OpCode.Return)], captures: [new(BoolScope, true)]);
summary = Summaries(Program(processCallFunction, capturedSet))[0];
Has(summary, "Writes", "Capture[0]::x");

var filesystemRange = Function("filesystem-range", 1, PathRange,
    [new(OpCode.LoadLocal, 0), new(OpCode.SelectPath, Text: ".+"), new(OpCode.Return)], types: [PathType]);
summary = Summaries(Program(filesystemRange))[0];
Check(Flag(summary, "ReturnsCallbackFreeRange") && !Flag(summary, "HasDeferredInvocation"), "Filesystem paths were mistaken for source callbacks");

foreach ((string name, string source, bool unknownWrite, bool latent) in new[]
{
    ("lazy-creation", "fn make(record) { $std::ranges::transform([1], fn(item) { record::x = item; item }) }; let record = object { x: 0 }; make(record); record::x", false, true),
    ("callback-execution", "fn change(record) { $std::ranges::any([true], fn(item) { record::x = item; item }) }; let record = object { x: false }; change(record); record::x", false, false)
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
        Value expected = latent ? new NumberValue(0) : Value.True;
        Check(await Interpreter.ExecuteAsync(optimized) == expected, $"{name} O{level}: observable field result changed");
        using MemoryStream bytes = new(); Artifact.Write(optimized, bytes); bytes.Position = 0;
        Check(await Interpreter.ExecuteAsync(Artifact.Read(bytes)) == expected, $"{name} O{level}: artifact field result changed");
    }
}
var eager = Function("eager", 2, Bool,
    [new(OpCode.LoadCell, 0, BoolScope + 1), new(OpCode.MakeClosure, 2, 1), new(OpCode.StoreLocal, 1),
     new(OpCode.True), new(OpCode.MakeRange, 1), new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 2, Text: "ranges.any"), new(OpCode.Return)], types: [BoolScope]);
var mutate = Function("predicate", 2, Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.AdmitScope, BoolScope), new(OpCode.LoadLocal, 1), new(OpCode.StoreField, X),
     new(OpCode.Pop), new(OpCode.LoadLocal, 1), new(OpCode.Return)], captures: [new(BoolScope, true)], types: [Bool]);
var main = Function("main", 0, Bool,
    [new(OpCode.MakeClosure, 1), new(OpCode.False), new(OpCode.NewScope, BoolLayout), new(OpCode.Call, 1, 2), new(OpCode.Return)]);
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
var redirectingPredicate = Function("redirecting-predicate", 3, Bool,
    [new(OpCode.LoadLocal, 0), new(OpCode.AdmitScope, BoolScope), new(OpCode.True), new(OpCode.StoreField, X), new(OpCode.Pop),
     new(OpCode.LoadLocal, 1), new(OpCode.StoreLocal, 0), new(OpCode.False), new(OpCode.Return)], captures: [new(BoolScope, true), new(BoolScope, true)], types: [Bool]);
var repeated = Function("repeated", 2, Bool,
    [new(OpCode.True), new(OpCode.False), new(OpCode.MakeRange, 2), new(OpCode.LoadCell, 0, BoolScope + 1),
     new(OpCode.LoadCell, 1, BoolScope + 1), new(OpCode.MakeClosure, 1, 2), new(OpCode.NativeCall, 2, Text: "ranges.any"), new(OpCode.Return)],
    types: [BoolScope, BoolScope]);
summary = Summaries(Program(repeated, redirectingPredicate))[0];
Check(Flag(summary, "UnknownWrites") || Accesses(summary, "Writes").Contains("Parameter[1]::x"),
    "Repeated predicate ignored the pointee selected by a previous invocation");
var emptyTerminal = repeated with { Instructions = [new(OpCode.MakeRange, 0), .. repeated.Instructions.Skip(3)] };
summary = Summaries(Program(emptyTerminal, redirectingPredicate))[0];
Has(summary, "Writes");
Check(!Flag(summary, "UnknownWrites"), "Empty terminal invoked its predicate");
const string redirectedSource = "fn change(first, second) { let selected = first; $std::ranges::any([false, false, false], fn(item) { selected::x = true; selected = second; item }); }; let first = object { x: false }; let second = object { x: false }; change(first, second); second::x";
Module redirected = SourceCompiler.Compile(redirectedSource, "repeated-callback-rebinding", 0);
foreach (int level in new[] { 0, 2, 3 })
{
    Module optimized = Optimizer.Optimize(redirected, level);
    Check(await Interpreter.ExecuteAsync(optimized) == Value.True, $"O{level} repeated callback lost later alias writes");
    using MemoryStream bytes = new(); Artifact.Write(optimized, bytes); bytes.Position = 0;
    Check(await Interpreter.ExecuteAsync(Artifact.Read(bytes)) == Value.True, $"O{level} repeated callback artifact lost later alias writes");
}
const int chainLength = 1500;
var chain = Enumerable.Range(0, chainLength).Select(index => index + 1 == chainLength ? get
    : Function("relay" + index, 1, Number,
        [new(OpCode.MakeClosure, index + 1), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, index + 2), new(OpCode.Return)], types: [NumberScope])).ToArray();
var chainSummaries = Summaries(Program(chain));
Check(chainSummaries.Length == chainLength, "Long call graph lost summaries");
Has(chainSummaries[0], "Reads", "Parameter[0]::x");
Check(!Flag(chainSummaries[0], "UnknownWrites"), "Long call graph introduced an unknown clobber");

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "memory-effects-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await File.WriteAllBytesAsync(Path.Combine(root, "input"), [0, 1, 2, 255]);
    const string source = "fn write(other) { other::flag = true; exists ./marker; }; let left = object { file: ./input }; let other = object { flag: false }; let before = left::file; if exists ./choose { write(other); } else { write(other); }; let after = left::file; after -> ./result; left";
    Module raw = SourceCompiler.Compile(source, "memory-field-io", 0);
    var footprints = Summaries(raw);
    int[] writers = raw.Functions.SelectMany(function => function.Instructions)
        .Where(instruction => instruction.Op == OpCode.Call && instruction.B > 0)
        .Select(instruction => instruction.B - 1).Distinct()
        .Where(index => raw.Functions[index].Name == "write").ToArray();
    Check(writers.Length != 0, "Real field-write calls were not retained");
    foreach (int writer in writers)
    {
        Has(footprints[writer], "Writes", "Parameter[0]::flag");
        Check(!Flag(footprints[writer], "UnknownWrites"), "Real filesystem read globally clobbered distinct fields");
    }
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
            Check(result is ScopeValue { Members.Count: 1 }, $"O{level} escaped Object changed shape");
            Check((await File.ReadAllBytesAsync(Path.Combine(root, "result"))).AsSpan().SequenceEqual(new byte[] { 0, 1, 2, 255 }), $"O{level} field/call/branch I/O bytes differ");
        }
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS: {assertions} precise memory/alias/recursive/deferred contracts plus actual source and artifact execution");
