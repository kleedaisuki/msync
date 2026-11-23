using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;
using Msync.Runtime.Native;

if (args.Contains("--pointer-storage", StringComparer.Ordinal))
{
    await PointerStorageChecks.RunAsync();
    return;
}

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.temp/native-execution"));
Directory.CreateDirectory(root);
if (args.Contains("--future-identity", StringComparer.Ordinal))
{
    await FutureIdentityChecks.RunAsync(root);
    return;
}
int checks = 0;

async Task<NativeValue> Run(string name, string source, string? expectedOutput = null, string input = "")
{
    string directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, "program.msync");
    File.WriteAllText(file, source);
    NativeProgram program = Compiler.CompileNative(source, file);
    File.WriteAllText(Path.Combine(directory, "instructions.txt"), string.Join('\n', program.Functions.SelectMany(
        function => function.Instructions.Select(instruction => $"{function.Name}: {instruction}"))));
    StringWriter output = new(CultureInfo.InvariantCulture);
    StringWriter errors = new(CultureInfo.InvariantCulture);
    using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
    await using NativeInterpreter interpreter = new(program, new(directory, file, Output: output, Error: errors)
    { Input = new StringReader(input) });
    NativeValue value = await interpreter.ExecuteAsync(cancellation.Token);
    if (expectedOutput is not null && output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) != expectedOutput)
        throw new Exception($"{name}: expected output {expectedOutput}, got {output}; errors={errors}");
    if (errors.ToString().Length != 0) throw new Exception($"{name}: unexpected errors {errors}");
    if (interpreter.FailedTransfers != 0) throw new Exception($"{name}: a transfer failed");
    checks++;
    Console.WriteLine($"PASS {name}");
    return value;
}

void Number(NativeValue actual, double expected)
{
    if (actual.Kind != NativeKind.Number || BitConverter.DoubleToInt64Bits(actual.AsNumber()) != BitConverter.DoubleToInt64Bits(expected))
        throw new Exception($"Expected Number {expected}, received {actual} ({actual.Kind})");
    checks++;
}

Number(await Run("native-fields", """
    let row = scope { n: 1, text: "data", path: ./item, endpoint: $local, flag: true, type: Number, none?: null };
    let alias = row;
    alias::n = 42;
    row::n
    """), 42);

Number(await Run("captured-locations", """
    fn counter(seed: Number) {
        let n = seed;
        scope { next: fn() { n = n + 1; n } }
    };
    let first = counter(0);
    let second = counter(10);
    first::next() + first::next() * 10 + second::next() * 100
    """), 1121);

Number(await Run("captured-parameter", """
    fn change(n: Number) { fn() { n = n + 3; n } };
    let next = change(1);
    next() + next()
    """), 11);

Number(await Run("recursive-call", """
    fn factorial(n: Number) -> Number { if n <= 1 { 1 } else { n * factorial(n - 1) } };
    factorial(7)
    """), 5040);

Number(await Run("scope-rebind", """
    let original = scope { number: 4 };
    let selected = original;
    let alias = selected;
    selected = scope { number: 8 };
    alias::number = 6;
    original::number * 10 + selected::number
    """), 68);

await Run("all-kinds-call-return", """
    fn identity(value?) { value };
    let result = scope {
        number: identity(4), text: identity("hello"), flag: identity(true),
        path: identity(./bound), endpoint: identity($local), type: identity(Number),
        values: identity([1, 2, 3]), function: identity(fn(value) { value }), none?: identity(null)
    };
    $std::io::println(result::text);
    $std::io::println(result::path as String);
    $std::io::println(result::endpoint as String);
    $std::io::println(result::flag as String);
    $std::io::println($std::ranges::size(result::values) as String);
    $std::io::println(result::function(result::number) as String);
    $std::io::println((result::none is null) as String);
    """, "hello\n./bound\nlocal\ntrue\n3\n4\ntrue\n");

await Run("lazy-stdin", """
    let values = $std::ranges::transform($std::io::stdin_lines(), fn(line: String) { line as Number });
    let count = 0;
    for value in $std::ranges::take(values, 3) { count = count + value; };
    $std::io::println(count as String);
    """, "12\n", "2\n4\n6\nnot-a-number\n");

await Run("actual-copy", """
    "published bytes" -> result.txt;
    $std::io::println($std::io::read_text(result.txt));
    """, "published bytes\n");
if (File.ReadAllText(Path.Combine(root, "actual-copy/result.txt")) != "published bytes")
    throw new Exception("Native transfer published wrong bytes");
checks++;

if (!OperatingSystem.IsWindows())
{
    await Run("returned-waiting-process", """
        fn launch() {
            let child = $std::process::spawn(/bin/sh, ["-c", "IFS= read -r value; printf 'reply:%s' $value"]);
            let ignored = child::exit_code;
            scope { child: child }
        };
        let owner = launch();
        $std::process::write_stdin(owner::child, "hello");
        $std::process::close_stdin(owner::child);
        $std::io::println($std::process::stdout(owner::child));
        """, "reply:hello\n");
}

await MemoryRegionJourney();
await EnvironmentJourney();
await PendingMemoryJourney();
await ProfileJourney();
await TypeWitnessJourney();
await ClosedNamespaceJourney();
checks += await NamespaceLayoutChecks.RunAsync();
checks += await PointerStorageChecks.RunAsync();
checks += await NativeMetadataChecks.RunAsync();
checks += await IteratorLifecycleChecks.RunAsync();
checks += await WorkLifecycleChecks.RunAsync();
checks += await SchedulingChecks.RunAsync();
checks += await RangeDatumChecks.RunAsync(root);
checks += await FutureIdentityChecks.RunAsync(root);
await SuspendedGenerationJourney();
await FailedTransferJourney();
if (!OperatingSystem.IsWindows())
{
    await ProcessLifetimeJourney();
    await PrivateSessionProcessJourney();
    Directory.CreateDirectory(Path.Combine(root, "pending-process-range"));
    File.Delete(Path.Combine(root, "pending-process-range", "ready"));
    await Run("pending-process-range", """
        let child = release /bin/sh("-c", "while [ ! -f ready ]; do sleep 0.01; done; exit 7");
        let result = 0;
        for value in [child::exit_code] { result = $std::math::abs(value); };
        $std::io::write_text(./ready, "go");
        $std::io::println(result as String);
        """, "7\n");
}

async Task MemoryRegionJourney()
{
    NativeCodeType[] types = [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]),
        new(TypeKind.Never, Pointee: 0), new(TypeKind.Range, Element: 0)];
    NativeProgram program = new(
        [new(NativeKind.Number, Number: 3), new(NativeKind.Number, Number: 0),
         new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 2),
         new(NativeKind.Number, Number: 4), new(NativeKind.Number, Number: 6)],
        [new("regions", [new(0), new(0), new(0), new(0), new(0), new(0), new(1), new(1), new(2), new(3)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
             new(NativeOp.Constant, 2, 2), new(NativeOp.Constant, 3, 3),
             new(NativeOp.Constant, 4, 4), new(NativeOp.Constant, 5, 5),
             new(NativeOp.Allocate, 6, 0, C: 0),
             new(NativeOp.AddressOffset, 8, 6, 0, 0), new(NativeOp.Store, A: 8, B: 3, C: 1, Type: 0),
             new(NativeOp.IndexAddress, 7, 6, 2, 0), new(NativeOp.AddressOffset, 8, 7, 0, 0),
             new(NativeOp.Store, A: 8, B: 4, C: 1, Type: 0),
             new(NativeOp.IndexAddress, 7, 6, 3, 0), new(NativeOp.AddressOffset, 8, 7, 0, 0),
             new(NativeOp.Store, A: 8, B: 5, C: 1, Type: 0),
             new(NativeOp.IndexAddress, 7, 6, 2, 0),
             new(NativeOp.CopyMemory, A: 7, B: 6, C: 3, Immediate: 0),
             new(NativeOp.RangeFromMemory, 9, 6, 0, 0, Type: 3),
             new(NativeOp.ClearMemory, A: 6, C: 0, Immediate: 0), new(NativeOp.Return, A: 9)],
             new([], 3, []))], types, [new([new("item", 0)])]);
    using MemoryStream artifact = new();
    NativeArtifact.Write(program, artifact);
    artifact.Position = 0;
    await using NativeInterpreter interpreter = new(NativeArtifact.Read(artifact));
    NativeRange result = (await interpreter.ExecuteAsync()).AsRange();
    List<double> actual = [];
    await foreach (NativeValue value in result.Items) actual.Add(value.AsNumber());
    if (!actual.SequenceEqual([2d, 2d, 4d]))
        throw new Exception($"Overlapping copy/snapshot/clear changed region membership: {string.Join(',', actual)}");
    checks++;
    foreach (bool move in new[] { false, true })
    {
        NativeInstruction[] instructions = program.Functions[0].Instructions
            .Where(instruction => !move || instruction.Op != NativeOp.ClearMemory)
            .Select(instruction => instruction.Op switch
            {
                NativeOp.AddressOffset => instruction with { C = 1 },
                NativeOp.RangeFromMemory => instruction with { Immediate = 1, Symbol = move ? "move" : null },
                _ => instruction
            }).ToArray();
        NativeProgram rows = program with
        {
            Types = [.. program.Types, new(TypeKind.Bool)],
            Layouts = [new([new("uninitialized_flag", 4), new("item", 0)])],
            Functions = [program.Functions[0] with { Instructions = instructions }]
        };
        using MemoryStream rowsArtifact = new();
        NativeArtifact.Write(rows, rowsArtifact);
        rowsArtifact.Position = 0;
        await using NativeInterpreter rowInterpreter = new(NativeArtifact.Read(rowsArtifact));
        NativeRange snapshot = (await rowInterpreter.ExecuteAsync()).AsRange();
        List<double> members = [];
        await foreach (NativeValue value in snapshot.Items) members.Add(value.AsNumber());
        if (!members.SequenceEqual([2d, 2d, 4d]))
            throw new Exception($"Selected AoS field lost { (move ? "owned" : "copied") } membership");
        checks++;
    }
    foreach (double invalid in new[] { -1d, 0.5d, double.NaN, double.PositiveInfinity, (double)int.MaxValue + 1 })
    {
        NativeProgram badCount = program with { Constants = [new(NativeKind.Number, Number: invalid), .. program.Constants.Skip(1)] };
        await using NativeInterpreter rejected = new(badCount);
        try
        {
            await rejected.ExecuteAsync();
            throw new Exception($"Invalid native memory count {invalid} was accepted");
        }
        catch (MsyncException error) when (error.Message.Contains("non-negative integer", StringComparison.Ordinal)) { checks++; }
    }
    NativeProgram bounds = program with { Constants = [.. program.Constants.Take(2), new(NativeKind.Number, Number: 3), .. program.Constants.Skip(3)] };
    await using (NativeInterpreter rejected = new(bounds))
    {
        try
        {
            await rejected.ExecuteAsync();
            throw new Exception("Out-of-bounds native index was accepted");
        }
        catch (MsyncException error) when (error.Message.Contains("exceeds its owning allocation", StringComparison.Ordinal)) { checks++; }
    }
    Console.WriteLine("PASS dynamic-regions: detached artifact -> overlapping copy -> snapshot -> clear");
}

async Task EnvironmentJourney()
{
    NativeProgram program = new([new(NativeKind.Number, Number: 17)],
        [new("environment", [new(0), new(1), new(2), new(3), new(0)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.MakeFunction, 1, 1, Inputs: [0]),
             new(NativeOp.EnvironmentAddress, 2, 1, 0), new(NativeOp.AddressOffset, 3, 2, 0, 0),
             new(NativeOp.Load, 4, 3, Type: 0), new(NativeOp.Return, A: 4)], new([], 0, [])),
         new("capture", [new(0, NativeSlotRole.Capture)], [new(NativeOp.Return, A: 0)],
             new([], 0, [new(0, NativeCaptureMode.Value)]), CaptureCount: 1)],
        [new(TypeKind.Number), new(TypeKind.Function), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
        [new([new("0", 0)])]);
    using MemoryStream artifact = new();
    NativeArtifact.Write(program, artifact);
    artifact.Position = 0;
    await using NativeInterpreter interpreter = new(NativeArtifact.Read(artifact));
    Number(await interpreter.ExecuteAsync(), 17);
    Console.WriteLine("PASS environment-address: detached artifact -> typed capture address -> load");
}

async Task PendingMemoryJourney()
{
    TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    NativeRuntimeContext context = new()
    {
        TransferAsync = async (_, _, _, token) => await completion.Task.WaitAsync(token)
    };
    NativeProgram program = new([new(NativeKind.Number, Number: 1), new(NativeKind.String, Text: "payload"),
        new(NativeKind.Path, Text: "destination")],
        [new("pending-memory", [new(0), new(1), new(2), new(3), new(3), new(4), new(5), new(6)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1), new(NativeOp.Constant, 2, 2),
             new(NativeOp.Allocate, 3, 0), new(NativeOp.Allocate, 4, 0),
             new(NativeOp.AddressOffset, 5, 3, 0, 0), new(NativeOp.Transfer, 6, 1, 2),
             new(NativeOp.Store, A: 5, B: 6, C: 1, Type: 5),
             new(NativeOp.CopyMemory, A: 4, B: 3, C: 0, Immediate: 0),
             new(NativeOp.RangeFromMemory, 7, 4, 0, 0, Type: 6),
             new(NativeOp.ClearMemory, A: 3, C: 0, Immediate: 0),
             new(NativeOp.ClearMemory, A: 4, C: 0, Immediate: 0), new(NativeOp.Return, A: 7)], new([], 6, []))],
        [new(TypeKind.Number), new(TypeKind.String), new(TypeKind.Path), new(TypeKind.Scope, Layouts: [0]),
         new(TypeKind.Never, Pointee: 5), new(TypeKind.Bool), new(TypeKind.Range, Element: 5)],
        [new([new("pending", 5)])]);
    await using NativeInterpreter interpreter = new(program, context: context);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
    NativeRange range = (await interpreter.ExecuteAsync(timeout.Token)).AsRange();
    if (completion.Task.IsCompleted) throw new Exception("Future fixture unexpectedly completed before demand");
    completion.SetResult(true);
    int count = 0;
    await foreach (NativeValue value in range.Items.WithCancellation(timeout.Token))
    {
        if (!value.AsBool()) throw new Exception("Pending copied future lost its eventual result");
        count++;
    }
    if (count != 1) throw new Exception("Pending snapshot lost membership");
    checks++;
    NativeFunctionCode scoped = program.Functions[0] with
    {
        Instructions = [.. program.Functions[0].Instructions.Take(8), new(NativeOp.Return, A: 3)],
        Signature = new([], 3, [])
    };
    await using NativeInterpreter lifetime = new(program with { Functions = [scoped] }, context: new()
    { TransferAsync = static (_, _, _, _) => new(true) });
    WeakReference<NativeAllocation> allocation = await CreateWeakAllocation(lifetime);
    for (int attempt = 0; attempt < 3 && AllocationAlive(allocation); attempt++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Yield();
    }
    if (AllocationAlive(allocation)) throw new Exception("Pending future side metadata retained an unreachable logical allocation");
    GC.KeepAlive(lifetime);
    checks++;
    Console.WriteLine("PASS pending-memory: unresolved transfer -> typed copy -> snapshot -> clear -> demand");
}

async Task ProfileJourney()
{
    RuntimeProfile profile = new(instructionSites: true);
    NativeProgram program = Compiler.CompileNative("""
        fn step(value: Number) { scope { n: value + 1 } };
        let result = step(4);
        $std::io::println(result::n as String);
        result::n
        """, optimization: 0);
    StringWriter output = new(CultureInfo.InvariantCulture);
    await using NativeInterpreter interpreter = new(program, new(Output: output) { Profile = profile });
    Number(await interpreter.ExecuteAsync(), 5);
    IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
    ulong instructions = samples.Where(sample => sample.Name == "vm.function").Aggregate(0UL,
        (sum, sample) => sum + sample.Metrics["instructions"]);
    ulong sites = samples.Where(sample => sample.Name == "vm.instruction").Aggregate(0UL,
        (sum, sample) => sum + sample.Metrics["hits"]);
    if (instructions == 0 || instructions != sites || !samples.Any(sample => sample.Name == "vm.call")
        || !samples.Any(sample => sample.Name == "vm.native" && sample.Attributes["native.name"] == "io.println" && sample.Metrics["calls"] == 1)
        || !samples.Any(sample => sample.Name == "vm.function" && sample.Metrics["logical_allocations"] > 0)
        || samples.Any(sample => sample.Attributes["representation"] != NativeProgram.Representation)
        || profile.Drain().Count != 0 || output.ToString().Trim() != "5")
        throw new Exception("Native runtime profiling lost executed sites, selected calls, native work or physical allocations");
    checks++;
    Console.WriteLine("PASS native-profile: actual instructions, selected call, builtin and logical allocation accounting");
}

async Task SuspendedGenerationJourney()
{
    TaskCompletionSource<bool> admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    NativeRuntimeContext context = new()
    {
        TransferAsync = async (_, _, _, token) =>
        {
            started.TrySetResult();
            return await admission.Task.WaitAsync(token);
        }
    };
    NativeProgram original = new([new(NativeKind.Number, Number: 17), new(NativeKind.String, Text: "payload"),
        new(NativeKind.Path, Text: "destination")],
        [new("entry", [], [new(NativeOp.Return)], new([], 4, [])),
         new("suspended", [new(0), new(1), new(2), new(3)],
            [new(NativeOp.Constant, 1, 1), new(NativeOp.Constant, 2, 2), new(NativeOp.Transfer, 0, 1, 2),
             new(NativeOp.Branch, A: 0, B: 4, C: 4), new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)], new([], 3, []))],
        [new(TypeKind.Bool), new(TypeKind.String), new(TypeKind.Path), new(TypeKind.Number), new(TypeKind.Null)], []);
    RuntimeProfile profile = new();
    await using NativeInterpreter interpreter = new(original, new() { Profile = profile }, context);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
    Task<NativeValue> pending = interpreter.InvokeAsync(NativeValue.FromFunction(new(1)), [], timeout.Token).AsTask();
    await started.Task.WaitAsync(timeout.Token);
    interpreter.UpdateProgram(original with { Constants = [new(NativeKind.Number, Number: 99), .. original.Constants.Skip(1)] });
    admission.SetResult(true);
    Number(await pending.WaitAsync(timeout.Token), 17);
    Number(await interpreter.InvokeAsync(NativeValue.FromFunction(new(1)), [], timeout.Token), 99);
    IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
    if (!samples.Any(sample => sample.Name == "vm.function" && sample.Metrics["dispatch_pauses"] > 0
        && sample.Metrics["dispatch_pauses"] == sample.Metrics["dispatch_continuations"]))
        throw new Exception("Actual suspension was not paired with its native continuation");
    checks++;
    Console.WriteLine("PASS image-generation: suspended frame retains old constants while later calls use updated image");
}

async Task TypeWitnessJourney()
{
    NativeProgram program = new([], [new("witness", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(1)],
        [new(NativeOp.Unary, 2, 0, 1, Immediate: (int)NativeUnary.IsType), new(NativeOp.Return, A: 2)],
        new([0, 0], 1, []), ParameterCount: 2)], [new(TypeKind.Type), new(TypeKind.Bool)], []);
    await using NativeInterpreter interpreter = new(program);
    TypeRef[] types = [TypeRef.Null, TypeRef.Bool, TypeRef.Number, TypeRef.String, TypeRef.Path,
        TypeRef.Connection, TypeRef.Function, new(TypeKind.Range), TypeRef.Process, TypeRef.Type];
    foreach (TypeRef actual in types)
        foreach (TypeRef expected in types)
        {
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromType(actual), NativeValue.FromType(expected)]);
            if (result.AsBool() != expected.Accepts(actual))
                throw new Exception($"Type witness identity tested wrapper rather than denotation: {actual} is {expected}");
            checks++;
        }
    foreach (TypeRef expected in types)
    {
        NativeProgram unknown = new([new(NativeKind.Type, Type: expected)],
            [new("typed-witness", [new(0, NativeSlotRole.Parameter), new(0), new(1)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Unary, 2, 0, 1, Immediate: (int)NativeUnary.IsType),
                 new(NativeOp.Return, A: 2)], new([0], 1, []), ParameterCount: 1)], program.Types, []);
        await using NativeInterpreter optimized = new(NativeOptimizer.Optimize(unknown));
        foreach (TypeRef actual in types)
        {
            NativeValue result = await optimized.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromType(actual)]);
            if (result.AsBool() != expected.Accepts(actual))
                throw new Exception($"Optimizer folded unknown Type denotation from wrapper kind: {actual} is {expected}");
            checks++;
            NativeProgram known = unknown with
            {
                Constants = [new(NativeKind.Type, Type: expected), new(NativeKind.Type, Type: actual)],
                Functions = [unknown.Functions[0] with
                {
                    Slots = [new(0), new(0), new(1)], ParameterCount = 0, Signature = new([], 1, []),
                    Instructions = [new(NativeOp.Constant, 0, 1), .. unknown.Functions[0].Instructions]
                }]
            };
            await using NativeInterpreter folded = new(NativeOptimizer.Optimize(known));
            if ((await folded.ExecuteAsync()).AsBool() != expected.Accepts(actual))
                throw new Exception($"Constant folding tested Type wrapper rather than denotation: {actual} is {expected}");
            checks++;
        }
    }
    Console.WriteLine("PASS type-witness: all builtin denotations through actual typed arguments and IsType dispatch");
}

async Task ClosedNamespaceJourney()
{
    foreach (NativeOp operation in new[] { NativeOp.Narrow, NativeOp.Convert })
    {
        SourceSpan source = new("closed-namespace.msync", 0, 4);
        NativeProgram program = new([], [new("closed-cast", [new(0), new(1)],
            [new(NativeOp.Allocate, 0, 0), new(operation, 1, 0, Type: 1, Immediate: 1, Source: source),
             new(NativeOp.Return, A: 1)], new([], 1, []))],
            [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Scope, Layouts: [1]), new(TypeKind.Number), new(TypeKind.String)],
            [new([new("number", 2)]), new([new("text", 3)])]);
        await using NativeInterpreter interpreter = new(program);
        try
        {
            await interpreter.ExecuteAsync();
            throw new Exception($"{operation} accepted a namespace outside its finite physical layout set");
        }
        catch (MsyncException error) when (error.Span == source && error.Message.Contains("layout #0", StringComparison.Ordinal)) { checks++; }
    }
    Console.WriteLine("PASS closed-namespace: Convert/Narrow reject incompatible physical layouts with original source attribution");
}

async Task FailedTransferJourney()
{
    foreach (bool observed in new[] { false, true })
    {
        string directory = Path.Combine(root, observed ? "observed-failure" : "ignored-failure");
        Directory.CreateDirectory(directory);
        string source = observed ? "fn fail() { ./missing -> ./destination }; if fail() { 1 } else { 2 }"
            : "fn fail() { ./missing -> ./destination }; fail()";
        StringWriter errors = new(CultureInfo.InvariantCulture);
        await using NativeInterpreter interpreter = new(Compiler.CompileNative(source),
            new(directory, Error: errors));
        NativeValue result = await interpreter.ExecuteAsync();
        if (observed) Number(result, 2);
        else if (result.Kind != NativeKind.Bool || result.AsBool()) throw new Exception("Failed returned transfer lost its false result");
        if (interpreter.FailedTransfers != 1 || !errors.ToString().Contains("missing", StringComparison.Ordinal))
            throw new Exception($"Transfer failure lost observation/diagnostics: observed={observed}, count={interpreter.FailedTransfers}, errors={errors}");
        if (File.Exists(Path.Combine(directory, "destination"))) throw new Exception("Failed transfer published a destination");
        await interpreter.DisposeAsync();
        if (interpreter.FailedTransfers != 1) throw new Exception("Disposal counted a selected transfer failure twice");
        checks++;
    }
    Console.WriteLine("PASS transfer-failure: ignored/handled failures counted once; false result and concrete diagnostic retained");
}

async Task ProcessLifetimeJourney()
{
    NativeProgram program = Compiler.CompileNative("""
        fn launch() {
            let child = $std::process::spawn(/bin/sh, ["-c", "read value"]);
            fn() { child }
        };
        launch()
        """);
    NativeProcess? capturedChild = null;
    NativeRuntimeContext capturedContext = new() { ProcessCreated = process => capturedChild = process };
    NativeInterpreter interpreter = new(program, context: capturedContext);
    NativeValue closure = await interpreter.ExecuteAsync();
    NativeProcess child = capturedChild ?? throw new Exception("Capture fixture did not start a child");
    if (closure.Kind != NativeKind.Function) throw new Exception("Capture fixture did not return a closure");
    if (child.Control?.IsRunning != true) throw new Exception("Captured Process was prematurely joined or retired");
    await interpreter.DisposeAsync();
    if (child.Control.IsRunning || child.Completion?.IsCompleted != true)
        throw new Exception("Native disposal left a captured owned Process running");
    checks++;

    NativeProcess? cancelledChild = null;
    NativeRuntimeContext context = new() { ProcessCreated = process => cancelledChild = process };
    program = Compiler.CompileNative("""
        let child = $std::process::spawn(/bin/sh, ["-c", "read value"]);
        $std::process::wait(child)
        """);
    await using NativeInterpreter cancelled = new(program, context: context);
    using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(250));
    try
    {
        await cancelled.ExecuteAsync(timeout.Token);
        throw new Exception("Native Process wait ignored cancellation");
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    if (cancelledChild?.Completion is not { } completion) throw new Exception("Cancellation fixture did not start a child");
    try { await completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { }
    if (cancelledChild.Control?.IsRunning != false) throw new Exception("Cancelled execution left its child running");
    checks++;
    Console.WriteLine("PASS process-lifetime: captured Process escape, disposal and actual wait cancellation");
}

async Task PrivateSessionProcessJourney()
{
    string directory = Path.Combine(root, "private-session-process");
    Directory.CreateDirectory(directory);
    File.Delete(Path.Combine(directory, "release"));
    File.Delete(Path.Combine(directory, "finished"));
    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    NativeCompilerSession compiler = new();
    await using NativeExecutionSession session = new(new(WorkingDirectory: directory, Observe: observation =>
    {
        if (observation is { Phase: "process", Outcome: "started" }) started.TrySetResult();
    }));
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
    NativeProgram privateProgram = compiler.Compile("""
        {
            let private_child = $std::process::spawn(/bin/sh,
                ["-c", "while [ ! -f ./release ]; do sleep 0.01; done; printf joined > ./finished"]);
            $std::ranges::materialize($std::ranges::take([private_child::exit_code], 0))
        }
        """);
    File.WriteAllText(Path.Combine(directory, "roots.txt"), string.Join('\n', privateProgram.SessionRoots?.Select(slot => $"{slot}: {privateProgram.Functions[privateProgram.EntryPoint].Slots[slot]}") ?? []));
    File.WriteAllText(Path.Combine(directory, "instructions.txt"), string.Join('\n', privateProgram.Functions.SelectMany(function => function.Instructions.Select(instruction => $"{function.Name}: {instruction}"))));
    Task<NativeValue> pending = session.ExecuteAsync(privateProgram, timeout.Token).AsTask();
    await started.Task.WaitAsync(timeout.Token);
    await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token));
    if (pending.IsCompleted)
    {
        await pending;
        throw new Exception("Private block Process escaped through persistent backend locals");
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "release"), "release", timeout.Token);
    await pending.WaitAsync(timeout.Token);
    if (await File.ReadAllTextAsync(Path.Combine(directory, "finished"), timeout.Token) != "joined")
        throw new Exception("Submission returned before joining its private block Process");
    checks++;
    Console.WriteLine("PASS private-session-work: persisted private local does not become an escaping Process root");
}

Number(await Run("ordered-source-set", """
    let values = $std::containers::Set::new([3, 1, 3, 2]);
    $std::containers::Set::size(values)
    """), 3);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<WeakReference<NativeAllocation>> CreateWeakAllocation(NativeInterpreter interpreter) =>
    new((await interpreter.ExecuteAsync()).AsAddress().Allocation);

[MethodImpl(MethodImplOptions.NoInlining)]
static bool AllocationAlive(WeakReference<NativeAllocation> reference) => reference.TryGetTarget(out _);

Console.WriteLine($"Native execution: {checks} checked real programs/results.");
