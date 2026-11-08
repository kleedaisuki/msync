using Msync.Core;
using Msync.Runtime;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
Module Program(IReadOnlyList<Value> constants, params Instruction[] instructions) => new(constants,
    [new("main", 0, 2, instructions, TypeRef.Any)]);
async Task<Value> Run(Module module, ExecutionOptions? options = null) => await Interpreter.ExecuteAsync(module, options);
void Reject(Module module, string expected)
{
    try { Artifact.Validate(module); Check(false, $"Malformed bytecode accepted: {expected}"); }
    catch (MsyncException error) { Check(error.Message.Contains(expected, StringComparison.Ordinal), $"Malformed bytecode diagnostic lacks {expected}: {error.Message}"); }
}
Reject(Program([], new(OpCode.Pop), new(OpCode.Return)), "stack underflow");
Reject(Program([], new(OpCode.LoadLocal, 0), new(OpCode.Return)), "not defined");
Reject(Program([], new(OpCode.True), new(OpCode.GetIterator), new(OpCode.Return)), "expected Range");
Reject(Program([], new(OpCode.Null), new(OpCode.Not), new(OpCode.Return)), "expected Bool");
Reject(new Module([], [new FunctionCode("main", 1, 2, [new(OpCode.LoadLocal, 0), new(OpCode.JumpIfFalse, 4), new(OpCode.True), new(OpCode.Jump, 5), new(OpCode.Nop), new(OpCode.Return)], TypeRef.Any, ParameterTypes: [TypeRef.Bool])]), "stack heights disagree");
Check(PathSemantics.Join(new PathValue("left\\", Connection: new(ConnectionKind.Ssh, "host")), "leaf") == "left\\/leaf", "Remote path join erased a POSIX backslash");
var emptyScope = Program([], new(OpCode.MakeScope, 0, Text: ""), new(OpCode.Return));
Artifact.Validate(emptyScope);
Check(await Run(emptyScope) is ScopeValue { Members.Count: 0 }, "Empty lexical scope gained an empty-name member");
Check(await Run(Program([], new(OpCode.Null), new(OpCode.RegexMatch, Text: ".+"), new(OpCode.Return))) is BoolValue { Data: false }, "Regex partial pattern matched Null");
Check(await Run(Program([], new(OpCode.True), new(OpCode.RegexMatch, Text: ".+"), new(OpCode.Return))) is BoolValue { Data: false }, "Regex partial pattern stringified Bool");
var dispatch = new Module([new StringValue("argument")],
    [new("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, Text: "1:2"), new(OpCode.Return)], TypeRef.String),
     new("template", 1, 1, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
     new("specialized", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String)]);
using (var dispatchStream = new MemoryStream())
{
    Artifact.Write(dispatch, dispatchStream); dispatchStream.Position = 0;
    Check(await Run(Artifact.Read(dispatchStream)) is StringValue { Data: "argument" }, "Compiled first-class dispatch lost selected signature or argument");
}
var random = new Random(413);
for (int i = 0; i < 100; i++)
{
    string text = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => char.ConvertFromUtf32(random.Next(0x20, 0xd7ff))));
    var module = Program([new StringValue(text)], new(OpCode.Constant, 0), new(OpCode.Return));
    using var stream = new MemoryStream(); Artifact.Write(module, stream); stream.Position = 0;
    var loaded = Artifact.Read(stream);
    Check(await Run(loaded) is StringValue result && result.Data == text, "Binary constant round trip changed Unicode bytes");
    Check(loaded.Stage == ArtifactStage.Relocatable && loaded.Context is null, "Relocatable object acquired context");
}
var arithmetic = Program([new NumberValue(12), new NumberValue(3)], new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Multiply), new(OpCode.Return));
var optimized = Optimizer.Optimize(arithmetic);
Check(await Run(optimized) is NumberValue { Data: 36 }, "Constant-folded arithmetic changed value");
Check(optimized.Functions[0].Instructions.Count(i => i.Op == OpCode.Nop) == 2, "Pure arithmetic did not fold");
var branches = Program([new StringValue("yes"), new StringValue("no")], new(OpCode.True), new(OpCode.JumpIfFalse, 5), new(OpCode.Constant, 0), new(OpCode.Return), new(OpCode.Nop), new(OpCode.Constant, 1), new(OpCode.Return));
var boundBranches = Binding.Bind(Optimizer.Optimize(branches), new(Environment.CurrentDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
Check(await Run(boundBranches) is StringValue { Data: "yes" }, "Executable jump relocation changed selected branch");
Check(boundBranches.Functions[0].Instructions.All(i => i.Op != OpCode.Nop), "Executable retained removable no-op instructions");
var root = Path.Combine(Environment.CurrentDirectory, ".temp", "runtime-journey-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var writes = Program([new StringValue("klee\n世界"), new PathValue("a"), new PathValue("b"), new PathValue("c")],
        new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Pop),
        new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Transfer), new(OpCode.Pop),
        new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.Transfer), new(OpCode.Return));
    var executable = Binding.Bind(writes, new(root, root));
    Check(executable.Functions[0].Instructions.Count(i => i.Op == OpCode.LocalTransfer) == 3, "Executable failed to specialize known local transfers");
    Check(await Run(executable, new(Jobs: 4)) is BoolValue { Data: true }, "Transfer did not return Bool success");
    Check(await File.ReadAllTextAsync(Path.Combine(root, "c")) == "klee\n世界", "Scheduled read-after-write dependency lost bytes");
    string other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
    using var stream = new MemoryStream(); Artifact.Write(executable, stream); stream.Position = 0;
    await Run(Artifact.Read(stream), new(WorkingDirectory: other));
    Check(!File.Exists(Path.Combine(other, "a")), "Executable artifact relocated frozen cwd");
    await Run(writes, new(WorkingDirectory: other));
    Check(File.Exists(Path.Combine(other, "c")), "Relocatable module failed to bind current cwd");
    var lazyConstants = new Value[] { new StringValue("lazy"), new PathValue("lazy-flag"), new NumberValue(0) };
    var thunk = new FunctionCode("lazy-item", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Constant, 1), new(OpCode.Return)], TypeRef.Path);
    var lazy = new Module(lazyConstants, [new("main", 0, 1, [new(OpCode.MakeClosure, 1), new(OpCode.MakeLazyRange, 1, Text: "1"), new(OpCode.Constant, 2), new(OpCode.NativeCall, 2, Text: "ranges.take"), new(OpCode.NativeCall, 1, Text: "ranges.empty"), new(OpCode.Return)], TypeRef.Bool), thunk]);
    Check(await Run(lazy, new(WorkingDirectory: root)) is BoolValue { Data: true }, "Lazy take(0) did not produce empty Range");
    Check(!File.Exists(Path.Combine(root, "lazy-flag")), "Lazy item executed before first demand");
    var session = new ExecutionSession(new(WorkingDirectory: root));
    var firstSubmission = new Module(lazyConstants, [new("main", 0, 1, [new(OpCode.MakeClosure, 1), new(OpCode.MakeLazyRange, 1, Text: "1"), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null), thunk]);
    await session.ExecuteAsync(firstSubmission);
    Check(!File.Exists(Path.Combine(root, "lazy-flag")), "Stored REPL range evaluated eagerly");
    var secondSubmission = new Module(lazyConstants, [new("main", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "ranges.first"), new(OpCode.Return)], TypeRef.Path), thunk]);
    Check(await session.ExecuteAsync(secondSubmission) is PathValue { Raw: "lazy-flag" }, "Escaping lazy range lost originating module/callback");
    Check(await File.ReadAllTextAsync(Path.Combine(root, "lazy-flag")) == "lazy", "Escaping lazy range could not perform selected callback I/O");
    var errors = new StringWriter();
    var missing = Program([new PathValue("missing"), new PathValue("b")], new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Return));
    var interpreter = new Interpreter(missing, new(WorkingDirectory: root, Error: errors));
    Check(await interpreter.ExecuteAsync() is BoolValue { Data: false }, "Operational transfer failure did not yield false");
    Check(interpreter.FailedTransfers == 1 && errors.ToString().Contains("missing", StringComparison.Ordinal), "Failed transfer lost accounting or diagnostic");
    Check(await File.ReadAllTextAsync(Path.Combine(root, "b")) == "klee\n世界", "Failed transfer damaged existing destination");
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), "real");
        var alias = Program([new StringValue(new string('z', 65536)), new PathValue("real/data"), new PathValue("alias/data"), new PathValue("result")],
            new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.Transfer), new(OpCode.Return));
        await Run(alias, new(WorkingDirectory: root, Jobs: 4));
        Check((await File.ReadAllTextAsync(Path.Combine(root, "result"))).Length == 65536, "Alias-aware transfer dependency read stale source");
        var process = Program([new PathValue("/bin/sh"), new StringValue("-c"), new StringValue("printf hello")], new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Call, 2), new(OpCode.Return));
        Check(await Run(process) is ProcessValue { ExitCode: 0, StandardOutput: "hello" }, "Path process call lost output");
        var deferred = new Module([new PathValue("/bin/sh"), new StringValue("-c"), new StringValue("printf x >> deferred-calls"), new PathValue("item")],
            [new("main", 0, 1, [new(OpCode.MakeClosure, 1), new(OpCode.DeferRange), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "ranges.first"), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.NativeCall, 1, Text: "ranges.first"), new(OpCode.Return)], TypeRef.Path),
             new("range-call", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Call, 2), new(OpCode.Pop), new(OpCode.Constant, 3), new(OpCode.MakeRange, 1), new(OpCode.Return)], new(TypeKind.Range, TypeRef.Path))]);
        await Run(deferred, new(WorkingDirectory: root));
        Check(await File.ReadAllTextAsync(Path.Combine(root, "deferred-calls")) == "x", "Deferred range call reevaluated arguments/body across demands");
        try { await Run(process, new(SpoolLimit: 3)); Check(false, "Configured spool limit had no effect"); }
        catch (MsyncException error) { Check(error.Message.Contains("spool limit", StringComparison.Ordinal), "Spool failure lost diagnostic"); }
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Runtime bytecode/user journeys passed {assertions} assertions.");
