using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

if (args.Contains("--emission"))
{
    Console.WriteLine($"PASS {EmissionChecks.Run()} descriptor ABI and native emission checks");
    return;
}

if (args.Contains("--guest-heap"))
{
    GuestHeapChecks.Run();
    await GuestHeapChecks.HostLifetimeAsync();
    Console.WriteLine("PASS native heap coalescing, pinned identity, and CLR-allocation-free reuse");
    return;
}

if (args.Contains("--unary-math"))
{
    Console.WriteLine($"PASS {await UnaryMathChecks.RunAsync()} direct scalar math Native2 and pending properties");
    return;
}

if (args.Contains("--floating-predicates"))
{
    Console.WriteLine($"PASS {await FloatingPredicateChecks.RunAsync()} binary64 predicate Native2 and pending properties");
    return;
}

if (args.Contains("--ordinary-initialization"))
{
    Console.WriteLine($"PASS {OrdinaryInitializationChecks.Run()} ordinary initialization properties; {await OrdinaryInitializationChecks.RunSourceAsync()} source artifact journeys");
    return;
}

if (args.Contains("--register-demand"))
{
    Console.WriteLine($"PASS {await RegisterDemandChecks.RunAsync()} register planning demand properties");
    return;
}

if (args.Contains("--machine-scalars"))
{
    Console.WriteLine($"PASS {await MachineScalarReadinessChecks.RunAsync()} machine scalar readiness; {await MachinePendingChecks.RunAsync()} machine operation pending properties");
    return;
}

if (args.Contains("--function-parts"))
{
    Console.WriteLine($"PASS {await FunctionPartsPendingChecks.RunAsync()} Function component pending properties");
    return;
}

if (args.Contains("--selection"))
{
    Console.WriteLine($"PASS {await SelectChecks.RunAsync()} LLVM/Native2 selection and pending resource properties");
    return;
}

if (args.Contains("--argument-sources"))
{
    ArgumentSourceChecks.Run();
    return;
}

if (args.Contains("--private-initialization"))
{
    Console.WriteLine($"PASS {await PrivateInitializationChecks.RunAsync()} private initialization properties");
    return;
}

if (args.Contains("--materialization"))
{
    Console.WriteLine($"PASS {ReadyMaterializationChecks.Run()} private Future materialization properties");
    return;
}

int checks = EmissionChecks.Run() + FrameBindingChecks.Run() + RegionCopyChecks.Run();
checks += await UnaryMathChecks.RunAsync();
checks += await NumberAccessChecks.RunAsync();
checks += await RegisterRegionChecks.RunAsync();
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task<(NativeValue Value, NativeTierStatistics Statistics, bool RanSsa)> Run(string source, NativeJitPolicy policy,
    string input = "", string? expectedOutput = null)
{
    NativeProgram program = Compiler.Compile(source, "tier-journey.msync", 2);
    using MemoryStream artifact = new();
    NativeArtifact.Write(program, artifact); artifact.Position = 0;
    using StringWriter output = new();
    using StringWriter errors = new();
    RuntimeProfile profile = new();
    await using NativeInterpreter interpreter = new(NativeArtifact.Read(artifact), new(Output: output, Error: errors)
    {
        Input = new StringReader(input),
        Profile = profile,
        Tiers = new(policy, CallThreshold: 1, BackedgeThreshold: 2, RecompileThreshold: 2)
    });
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    NativeValue result = await interpreter.ExecuteAsync(timeout.Token);
    Check(errors.ToString().Length == 0, errors.ToString());
    if (expectedOutput is not null) Check(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) == expectedOutput, output.ToString());
    Check(interpreter.FailedTransfers == 0, "Unexpected transfer failure");
    bool ranSsa = profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("jit_ssa_calls") > 0);
    return (result, interpreter.TierStatistics, ranSsa);
}

const string NumericLoop = "let x=0; let total=0; loop { if x>=100 {break;}; total=total+x; x=x+1; }; total";
foreach (string source in new[]
{
    NumericLoop,
    "let row=scope { count: 1, text: \"原生𐀀\", file: ./x, endpoint: $local }; let alias=row; alias::count=8; row::count",
    "fn make(n: Number) { let x=n; fn() { x=x+1; x } }; let f=make(3); f()*10+f()",
    "let x?=null; if x is null { true } else { false }",
    "$std::ranges::size($std::ranges::materialize($std::ranges::transform([1,2,3], fn(n:Number) {n+1})))"
})
{
    var baseline = await Run(source, NativeJitPolicy.Off);
    var tiered = await Run(source, NativeJitPolicy.Force);
    var linked = await Run(source, NativeJitPolicy.Linked);
    Check(baseline.Value == tiered.Value, "Native code changed a source result");
    Check(baseline.Value == linked.Value, "Linked execution changed a source result");
    if (source == NumericLoop)
    {
        Check(tiered.RanSsa || tiered.Statistics.DirectOperations > 0, "Forced native code was not entered");
        Check(linked.Statistics.LinkedEntries > 0 && linked.Statistics.LinkedOperations > 0, "Linked tier was not entered");
    }
    Check(baseline.Statistics.Entries == 0 && baseline.Statistics.Compilations == 0 && !baseline.RanSsa,
        "JIT-off generated native code");
    Check(linked.Statistics.Compilations == 0 && !linked.RanSsa, "Linked tier was mislabeled as native compilation");
}

await Guards();
await NumericAbi();
await PendingDestination();
checks += await RawWriteChecks.RunAsync();
checks += await TypedLoadChecks.RunAsync();
checks += await NarrowChecks.RunAsync();
checks += await ExecutionLoweringChecks.RunAsync();
checks += await ProducerLoweringChecks.RunAsync();
checks += await ReadinessChecks.RunAsync();
await SuspensionAndGeneration();
await SelectedEffects();
await ConcurrentVersions();
await Profiling();
await Cancellation();
foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Linked, NativeJitPolicy.Force })
{
    var tail = await Run("fn walk(n: Number, acc: Number) { if n == 0 { acc } else { walk(n - 1, acc + 1) } }; walk(5000, 0)", policy);
    Check(tail.Value.AsNumber() == 5000, "An execution tier reintroduced recursive host frames at a tail boundary");
    Check(policy != NativeJitPolicy.Force || tail.RanSsa || tail.Statistics.Entries > 0, "Tail boundary was not tested after native entry");
}

Console.WriteLine($"PASS {checks} native tier journeys");

async Task Guards()
{
    NativeProgram calls = new([new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 2), new(NativeKind.Number, Number: 0)],
        [new("dispatch", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(1), new(1)],
            [new(NativeOp.Call, 2, 0, Symbol: FunctionDispatch.Encode(new Dictionary<int, int> { [1] = 1, [2] = 2 }), Inputs: [1]),
             new(NativeOp.Constant, 3, 2), new(NativeOp.Binary, 3, 2, 3, Immediate: (long)NativeBinary.Add),
             new(NativeOp.Return, A: 3)], new([0, 1], 1, []), ParameterCount: 2),
         new("first", [new(1, NativeSlotRole.Parameter), new(1), new(1)],
            [new(NativeOp.Constant, 1, 0), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)],
            new([1], 1, []), ParameterCount: 1),
         new("second", [new(1, NativeSlotRole.Parameter), new(1), new(1)],
            [new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)],
            new([1], 1, []), ParameterCount: 1)], [new(TypeKind.Function), new(TypeKind.Number)], []);
    calls = TierFixture.Plan(calls);
    NativeArtifact.Validate(calls);
    await using (NativeInterpreter interpreter = new(calls, new() { Tiers = new(NativeJitPolicy.Force, 2, 2, 2) }))
    {
        foreach (int target in new[] { 1, 1, 2, 2, 2 })
        {
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromFunction(new(target)), NativeValue.FromNumber(8)]);
            Check(result.AsNumber() == 8 + target, "Call speculation captured a stale target or value");
        }
        Check(interpreter.TierStatistics.GuardMisses == 0 && interpreter.TierStatistics.Deoptimizations == 0,
            "An indirect helper call acquired a speculative guard without specializing its target");
        Check(interpreter.TierStatistics.Recompilations == 0, "An indirect helper call invalidated otherwise reusable code");
    }
    NativeProgram kinds = new([], [new("dynamic-add", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(0)],
        [new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)],
        new([0, 0], 0, []), ParameterCount: 2)], [new(TypeKind.Number | TypeKind.String)], []);
    kinds = TierFixture.Plan(kinds);
    NativeArtifact.Validate(kinds);
    await using (NativeInterpreter interpreter = new(kinds, new() { Tiers = new(NativeJitPolicy.Force, 2, 2, 2) }))
    {
        for (int i = 0; i < 2; i++) Check((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromNumber(4), NativeValue.FromNumber(5)])).AsNumber() == 9, "Union Number specialization changed addition");
        Check((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromString("Klee"), NativeValue.FromString("𐀀")])).AsString() == "Klee𐀀", "Kind guard failed to restore String addition");
        Check(interpreter.TierStatistics.GuardMisses > 0, "A changed union alternative did not deoptimize");
    }
    NativeProgram prefix = new([new(NativeKind.Number, Number: 100)],
        [new("raw-prefix", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(1), new(0), new(2), new(3), new(4)],
            [new(NativeOp.Constant, 2, 0), new(NativeOp.Binary, 3, 0, 1, Immediate: (long)NativeBinary.Add),
             new(NativeOp.Allocate, 4, 0), new(NativeOp.AddressOffset, 5, 4, 0, 0), new(NativeOp.Store, A: 5, B: 2, C: 1, Type: 1),
             new(NativeOp.AddressOffset, 6, 4, 0, 1), new(NativeOp.Store, A: 6, B: 3, C: 1, Type: 0), new(NativeOp.Return, A: 4)],
            new([0, 0], 2, []), ParameterCount: 2)],
        [new(TypeKind.Number | TypeKind.String), new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]),
         new(TypeKind.Never, Pointee: 1), new(TypeKind.Never, Pointee: 0)], [new([new("prefix", 1), new("result", 0)])]);
    prefix = TierFixture.Plan(prefix);
    NativeArtifact.Validate(prefix);
    await using (NativeInterpreter interpreter = new(prefix, new() { Tiers = new(NativeJitPolicy.Force, 2, 2, 2) }))
    {
        for (int i = 0; i < 2; i++) _ = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromNumber(1), NativeValue.FromNumber(2)]);
        NativeAddress result = (await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromString("prefix"), NativeValue.FromString("-retained")])).AsAddress();
        Check(result.Field(0).Load().AsNumber() == 100 && result.Field(1).Load().AsString() == "prefix-retained",
            "Guard deoptimization lost an already committed raw value or resumed after the failing operation");
        Check(interpreter.TierStatistics.RawOperations > 0 && interpreter.TierStatistics.GuardMisses > 0,
            "Raw-prefix deoptimization control did not execute both paths");
    }
    NativeProgram fields = new([], [new("read-field", [new(1, NativeSlotRole.Parameter), new(2), new(0)],
        [new(NativeOp.ProjectAddress, 1, 0, 0), new(NativeOp.Load, 2, 1, Type: 0), new(NativeOp.Return, A: 2)],
        new([1], 0, []), ParameterCount: 1)],
        [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0, 1]), new(TypeKind.Never, Pointee: 0)],
        [new([new("n", 0)]), new([new("other", 0), new("n", 0)])])
    { Projections = [new([new(0, 0), new(1, 1)])] };
    fields = TierFixture.Plan(fields);
    NativeArtifact.Validate(fields);
    await using (NativeInterpreter interpreter = new(fields, new() { Tiers = new(NativeJitPolicy.Force, 2, 2, 2) }))
    {
        NativeAllocation a = new(new NativeLayout([new NativeField("n", NativeType.Of(NativeKind.Number))]), 0);
        NativeAllocation b = new(new NativeLayout([new NativeField("other", NativeType.Of(NativeKind.Number)),
            new NativeField("n", NativeType.Of(NativeKind.Number))]), 1);
        a.Field(0).Store(NativeValue.FromNumber(7)); b.Field(0).Store(NativeValue.FromNumber(99)); b.Field(1).Store(NativeValue.FromNumber(13));
        foreach (NativeAllocation value in new[] { a, a, b })
            Check((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(value.Address)])).AsNumber()
                == (ReferenceEquals(value, a) ? 7 : 13), "Layout speculation used an offset from the wrong namespace");
        Check(interpreter.TierStatistics.GuardMisses == 0, "A dynamic layout selection acquired a redundant speculation guard");
    }
}

async Task NumericAbi()
{
    double[] values = [0, -0d, 1, -7, double.Epsilon, double.MaxValue, double.PositiveInfinity, double.NegativeInfinity,
        BitConverter.UInt64BitsToDouble(0x7ff8000000001234), BitConverter.UInt64BitsToDouble(0xfff8000000005678)];
    foreach (NativeBinary operation in new[] { NativeBinary.Add, NativeBinary.Subtract, NativeBinary.Multiply, NativeBinary.Divide,
        NativeBinary.Equal, NativeBinary.NotEqual, NativeBinary.Less, NativeBinary.LessEqual, NativeBinary.Greater, NativeBinary.GreaterEqual })
    {
        bool comparison = operation >= NativeBinary.Equal;
        NativeProgram program = new([], [new("numeric-abi", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(comparison ? 1 : 0)],
            [new(NativeOp.Binary, 2, 0, 1, Immediate: (long)operation), new(NativeOp.Return, A: 2)],
            new([0, 0], comparison ? 1 : 0, []), ParameterCount: 2)], [new(TypeKind.Number), new(TypeKind.Bool)], []);
        program = TierFixture.Plan(program);
        await using NativeInterpreter baseline = new(program, new() { Tiers = new(NativeJitPolicy.Off) });
        await using NativeInterpreter native = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        foreach (double first in values)
            foreach (double second in values)
            {
                NativeValue[] arguments = [NativeValue.FromNumber(first), NativeValue.FromNumber(second)];
                NativeValue expected = await baseline.InvokeAsync(NativeValue.FromFunction(new(0)), arguments);
                NativeValue actual = await native.InvokeAsync(NativeValue.FromFunction(new(0)), arguments);
                Check(actual == expected, $"Native {operation} changed IEEE bits for {first}/{second}");
            }
        Check(native.TierStatistics.DirectOperations > 0, "Numeric ABI test did not run native instructions");
        Check(native.TierStatistics.RawOperations > 0, "Private numeric ABI test did not use guarded canonical raw writes");
    }
}

async Task PendingDestination()
{
    NativeProgram program = new([new(NativeKind.Number, Number: 9), new(NativeKind.Number, Number: 1)],
        [new("pending-destination", [new(0, NativeSlotRole.Parameter), new(1), new(1), new(1), new(1)],
            [new(NativeOp.CallNative, 1, Symbol: "process.exit_code", Inputs: [0]),
             new(NativeOp.CallNative, 2, Symbol: "math.abs", Inputs: [1]), new(NativeOp.Constant, 2, 0),
             new(NativeOp.Constant, 3, 1), new(NativeOp.Binary, 4, 2, 3, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 4)],
            new([0], 1, []), ParameterCount: 1)], [new(TypeKind.Process), new(TypeKind.Number)], []);
    program = TierFixture.Plan(program);
    NativeArtifact.Validate(program);
    TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
    Task<NativeValue> result = interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
        [NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask();
    try
    {
        Check((await result.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() == 10,
            "A canonical raw write bypassed pending metadata cleanup or forced a discarded pure computation");
    }
    finally { completion.TrySetResult(new(7, "", "")); }
}

async Task SuspensionAndGeneration()
{
    NativeProgram program = new([new(NativeKind.Number, Number: 7)], [new("generation", [new(0), new(1), new(2)],
        [new(NativeOp.CallNative, 0, Symbol: "io.read_line", Inputs: []), new(NativeOp.Constant, 1, 0),
         new(NativeOp.Unary, 2, 0, Immediate: (long)NativeUnary.IsNull), new(NativeOp.Branch, A: 2, B: 4, C: 5),
         new(NativeOp.Return, A: 1), new(NativeOp.Return, A: 1)],
        new([], 1, []))], [new(TypeKind.String | TypeKind.Null), new(TypeKind.Number), new(TypeKind.Bool)], []);
    program = TierFixture.Plan(program);
    GateReader reader = new();
    await using NativeInterpreter interpreter = new(program, new() { Input = reader, Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
    Task<NativeValue> old = interpreter.ExecuteAsync().AsTask();
    await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!old.IsCompleted, "Native runtime helper did not suspend");
    interpreter.UpdateProgram(program with { Constants = [new(NativeKind.Number, Number: 999)] });
    reader.Release.SetResult("continue");
    Check((await old.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() == 7, "Suspended native activation switched image generation");
    Check((await interpreter.ExecuteAsync()).AsNumber() == 999, "New activation retained the old native image");
    Check(reader.Reads == 2, "Suspending helper replayed a selected read");
}

async Task Cancellation()
{
    NativeProgram program = new([], [new("cancel", [], [new(NativeOp.Jump, A: 0)], new([], 0, []))], [new(TypeKind.Null)], []);
    program = TierFixture.Plan(program);
    await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
    using CancellationTokenSource cancellation = new();
    Task<NativeValue> execution = Task.Run(async () => await interpreter.ExecuteAsync(cancellation.Token));
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
    try
    {
        while (interpreter.TierStatistics.Operations < 256) await Task.Delay(1, timeout.Token);
        Check(!execution.IsCompleted, "Native helper-only loop stopped instead of publishing bounded progress");
    }
    finally { cancellation.Cancel(); }
    try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Native loop ignored cancellation"); }
    catch (OperationCanceledException) { }
    Check(interpreter.TierStatistics.CancellationPolls > 0 && interpreter.TierStatistics.Entries > 0, "Cancellation did not exercise native code polls");
}

async Task SelectedEffects()
{
    string directory = Path.GetFullPath(Path.Combine(".temp", "native-tiering-effects-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(directory);
    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int transfers = 0;
    NativeRuntimeContext context = new(new(WorkingDirectory: directory));
    context.TransferAsync = async (value, destination, origin, token) =>
    {
        Interlocked.Increment(ref transfers);
        started.SetResult();
        await release.Task.WaitAsync(token);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        await File.WriteAllTextAsync(origin.LocalPath(destination), value.AsString(), token);
        return true;
    };
    NativeProgram program = Compiler.Compile("\"payload𐀀\" -> ./published; $std::fs::exists(./published)", "tier-effect.msync", 0);
    await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) }, context);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
    Task<NativeValue> execution = interpreter.ExecuteAsync(timeout.Token).AsTask();
    await started.Task.WaitAsync(timeout.Token);
    Check(!execution.IsCompleted, "Native I/O helper did not retain its pending operation");
    release.SetResult();
    Check((await execution.WaitAsync(timeout.Token)).AsBool(), "Native continuation queried metadata before selected publication");
    Check(transfers == 1 && File.ReadAllText(Path.Combine(directory, "published")) == "payload𐀀",
        "Native deoptimization or await replayed an effect or lost a GC root");
    if (!OperatingSystem.IsWindows())
    {
        var result = await Run("""
            fn launch() { /bin/sh("-c", "printf Klee") };
            let process = launch();
            $std::io::println(process::stdout);
            process::exit_code
            """, NativeJitPolicy.Force, expectedOutput: "Klee\n");
        Check(result.Value.AsNumber() == 0, "Native tier lost an escaped Process result or ownership");
    }
}

async Task ConcurrentVersions()
{
    NativeProgram program = new([new(NativeKind.Number, Number: 0), new(NativeKind.Number, Number: 1)],
        [new("caller", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(1), new(1), new(1), new(2), new(1)],
            [new(NativeOp.Constant, 2, 0), new(NativeOp.Constant, 3, 0), new(NativeOp.Constant, 4, 1),
             new(NativeOp.Binary, 5, 2, 1, Immediate: (long)NativeBinary.Less), new(NativeOp.Branch, A: 5, B: 5, C: 9),
             new(NativeOp.Call, 6, 0, Symbol: FunctionDispatch.Encode(new Dictionary<int, int> { [1] = 1, [2] = 2 }), Inputs: [2]),
             new(NativeOp.Binary, 3, 3, 6, Immediate: (long)NativeBinary.Add),
             new(NativeOp.Binary, 2, 2, 4, Immediate: (long)NativeBinary.Add), new(NativeOp.Jump, A: 3), new(NativeOp.Return, A: 3)],
            new([0, 1], 1, []), ParameterCount: 2),
         new("first", [new(1, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], new([1], 1, []), ParameterCount: 1),
         new("second", [new(1, NativeSlotRole.Parameter), new(1), new(1)],
            [new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)],
            new([1], 1, []), ParameterCount: 1)], [new(TypeKind.Function), new(TypeKind.Number), new(TypeKind.Bool)], []);
    program = TierFixture.Plan(program);
    NativeArtifact.Validate(program);
    await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 2, 8, 8) });
    _ = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromFunction(new(1)), NativeValue.FromNumber(1)]);
    Task<NativeValue>[] calls = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromFunction(new(index % 2 + 1)), NativeValue.FromNumber(300)]))).ToArray();
    NativeValue[] results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));
    for (int index = 0; index < results.Length; index++)
        Check(results[index].AsNumber() == 300 * 299 / 2 + (index % 2 == 0 ? 0 : 300),
            "Concurrent invalidation reused another call's values or freed executing code");
    Check(interpreter.TierStatistics.Entries > 0 && interpreter.TierStatistics.GuardMisses == 0,
        "Concurrent indirect calls should execute native code without pointless target invalidation");
}

async Task Profiling()
{
    NativeProgram program = Compiler.Compile("let x=0; let sum=0; loop { if x>=40 {break;}; sum=sum+x; x=x+1; }; sum", "tier-profile.msync", 0);
    Dictionary<NativeJitPolicy, Dictionary<string, ulong>> counts = [];
    foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
    {
        RuntimeProfile profile = new(instructionSites: true);
        await using NativeInterpreter interpreter = new(program, new() { Profile = profile, Tiers = new(policy, 1, 1, 1) });
        Check((await interpreter.ExecuteAsync()).AsNumber() == 780, "Profiling changed tier output");
        IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
        counts[policy] = samples.Where(sample => sample.Name == "vm.opcode")
            .GroupBy(sample => sample.Attributes["opcode"]).ToDictionary(group => group.Key, group => group.Aggregate(0UL, (sum, sample) => sum + sample.Metrics["hits"]));
        ulong functions = samples.Where(sample => sample.Name == "vm.function").Aggregate(0UL, (sum, sample) => sum + sample.Metrics["instructions"]);
        ulong sites = samples.Where(sample => sample.Name == "vm.instruction").Aggregate(0UL, (sum, sample) => sum + sample.Metrics["hits"]);
        Check(functions == sites && functions == counts[policy].Values.Aggregate(0UL, (sum, value) => sum + value),
            "Tier function, opcode and source-site counts diverged");
        ulong linked = samples.Where(sample => sample.Name == "vm.function").Aggregate(0UL, (sum, sample) => sum + sample.Metrics["linked_operations"]);
        ulong native = samples.Where(sample => sample.Name == "vm.function").Aggregate(0UL, (sum, sample) => sum + sample.Metrics["jit_operations"]);
        Check((linked > 0) == (policy == NativeJitPolicy.Linked) && (native > 0) == (policy == NativeJitPolicy.Force),
            "Execution tier counters describe an engine that did not run");
    }
    foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Linked, NativeJitPolicy.Force })
        Check(counts[policy].OrderBy(pair => pair.Key).SequenceEqual(counts[NativeJitPolicy.Off].OrderBy(pair => pair.Key)),
            "Tier selection changed logical opcode accounting");
}

internal sealed class GateReader : TextReader
{
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<string?> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Reads;
    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref Reads) != 1) return new((string?)null);
        Started.SetResult();
        return new(Release.Task.WaitAsync(cancellationToken));
    }
}
