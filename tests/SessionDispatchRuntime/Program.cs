using Msync.Core;
using Msync.Runtime;

if (args is ["--dispatch-child", var marker])
{
    await File.WriteAllTextAsync(marker, "completed");
    return;
}

int checks = 0;
if (args is ["--portable-contract-only"])
{
    PortableContract();
    Console.WriteLine($"PASS {checks} portable artifact/session boundary contracts");
    return;
}
foreach (int level in new[] { 0, 2, 3 })
{
    await DeferredGeneration(level);
    await MissingTarget(level);
    await NativeTarget(level);
    await FutureCallback(level);
    await SignatureSlots(level);
    await PartialFailure(level);
    await FutureProcess(level);
}
PortableContract();
Console.WriteLine($"PASS {checks} session dispatch runtime contracts");

async Task DeferredGeneration(int level)
{
    await using ExecutionSession session = new();
    Module initial = Image(Initial(), level, future: false);
    Artifact.Validate(initial);
    await session.ExecuteAsync(initial);
    Require(session.GetLocalValue(1) is RangeValue, "deferred value is retained before invocation");
    await session.ExecuteAsync(Image(Reassign(), level, future: true));
    Value selected = await session.ExecuteAsync(Image(Consume(), level, future: true));
    Require(selected is NumberValue { Data: 20 }, $"O{level}: old deferred value uses the new callable and its captures");

    // The range's existing once-on-demand memoization is independent of the
    // signature table: a later callable assignment cannot replay its evaluation.
    await session.ExecuteAsync(Image(InitialCallable(), level, future: true));
    Value retained = await session.ExecuteAsync(Image(Consume(), level, future: true));
    Require(retained is NumberValue { Data: 20 }, $"O{level}: enumerating a resolved range does not call again");

    await using ExecutionSession independent = new();
    await independent.ExecuteAsync(Image(Initial(), level, future: false));
    Value first = await independent.ExecuteAsync(Image(Consume(), level, future: false));
    Require(first is NumberValue { Data: 10 }, $"O{level}: dispatch tables do not leak across sessions");
}

async Task MissingTarget(int level)
{
    await using ExecutionSession session = new();
    await session.ExecuteAsync(Image(Initial(), level, future: false));
    await session.ExecuteAsync(Image(Reassign(), level, future: false));
    try
    {
        await session.ExecuteAsync(Image(Consume(), level, future: false));
        throw new InvalidOperationException("An unregistered future identity was executed");
    }
    catch (MsyncException error)
    {
        Require(error.Message.Contains("session call signature #0", StringComparison.Ordinal),
            $"O{level}: missing identity is diagnosed with its signature");
    }
}

async Task NativeTarget(int level)
{
    Value[] constants = [new FunctionValue(-1, NativeName: "ranges.single"), new NumberValue(42)];
    FunctionCode main = new("native", 0, 0,
        [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Call, 1, -1), new(OpCode.Return)], new(TypeKind.Range, TypeRef.Number));
    Module module = new(constants, [main], OptimizationLevel: level)
    { SessionDispatches = [new(0, new Dictionary<int, int>())] };
    Artifact.Validate(module);
    var result = await Interpreter.ExecuteAsync(module);
    Require(result is RangeValue, $"O{level}: native Function values bypass source identity lookup");
}

async Task FutureCallback(int level)
{
    await using ExecutionSession session = new();
    Module initial = CallbackImage(Initial(), level, future: false);
    Artifact.Validate(initial);
    await session.ExecuteAsync(initial);
    await session.ExecuteAsync(CallbackImage(Reassign(), level, future: true));
    Value result = await session.ExecuteAsync(CallbackImage(Consume(), level, future: true));
    Require(result is NumberValue { Data: 20 }, $"O{level}: native callbacks specialize the current callable from their session signature");
}

async Task SignatureSlots(int level)
{
    FunctionCode main = new("signatures", 0, 0,
    [
        new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, -1),
        new(OpCode.MakeClosure, 1), new(OpCode.Constant, 2), new(OpCode.Call, 1, -2),
        new(OpCode.MakeScope, 2, Text: "number\0text"), new(OpCode.Return)
    ], TypeRef.Scope);
    FunctionCode generic = new("generic", 1, 1, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null);
    FunctionCode number = new("number", 1, 1,
        [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.Return)], TypeRef.Number,
        ParameterTypes: [TypeRef.Number]);
    FunctionCode text = new("text", 1, 1,
        [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 3), new(OpCode.Add), new(OpCode.Return)], TypeRef.String,
        ParameterTypes: [TypeRef.String]);
    Module module = new([new NumberValue(10), new NumberValue(1), new StringValue("value"), new StringValue("-ok")],
        [main, generic, number, text], OptimizationLevel: level)
    { SessionDispatches = [new(0, new Dictionary<int, int> { [1] = 2 }), new(1, new Dictionary<int, int> { [1] = 3 })] };
    Artifact.Validate(module);
    Value result = await Interpreter.ExecuteAsync(module);
    Require(result is ScopeValue scope && scope["number"] is NumberValue { Data: 11 }
        && scope["text"] is StringValue { Data: "value-ok" }, $"O{level}: one identity resolves independently for different typed signatures");
}

async Task PartialFailure(int level)
{
    await using ExecutionSession session = new();
    await session.ExecuteAsync(Image(Initial(), level, future: false));
    string missing = Path.GetFullPath(Path.Combine(".temp", "session-dispatch-runtime", Guid.NewGuid().ToString("N"), "absent.txt"));
    Module next = Image([
        .. Reassign()[..^2], new(OpCode.Constant, 2), new(OpCode.NativeCall, 1, Text: "io.read_text"), new(OpCode.Return)
    ], level, future: true) with
    { Constants = [new NumberValue(10), new NumberValue(20), new PathValue(missing)] };
    try
    {
        await session.ExecuteAsync(next);
        throw new InvalidOperationException("The missing file read succeeded");
    }
    catch (Exception error) when (error is MsyncException or IOException)
    {
        Require(session.GetLocalValue(0) is FunctionValue { Function: 4 }, $"O{level}: runtime failure retains the preceding actual callable assignment");
    }
    Value result = await session.ExecuteAsync(Image(Consume(), level, future: true));
    Require(result is NumberValue { Data: 20 }, $"O{level}: partial-failure continuation retains the corresponding code/table generation");
}

async Task FutureProcess(int level)
{
    string work = Path.GetFullPath(Path.Combine(".temp", "session-dispatch-runtime", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(work);
    string marker = Path.Combine(work, "process-completed.txt");
    await using ExecutionSession session = new();
    await session.ExecuteAsync(Image(Initial(), level, future: false));
    Module next = Image(Reassign(), level, future: true);
    FunctionCode[] functions = next.Functions.ToArray();
    functions[5] = new("future_process", 0, 1,
    [
        new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.Constant, 4), new(OpCode.ProcessCall, 2, 1), new(OpCode.Pop),
        new(OpCode.LoadLocal, 0), new(OpCode.MakeRange, 1), new(OpCode.Return)
    ], new(TypeKind.Range, TypeRef.Number), CaptureCount: 1);
    next = next with
    {
        Functions = functions,
        Constants = [new NumberValue(10), new NumberValue(20),
        new PathValue(Environment.ProcessPath!), new StringValue("--dispatch-child"), new StringValue(marker)]
    };
    await session.ExecuteAsync(next);
    Value result = await session.ExecuteAsync(next with
    {
        Functions = functions.Select((function, index) =>
        index == 0 ? function with { Instructions = Consume() } : function).ToArray()
    });
    Require(result is NumberValue { Data: 20 } && File.ReadAllText(marker) == "completed",
        $"O{level}: a newly registered process-producing body joins its nonescaping work");
    File.Delete(marker);
    Directory.Delete(work);
}

void PortableContract()
{
    using MemoryStream stream = new();
    try
    {
        Artifact.Write(Image(Initial(), 2, future: false), stream);
        throw new InvalidOperationException("An open session table was serialized");
    }
    catch (MsyncException error)
    {
        Require(error.Message.Contains("active REPL", StringComparison.Ordinal), "open session exports explain the live dependency");
        Require(stream.Length == 0, "rejecting a session export does not emit a partial header");
    }
    FunctionCode main = new("closed", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number);
    Module closed = new([new NumberValue(7)], [main]);
    Artifact.Write(closed, stream);
    stream.Position = 0;
    Module loaded = Artifact.Read(stream);
    Require(loaded.SessionDispatches is null && Artifact.FormatVersion == 5 && Artifact.LanguageVersion == "0.8.0",
        "portable artifacts use the execution-plan format without serializing live session dispatch");
    Require((InstructionEffects.Effects(new(OpCode.Call, 0, -1)) & (InstructionEffect.Read | InstructionEffect.Write))
        == (InstructionEffect.Read | InstructionEffect.Write), "open call effects cannot be treated as a pure fixed target");
    Require((InstructionEffects.Effects(new(OpCode.Call, 0, 1)) & (InstructionEffect.Read | InstructionEffect.Write)) == 0,
        "ordinary direct-call effects are unchanged");
    try
    {
        Artifact.Validate(Image(Initial(), 0, future: false) with { SessionDispatches = null });
        throw new InvalidOperationException("A negative call operand was accepted without its session table");
    }
    catch (MsyncException error)
    {
        Require(error.Message.Contains("active session dispatch", StringComparison.Ordinal), "portable negative call operands are rejected");
    }
}

static Module Image(IReadOnlyList<Instruction> entry, int level, bool future)
{
    TypeRef range = new(TypeKind.Range, TypeRef.Number);
    FunctionCode identity = new("generic", 0, 1, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1);
    FunctionCode body = new("typed", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.MakeRange, 1), new(OpCode.Return)], range, CaptureCount: 1);
    FunctionCode thunk = new("pending", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Call, 0, -1), new(OpCode.Return)], range, CaptureCount: 1);
    FunctionCode main = new("<main>", 0, 2, entry, TypeRef.Any);
    Dictionary<int, int> targets = new() { [1] = 2 };
    if (future) targets.Add(4, 5);
    return new([new NumberValue(10), new NumberValue(20)], [main, identity, body, thunk, identity, body], OptimizationLevel: level)
    { SessionDispatches = [new(0, targets)], SessionRoots = [0, 1] };
}

static Module CallbackImage(IReadOnlyList<Instruction> entry, int level, bool future)
{
    Module image = Image(entry, level, future);
    FunctionCode[] functions = image.Functions.ToArray();
    functions[1] = functions[4] = new("generic_callback", 1, 2, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, CaptureCount: 1);
    functions[2] = new("initial_callback", 1, 2,
        [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.Return)], TypeRef.Number, CaptureCount: 1);
    functions[5] = new("future_callback", 1, 2,
        [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Multiply), new(OpCode.Return)], TypeRef.Number, CaptureCount: 1);
    functions[3] = new("deferred_transform", 0, 1,
    [
        new(OpCode.Constant, 2), new(OpCode.LoadLocal, 0), new(OpCode.SpecializeClosure, -1),
        new(OpCode.NativeCall, 2, Text: "ranges.transform"), new(OpCode.Return)
    ], new(TypeKind.Range, TypeRef.Number), CaptureCount: 1);
    return image with
    {
        Functions = functions,
        Constants = [new NumberValue(10), new NumberValue(20),
        new RangeValue([new NumberValue(1)], TypeRef.Number)]
    };
}

static Instruction[] Initial() =>
[
    new(OpCode.Constant, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.InitializeLocal, 0),
    new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 3, 1), new(OpCode.DeferRange), new(OpCode.InitializeLocal, 1),
    new(OpCode.Null), new(OpCode.Return)
];

static Instruction[] Reassign() =>
[
    new(OpCode.Constant, 1), new(OpCode.MakeClosure, 4, 1), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)
];

static Instruction[] InitialCallable() =>
[
    new(OpCode.Constant, 0), new(OpCode.MakeClosure, 1, 1), new(OpCode.StoreLocal, 0), new(OpCode.Null), new(OpCode.Return)
];

static Instruction[] Consume() =>
[
    new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 1, Text: "ranges.first"), new(OpCode.Return)
];

void Require(bool condition, string description)
{
    checks++;
    if (!condition) throw new InvalidOperationException(description);
}
