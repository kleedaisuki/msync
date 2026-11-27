using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Contains("--refinement"))
        {
            await RefinementJourney();
            Console.WriteLine("PASS direct refinement copies and serialized execution");
            return;
        }
        if (args.Contains("--compiled-copy"))
        {
            await CompiledCopyJourney();
            Console.WriteLine("PASS compiled copy proof, serialization and execution");
            return;
        }
        if (args.Contains("--workspace"))
        {
            await DirectWorkspaceJourney();
            Console.WriteLine("PASS direct workspace journey and live parent context");
            return;
        }
        if (args.Contains("--guest-stack"))
        {
            await DeepCalls();
            await StackRoots();
            await TailStorage();
            Console.WriteLine("PASS segmented guest calls, retained frames and native tail storage");
            return;
        }
        await Inlining();
        await Journeys();
        await BorrowAndReuse();
        await EscapingLocations();
        await Suspension();
        await DeferredDemand();
        await DeepCalls();
        await StackRoots();
        await CompiledWorkScopes();
        await DirectEntryJourneys();
        await PendingContextIsolation();
        await CancelQueuedProcess();
        await DiscardInput();
        StorageContract();
        Console.WriteLine("PASS call lowering: cleaned/nested inlining, detached execution, borrowed arguments, reusable storage, aliases and capture lifetime");
    }

    private static async Task Inlining()
    {
        const string source = """
            fn leaf(x: Number) { x * 2 + 1 };
            fn middle(x: Number) { leaf(x) + leaf(x + 1) };
            let row = object { value: 7 };
            fn select(row, x: Number, y: Number) {
                let a = row::value;
                let b = a + x;
                let c = b * 2;
                let d = c + y;
                if x > y { return d - x; };
                d + middle(x)
            };
            select(row, 2, 3)
            """;
        List<CompilationObservation> observations = [];
        NativeProgram program = Compiler.Compile(source, "inline-chain.msync", 2, new(observations.Add, true));
        Check(program.Functions[program.EntryPoint].Instructions.All(instruction => instruction.Op != NativeOp.Call),
            "Cleaned nested calls retained activation boundaries");
        Check(observations.Any(row => row.Metrics?.GetValueOrDefault("inline.expanded") > 0), "Missing inline decision telemetry");
        await Execute(program, 33);
    }

    private static async Task Journeys()
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("parameter-snapshot", """
                let row = object { n: 10 };
                fn consume(n: Number) { row::n = 30; n + row::n };
                consume(row::n)
                """, 40),
            ("writable-parameter", """
                fn change(n: Number) { n = n + 5; n };
                let value = 10;
                change(value) + value
                """, 25),
            ("escaping-capture", """
                fn make(n: Number) { fn() { n = n + 1; n } };
                let first = make(10);
                let second = make(20);
                first() + second() + first()
                """, 44),
            ("fresh-objects", """
                fn make() { object { n: 1 } };
                let first = make(); let second = make();
                first::n = 7;
                first::n + second::n
                """, 8),
            ("capture-adaptation", """
                fn outer(n: Number or String) { fn() { if n is Number { n + 1 } else { 0 } } };
                let first = outer(6); let second = outer("text");
                first() + second()
                """, 7),
            ("tail-borrow", """
                fn step(n: Number, sum: Number) { if n == 0 { return sum; }; step(n - 1, sum + n) };
                step(512, 0)
                """, 131328),
            ("tail-permutation", """
                fn swap(n: Number, a: Number, b: Number) {
                    if n == 0 { return a * 100 + b; }; swap(n - 1, b, a)
                };
                swap(65, 2, 7)
                """, 702),
            ("tail-capture", """
                fn relay(value) { value };
                fn make(n: Number) { relay(fn() { n = n + 1; n }) };
                let first = make(3); let second = make(10);
                first() + second() + first()
                """, 20),
            ("shared-location", """
                let row = object { n: 4 };
                fn change(value) { value::n = value::n + 1; value::n };
                change(row) + change(row) + row::n
                """, 17)
        ];
        foreach (var test in cases)
            foreach (int level in new[] { 0, 2 })
            {
                NativeProgram program = Compiler.Compile(test.Source, test.Name + ".msync", level);
                await Execute(program, test.Expected);
            }
    }

    private static async Task EscapingLocations()
    {
        const string source = "fn make(n: Number) { fn() { n = n + 1; n } }; let first = make(10); let second = make(20); first() + second() + first()";
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            RuntimeProfile profile = new();
            await using NativeInterpreter interpreter = new(Compiler.Compile(source, "heap-location.msync", 0),
                new() { Profile = profile, Tiers = new(policy, CallThreshold: 1, BackedgeThreshold: 1) });
            Check((await interpreter.ExecuteAsync()).AsNumber() == 44, "Escaping locations lost identity across frame reuse");
            var samples = profile.Drain().Where(sample => sample.Name == "vm.function"
                && sample.Metrics["heap_escaping_locations"] > 0).ToArray();
            Check(samples.Length > 0, "Escaping locations were not heap-placed");
            Check(samples.Any(sample => sample.Metrics["calls"] >= 2 && sample.Metrics["frame_storage_reuses"] > 0),
                "An escaping location retained its entire frame");
        }
    }

    private static async Task BorrowAndReuse()
    {
        const string source = """
            fn add(a: Number, b: Number) { a + b };
            let n = 0; let total = 0;
            loop { if n >= 64 { break; }; total = total + add(n, 1); n = n + 1; };
            total
            """;
        NativeProgram program = Compiler.Compile(source, "borrow.msync", 0);
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(program, new() { Profile = profile, Tiers = new(NativeJitPolicy.Off) });
        Check((await interpreter.ExecuteAsync()).AsNumber() == 2080, "Borrowed parameters changed their values");
        var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
        Check(samples.Sum(sample => (long)sample.Metrics["call_arguments_borrowed"]) > 0, "No arguments used proven shared cells");
        Check(samples.Sum(sample => (long)sample.Metrics["frame_storage_reuses"]) > 0, "Private activation storage was not reused");
        Check(samples.Sum(sample => (long)sample.Metrics["activation_descriptor_reuses"]) > 0,
            "Reusable frames did not retain their activation descriptors");
        Check(samples.All(sample => sample.Metrics["activation_descriptor_reuses"]
            + sample.Metrics["activation_descriptor_allocations"] == sample.Metrics["calls"]),
            "A guest call did not bind exactly one activation descriptor");
        Check(samples.Sum(sample => (long)sample.Metrics["call_argument_snapshots"]) == 0, "Private arguments were unnecessarily materialized");
        await TailStorage();
    }

    private static async Task TailStorage()
    {
        const string source = "fn step(n: Number, sum: Number) { if n == 0 { return sum; }; step(n - 1, sum + n) }; step(512, 0)";
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(Compiler.Compile(source, "tail-storage.msync", 0),
            new() { Profile = profile, Tiers = new(NativeJitPolicy.Off) });
        Check((await interpreter.ExecuteAsync()).AsNumber() == 131328, "Typed tail handoff changed the result");
        var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
        Check(samples.Sum(sample => (long)sample.Metrics["tail_argument_buffers"]) == 2, "Tail argument transfer must reuse two nonoverlapping buffers");
        Check(samples.Sum(sample => (long)sample.Metrics["native_heap_objects"]) >= 2,
            "Tail buffers did not use the native heap.");
        Check(samples.Sum(sample => (long)sample.Metrics["call_argument_snapshots"]) == 0, "Tail calls packed argument arrays");
        Check(samples.Sum(sample => (long)sample.Metrics["frame_storage_reuses"]) >= 510, "Tail handoff retained old private activations");
    }

    private static async Task Execute(NativeProgram input, double expected)
    {
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(input, bytes);
        bytes.Position = 0;
        NativeProgram detached = NativeArtifact.Read(bytes);
        foreach (NativeProgram program in new[] { input, detached })
            foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
            {
                await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy, CallThreshold: 1, BackedgeThreshold: 1) });
                double actual = (await interpreter.ExecuteAsync()).AsNumber();
                Check(actual == expected, $"Call lowering {policy}: expected {expected}, received {actual}");
            }
    }

    private static void StorageContract()
    {
        List<CompilationObservation> compiled = [];
        NativeProgram program = Compiler.Compile("fn add(n: Number) { n + 1 }; add(4)", "storage-plan.msync", 0,
            new(compiled.Add, true));
        Check(program.Functions.All(function => function.Storage is { } storage
            && storage.Slots.Count == function.Slots.Count && storage.Instructions.Count == function.Instructions.Count),
            "Compiler did not publish final slot/instruction storage plans");
        Check(compiled.Any(item => item.Name == "compiler.storage.function"), "Storage planning lost phase telemetry");
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        List<CompilationObservation> decoded = [];
        NativeProgram artifact = NativeArtifact.Read(bytes, new(decoded.Add, true));
        for (int index = 0; index < program.Functions.Count; index++)
        {
            NativeStoragePlan source = program.Functions[index].Storage!;
            NativeStoragePlan target = artifact.Functions[index].Storage!;
            Check(source.Slots.SequenceEqual(target.Slots) && source.Instructions.SequenceEqual(target.Instructions),
                "Artifact decoding changed compiler storage decisions");
            Check(source.Calls.Count == target.Calls.Count && source.Calls.Zip(target.Calls).All(pair =>
                pair.First.Instruction == pair.Second.Instruction && pair.First.Snapshot == pair.Second.Snapshot
                && pair.First.Borrowed.SequenceEqual(pair.Second.Borrowed)), "Call plan did not survive artifact encoding");
        }
        Check(!decoded.Any(item => item.Name.StartsWith("compiler.verify", StringComparison.Ordinal)
            || item.Name.StartsWith("compiler.storage", StringComparison.Ordinal)
            || item.Name == "compiler.artifact.allocation_hints"), "Artifact decoding re-ran compiler analysis");
        byte[] old = bytes.ToArray();
        old[8] = 1;
        using MemoryStream legacy = new(old);
        try { NativeArtifact.Read(legacy); throw new InvalidOperationException("Old artifact format was accepted"); }
        catch (MsyncException error)
        { Check(error.Message.Contains("Recompile", StringComparison.Ordinal), "Old artifact rejection lost migration guidance"); }
    }

    private static async Task Suspension()
    {
        const string source = """
            fn finish(n: Number) { n + 1 };
            fn read(n: Number) {
                let line? = $std::io::read_line();
                if line is null { return 0; };
                finish(n + (line as Number))
            };
            read(9)
            """;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            using DeferredReader reader = new();
            await using NativeInterpreter interpreter = new(Compiler.Compile(source, "suspended-call.msync", 0),
                new() { Input = reader, Tiers = new(policy, CallThreshold: 1) });
            ValueTask<NativeValue> pending = interpreter.ExecuteAsync();
            Check(!pending.IsCompleted, "Suspension journey never suspended");
            reader.Complete("7");
            Check((await pending).AsNumber() == 17, "Suspended tail handoff lost arguments");
            Check((await interpreter.ExecuteAsync()).AsNumber() == 17, "Reused activation retained suspended state");
        }
    }

    private static async Task DiscardInput()
    {
        using StickyInput input = new();
        using InputStartedWriter output = new(input.Started.Task);
        await using NativeInterpreter interpreter = new(Compiler.Compile(
            "$std::io::read_line(); $std::io::println(\"started\"); 7", "discarded-input.msync", 0),
            new() { Input = input, Output = output });
        try
        {
            Check((await interpreter.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).AsNumber() == 7,
                "Discarded input held the evaluator until the physical reader completed");
            Check(!input.ReadCompletion.IsCompleted, "Uncooperative physical reader unexpectedly completed");
            Check((await interpreter.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).AsNumber() == 7,
                "A queued input prevented scope completion");
            Check(input.ReadCount == 1, "Cancelled input released the reader turn before physical completion");
        }
        finally { input.Complete(); }
    }

    private sealed class StickyInput : TextReader
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> value = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<string?> ReadCompletion => value.Task;
        private int reads;
        internal int ReadCount => Volatile.Read(ref reads);
        internal void Complete() => value.TrySetResult("ignored");
        public override ValueTask<string?> ReadLineAsync(CancellationToken token)
        {
            Interlocked.Increment(ref reads);
            Started.TrySetResult();
            return new(value.Task);
        }
    }

    private sealed class InputStartedWriter(Task started) : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken token = default) => started.WaitAsync(token);
    }

    private static async Task CancelQueuedProcess()
    {
        using QueuedInput input = new();
        int launches = 0;
        NativeRuntimeContext context = new(new() { Input = input });
        context.ProcessCallAsync = (_, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            return ValueTask.FromResult(NativeValue.FromProcess(new(0, "", "")));
        };
        NativeInterpreter interpreter = new(Compiler.Compile(
            "object { job: release ./unused($std::io::read_stdin()) }", "queued-process.msync", 0), context: context);
        try
        {
            NativeValue result = await interpreter.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Kind == NativeKind.Address, "Returning a queued process demanded its arguments");
            await interpreter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Check(Volatile.Read(ref launches) == 0, "A cancelled submission launched a process");
        }
        finally
        {
            input.Complete();
            await interpreter.DisposeAsync();
        }
    }

    private sealed class QueuedInput : TextReader
    {
        private readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(string value = "ready") => ready.TrySetResult(value);
        public override Task<string> ReadToEndAsync(CancellationToken token) => ready.Task.WaitAsync(token);
    }

    private static async Task CompiledWorkScopes()
    {
        const string source = "fn leaf(n: Number) { n + 1 }; fn middle(n: Number) { leaf(n) + leaf(n + 1) }; middle(5)";
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile(source, "work-scope.msync", 0));
        using MemoryStream executable = new();
        NativeArtifact.WriteProduced(program, executable);
        executable.Position = 0;
        program = NativeArtifact.Read(executable);
        Check(program.Backend!.Functions[program.EntryPoint].Instructions.Any(code => code.Handler == NativeBackendHandler.DirectCall),
            "The bounded direct-call contract did not survive executable serialization");
        for (int index = 0; index < program.Functions.Count; index++)
            if (program.Backend!.Functions[index].ContextFree)
                Check(program.Functions[index].Instructions.All(code => code.Op is not (NativeOp.Call or NativeOp.CallNative)),
                    "The leaf backend contract admitted a nested call");
        Check(program.Backend!.Functions.All(function => function.PureSynchronous),
            "A direct pure call created a work scope");
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, CallThreshold: 1) });
        Check((await interpreter.ExecuteAsync()).AsNumber() == 13, "Scopeless calls changed their result");
        const string recursive = "fn step(n: Number) { if n == 0 { return 1; }; step(n - 1) }; step(64)";
        NativeProgram cycle = NativePreparation.Prepare(Compiler.Compile(recursive, "work-cycle.msync", 0));
        Check(cycle.Backend!.Functions.All(function => function.PureSynchronous), "A pure recursive call cycle required work ownership");
        NativeProgram effect = NativePreparation.Prepare(Compiler.Compile(
            "fn read() { $std::io::read_line() }; fn outer() { read() }; outer()", "work-effect.msync", 0));
        Check(!effect.Backend!.Functions[effect.EntryPoint].PureSynchronous,
            "An input submission lost its transitive entry owner");
        foreach (var (function, index) in effect.Functions.Select((function, index) => (function, index)))
            foreach (NativeInstruction instruction in function.Instructions)
            {
                bool submitsInput = instruction.Op == NativeOp.CallNative && instruction.Symbol == "io.read_line";
                bool callsOwner = instruction.Op == NativeOp.Call && instruction.C < 0 && instruction.B >= 0
                    && !effect.Backend.Functions[instruction.B].PureSynchronous;
                if (submitsInput || callsOwner)
                    Check(!effect.Backend.Functions[index].PureSynchronous, "A compiled input dependency lost its owner");
            }
    }

    private static async Task DirectEntryJourneys()
    {
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
            foreach (bool observed in new[] { false, true })
                await DirectEntryJourney(policy, observed);
    }

    private static async Task DirectEntryJourney(NativeJitPolicy policy, bool observed)
    {
        ExecutionOptions options = new() { Tiers = new(policy, CallThreshold: 1), Profile = observed ? new() : null };
        NativeProgram tail = Compiler.Compile(
            "fn leaf(n: Number) { n+1 }; fn middle(n: Number) { leaf(n) }; middle(41)", "direct-tail.msync", 0);
        await using (NativeInterpreter interpreter = new(tail, options))
            Check((await interpreter.ExecuteAsync()).AsNumber() == 42, "A direct entry lost its tail-position callee result");
        if (options.Profile is { } directProfile)
        {
            var samples = directProfile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
            Check(samples.Sum(sample => (long)sample.Metrics["direct_return_slots"]
                + (long)sample.Metrics["direct_return_scalars"]) >= 2, "Direct calls did not return into caller storage");
            if (policy == NativeJitPolicy.Force)
                Check(samples.Sum(sample => (long)sample.Metrics["direct_return_scalars"]) > 0,
                    "Ready native scalar returns used generic value containers");
        }
        await using (NativeInterpreter mixed = new(Compiler.Compile(
            "fn choose(flag: Bool) { if flag { return null; }; 42 }; choose(false)", "direct-union.msync", 0), options))
            Check((await mixed.ExecuteAsync()).AsNumber() == 42, "A direct scalar return lost its union discriminator");
        await using (NativeInterpreter boolean = new(Compiler.Compile(
            "fn below(n: Number) { n < 10 }; below(4)", "direct-bool.msync", 0), options))
            Check((await boolean.ExecuteAsync()).AsBool(), "A direct Boolean return lost its scalar representation");

        await DirectFutureJourney(options,
            "fn echo(text: String) { text }; let value=echo($std::io::read_stdin()); $std::io::println(\"submitted\"); value",
            "ready", NativeValue.FromString("ready"));
        await DirectFutureJourney(options,
            "fn parse(text: String) { text as Number }; fn plus(n: Number) { n+1 }; let value=plus(parse($std::io::read_stdin())); $std::io::println(\"submitted\"); value",
            "41", NativeValue.FromNumber(42));

        using StringReader invalid = new("not-a-number");
        await using NativeInterpreter failing = new(Compiler.Compile(
            "fn parse(text: String) { text as Number }; parse($std::io::read_stdin())", "direct-failure.msync", 0),
            options with { Input = invalid });
        try
        {
            await failing.ExecuteAsync();
            throw new InvalidOperationException("An invalid direct-call conversion succeeded");
        }
        catch (MsyncException error)
        {
            Check(error.Span is not null, "A direct-call failure lost its source location");
        }
    }

    private static async Task PendingContextIsolation()
    {
        using QueuedInput input = new();
        using SignalWriter output = new();
        RuntimeProfile profile = new();
        const string source = "fn plus(n: Number) { n+1 }; let pending=$std::io::read_stdin(); $std::io::println(plus(41) as String); $std::io::println(\"submitted\"); pending";
        await using NativeInterpreter interpreter = new(Compiler.Compile(source, "pending-context.msync", 0),
            new() { Input = input, Output = output, Profile = profile, Tiers = new(NativeJitPolicy.Force, CallThreshold: 1) });
        ValueTask<NativeValue> execution = interpreter.ExecuteAsync();
        try
        {
            await output.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(output.ToString().Contains("42"), "Unrelated input changed a ready numeric call");
        }
        finally { input.Complete(); }
        Check((await execution).AsString() == "ready", "Context isolation lost the eventual input result");
        var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
        Check(samples.Sum(sample => (long)sample.Metrics["tier_context_unrelated_pending"]) > 0,
            "An unrelated input Future polluted the numeric callee's native context");
    }

    private static async Task DirectFutureJourney(ExecutionOptions options, string source, string supplied, NativeValue expected)
    {
        using QueuedInput input = new();
        using SignalWriter output = new();
        await using NativeInterpreter interpreter = new(Compiler.Compile(source, "direct-future.msync", 0),
            options with { Input = input, Output = output });
        ValueTask<NativeValue> execution = interpreter.ExecuteAsync();
        try
        {
            await output.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!execution.IsCompleted, "A direct return prematurely demanded its Future argument");
        }
        finally { input.Complete(supplied); }
        Check((await execution).Equals(expected), "A direct return lost a pending argument after frame release");
    }

    private static async Task CompiledCopyJourney()
    {
        const string source = """
            fn choose(flag: Bool, a: String, b: String) {
                let value = if flag {$std::string::concat(a, "a")} else {$std::string::concat(b, "b")}; value
            };
            choose($std::io::read_line() is null, "left", "right")
            """;
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile(source, "compiled-copy.msync", 0));
        int sites = 0;
        for (int function = 0; function < program.Functions.Count; function++)
            foreach (NativeBackendInstruction instruction in program.Backend!.Functions[function].Instructions)
            {
                if (instruction.Handler != NativeBackendHandler.CompiledCopy) continue;
                sites++;
                Check(instruction.Op == NativeOp.Copy && instruction.OperandKinds != ulong.MaxValue,
                    "Compiled copy lost its exact carrier contract");
                Check(program.Functions[function].Storage!.Slots[instruction.A].HasFlag(NativeSlotStorage.PrivateArgument),
                    "Compiled copy accepted an address-exposed source");
            }
        Check(sites > 0, "The source journey did not lower a compiled copy");
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        program = NativeArtifact.Read(bytes);
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            using StringReader input = new("");
            RuntimeProfile profile = new();
            await using NativeInterpreter interpreter = new(program,
                new() { Input = input, Profile = profile, Tiers = new(policy, CallThreshold: 1) });
            Check((await interpreter.ExecuteAsync()).AsString() == "lefta", "Compiled copy changed the selected value");
            var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
            Check(samples.Sum(sample => (long)sample.Metrics["compiled_copies"]) > 0,
                "The compiled-copy handler was not executed");
            Check(samples.Sum(sample => (long)sample.Metrics["compiled_reads"]) > 0,
                "The compiler-proven read path was not executed");
        }
    }

    private static async Task RefinementJourney()
    {
        foreach ((string argument, NativeValue expected) in new[]
            { ("41", NativeValue.FromNumber(42)), ("\"hello\"", NativeValue.FromString("hello!")) })
        {
            string source = "fn choose(value: Number or String) { if value is Number { value + 1 } "
                + "else { $std::string::concat(value, \"!\") } }; choose(" + argument + ")";
            NativeProgram program = NativePreparation.Prepare(Compiler.Compile(source, "refinement.msync", 0));
            Check(program.Functions.Any(function => function.Instructions.Any(code => code.Op == NativeOp.Narrow)),
                "Refinement journey lost its narrowing instruction");
            using MemoryStream bytes = new();
            NativeArtifact.WriteProduced(program, bytes);
            bytes.Position = 0;
            program = NativeArtifact.Read(bytes);
            Check(program.Functions.Any(function => function.Instructions.Any(code =>
                code.Op is NativeOp.Copy or NativeOp.Move
                && function.Storage!.Slots[code.A].HasFlag(NativeSlotStorage.PrivateArgument)
                && program.Types[function.Slots[code.A].Type].Kind != program.Types[function.Slots[code.Result].Type].Kind)),
                "Refinement journey lost its private widening copy");
            program = program with
            {
                Backend = program.Backend! with
                {
                    Functions = program.Backend!.Functions.Select(function => function with
                    {
                        Instructions = function.Instructions.Select(code => code.Handler == NativeBackendHandler.CompiledCopy
                            ? code with { Handler = NativeBackendHandler.Standard } : code).ToArray()
                    }).ToArray()
                }
            };
            foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
            {
                RuntimeProfile profile = new();
                await using NativeInterpreter interpreter = new(program,
                    new() { Profile = profile, Tiers = new(policy, CallThreshold: 1) });
                Check(await interpreter.ExecuteAsync() == expected, "Refinement copy changed its payload");
                if (policy == NativeJitPolicy.Off)
                {
                    var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
                    Check(samples.Sum(sample => (long)sample.Metrics["refinement_copies"]) > 0,
                        "Ordinary execution still materialized every narrowed value");
                    Check(samples.Sum(sample => (long)sample.Metrics["compiled_copies"]) > 0,
                        "Generic Copy/Move did not consume the private-slot proof");
                }
            }
        }
    }

    private static async Task DirectWorkspaceJourney()
    {
        long shortRun = await WorkspaceLeases(32);
        long longRun = await WorkspaceLeases(1024);
        Console.WriteLine($"Native workspace leases for 32/1024 iterations: {shortRun}/{longRun}");
    }

    private static async Task<long> WorkspaceLeases(int count)
    {
        string source = "fn leaf(x: Number) { x * 2 + 1 }; "
            + "fn middle(x: Number) { leaf(x) + leaf(x + 1) }; let i = 0; let sum = 0; "
            + $"loop {{ if i >= {count} {{break;}}; sum = sum + middle(i); i = i + 1; }}; sum";
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(Compiler.Compile(source, "workspace-siblings.msync", 0),
            new() { Profile = profile, Tiers = new(NativeJitPolicy.Force, CallThreshold: 1) });
        Check((await interpreter.ExecuteAsync()).AsNumber() == 2.0 * count * (count + 1),
            "A direct child overwrote its live parent's native context");
        var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
        Check(samples.Sum(sample => (long)sample.Metrics["guest_direct_calls"]) >= 2 * count,
            "Workspace journey did not exercise direct sibling calls");
        return samples.Sum(sample => (long)sample.Metrics["tier_workspace_leases"]);
    }

    private static async Task DeepCalls()
    {
        const string source = "fn descend(n: Number) { if n == 0 { return 0; }; descend(n - 1) + 1 }; descend(20000)";
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            RuntimeProfile profile = new();
            await using NativeInterpreter interpreter = new(Compiler.Compile(source, "deep-calls.msync", 0),
                new() { Profile = profile, Tiers = new(policy, CallThreshold: 1) });
            Check((await interpreter.ExecuteAsync()).AsNumber() == 20000, "Explicit guest return stack lost a caller result");
            var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
            long pushes = samples.Sum(sample => (long)sample.Metrics["guest_call_pushes"]);
            long direct = samples.Sum(sample => (long)sample.Metrics["guest_direct_calls"]);
            long handoffs = samples.Sum(sample => (long)sample.Metrics["guest_loop_handoffs"]);
            Check(pushes + direct + handoffs >= 20000, "Deep calls lost an execution transition");
            Check(samples.Sum(sample => (long)sample.Metrics["stack_frames"]) >= 20000,
                "Ordinary recursive frames did not use segmented storage");
            Check(samples.Sum(sample => (long)sample.Metrics["stack_segment_allocations"]) > 1,
                "Deep recursive journey did not cross a storage segment");
            Check(samples.Sum(sample => (long)sample.Metrics["guest_call_pushes"])
                == samples.Sum(sample => (long)sample.Metrics["guest_call_resumes"]), "Guest callers were not resumed exactly once");
            if (policy == NativeJitPolicy.Force)
            {
                Check(samples.Sum(sample => (long)sample.Metrics["tier_workspace_leases"]) <= direct + handoffs + 1,
                    "Explicit guest calls rented separate native workspaces");
                Check(samples.Sum(sample => (long)sample.Metrics["tier_workspace_reuses"]) >= pushes,
                    "Native return continuations did not reuse the guest-chain workspace");
            }
        }
    }

    private static async Task StackRoots()
    {
        const string source = """
            fn descend(n: Number) {
                let text = n as String;
                if n == 0 { return 0; };
                let below = descend(n - 1);
                if text == (n as String) { below + 1 } else { -100000 }
            };
            descend(3000) + descend(3000)
            """;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            await using NativeInterpreter interpreter = new(Compiler.Compile(source, "stack-roots.msync", 0),
                new() { Tiers = new(policy, CallThreshold: 1) });
            Check((await interpreter.ExecuteAsync()).AsNumber() == 6000,
                "Segment crossing or reuse lost a caller's string roots");
        }
    }

    private static async Task DeferredDemand()
    {
        const string source = "fn input() { $std::io::read_line() }; let text? = input(); let i=0; loop { if i>=1024 {break;}; i=i+1; }; $std::io::println(\"submitted\"); if text is null { \"eof\" } else { text }";
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            using DeferredReader reader = new();
            using SignalWriter writer = new();
            await using NativeInterpreter interpreter = new(Compiler.Compile(source, "future-demand.msync", 0),
                new() { Input = reader, Output = writer, Tiers = new(policy, CallThreshold: 1) });
            ValueTask<NativeValue> execution = interpreter.ExecuteAsync();
            try
            {
                await writer.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (policy == NativeJitPolicy.Force)
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    while (interpreter.TierStatistics.ReadinessExits == 0) await Task.Delay(1, timeout.Token);
                    Check(interpreter.TierStatistics.Deoptimizations == 0, "A pending value invalidated its compiled type specialization");
                }
                Check(!execution.IsCompleted, "Demand did not wait for its input Future");
            }
            finally { reader.Complete("ready"); }
            Check((await execution).AsString() == "ready", "A returned Future lost its result or frame lifetime");
            if (policy == NativeJitPolicy.Force)
                Check(interpreter.TierStatistics.RawOperations == interpreter.TierStatistics.DirectOperations,
                    "An unrelated Future forced ready scalar stores through host callbacks");
        }
    }

    private sealed class SignalWriter : StringWriter
    {
        internal TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            WriteLine(value.ToString());
            if (value.Span.SequenceEqual("submitted")) Submitted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredReader : TextReader
    {
        private readonly TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(string value) => completion.SetResult(value);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            new(completion.Task.WaitAsync(cancellationToken));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
