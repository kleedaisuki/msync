using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Main(string[] args)
    {
        LayoutComposition();
        string source = """
            fn probe(flag: Bool) {
                let first = object { n: 1, text: "roots", path: ./bound, endpoint: $local, flag: true,
                    type: Number, values: [1, 2], function: fn(n: Number) { n + 1 }, none?: null };
                let alias = first;
                let second = object { n: 10 };
                if flag { alias::n = 7; second::n = 20; } else { alias::n = 9; };
                if first::flag { first::n + second::n } else { 0 }
            };
            probe(true)
            """;
        NativeProgram optimized = await Journey("mixed-alias", source, 27);
        Check(optimized.Allocations.Any(plan => plan.Placement == NativeAllocationPlacement.Frame),
            "A real compiled mixed/alias Object must retain a frame-placement plan");
        await ProfilePlacement(optimized);
        await Journey("finite-layout-projection", """
            fn select(flag: Bool) {
                let row = if flag { object { text: "left", n: 11 } } else { object { n: 17, flag: false } };
                row::n
            };
            select(true) + select(false)
            """, 28);
        NativeProgram cyclic = await Journey("loop-freshness", """
            let current = object { n: 0 };
            let original = current;
            let previous = current;
            for n in [1, 2, 3] { previous = current; current = object { n: n }; };
            original::n * 100 + previous::n * 10 + current::n
            """, 23);
        await UntrustedHints(cyclic, 23);
        await DynamicCountHint();
        await LogicalRegionBounds();
        await PendingScopeFutureJourney();
        await OwnedRangeHint();
        await AllBuiltinJourney();
        NativeProgram escaping = await Journey("returned-nested-closure", """
            fn make(seed: Number) {
                let outer = object { inner: object { n: seed } };
                fn() { outer::inner::n = outer::inner::n + 1; outer::inner::n }
            };
            let next = make(4);
            next() * 10 + next()
            """, 56);
        Check(escaping.Allocations.Any(plan => plan.Placement == NativeAllocationPlacement.LogicalHeap),
            "A returned closure's transitively captured namespaces keep independent heap lifetime");
        if (args.Contains("--measure", StringComparer.Ordinal)) await Measure(optimized);
        Console.WriteLine($"PASS {checks} native frame allocation checks");
    }

    private static void LayoutComposition()
    {
        NativeLayout empty = new();
        NativeLayout nil = new(NativeType.Of(NativeKind.Null));
        NativeLayout[] parts = [empty, empty, nil, new NativeLayout(Enum.GetValues<NativeKind>().Select(NativeType.Of).ToArray()), nil];
        NativeLayout combined = NativeLayout.Compose(parts, out IReadOnlyList<NativeOffset> offsets);
        Check(offsets.Distinct().Count() == parts.Length, "Empty/nil Object regions preserve freshness");
        NativeAllocation storage = new(combined);
        NativeValue[] samples = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(-0d),
            NativeValue.FromString("根𐐀"), NativeValue.FromPath(new("./raw", "/origin", new(ConnectionKind.Ssh, "alias"))),
            NativeValue.FromConnection(new(ConnectionKind.Wsl, "distribution")), NativeValue.FromFunction(new(3)),
            NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(8)], TypeRef.Number)),
            NativeValue.FromProcess(new(0, "out", "err")), NativeValue.FromType(TypeRef.Number),
            NativeValue.FromAddress(new NativeAllocation(empty).Address)];
        int ordinal = 0;
        foreach (NativeFieldLayout field in parts[3].Fields)
        {
            NativeAddress address = storage.AddressAt(offsets[3] + field.Offset, field.Type);
            address.Store(samples[ordinal]);
            Check(address.Load() == samples[ordinal++], $"Composed typed field {field.Type} preserves its descriptor");
        }
        storage.AddressAt(offsets[2], NativeType.Of(NativeKind.Null)).Store(NativeValue.Null);
        Check(!storage.AddressAt(offsets[4], NativeType.Of(NativeKind.Null)).IsInitialized,
            "Writing a zero-payload Null does not initialize its neighbour");
        for (int index = 0; index < parts.Length; index++)
            NativeStorage.ValidateRegion(storage.AddressAt(offsets[index]), parts[index], 1);
    }

    private static async Task<NativeProgram> Journey(string name, string source, double expected)
    {
        NativeProgram? optimized = null;
        foreach (int level in new[] { 0, 2 })
        {
            NativeProgram program = SourceCompiler.CompileNative(source, name + ".msync", level);
            if (level == 2) optimized = program;
            using MemoryStream bytes = new();
            NativeArtifact.WriteProduced(program, bytes);
            bytes.Position = 0;
            foreach (NativeProgram route in new[] { program, NativeArtifact.Read(bytes) })
            {
                await using NativeInterpreter interpreter = new(route);
                NativeValue value;
                try { value = await interpreter.ExecuteAsync(); }
                catch
                {
                    string directory = Path.Combine(".temp", "native-allocation-failures");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, $"{name}.O{level}.json"), NativeArtifact.ToJson(route));
                    throw;
                }
                Check(value.Kind == NativeKind.Number && value.AsNumber() == expected,
                    $"{name} O{level} source/artifact independent oracle {expected}, received {value}");
            }
        }
        return optimized!;
    }

    private static async Task ProfilePlacement(NativeProgram program)
    {
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(program, new() { Profile = profile });
        _ = await interpreter.ExecuteAsync();
        RuntimeProfileSample[] functions = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
        Check(functions.All(sample => sample.Attributes["representation"] == NativeProgram.Representation
            && sample.Attributes["artifact.stage"] == program.Stage.ToString()
            && sample.Attributes["optimization.level"] == program.OptimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "Native function telemetry preserves representation, artifact stage and optimization level");
        ulong placed = functions.Aggregate(0UL, (sum, sample) => sum + sample.Metrics["frame_placed_allocations"]);
        Check(placed > 0, "Runtime consumes Frame plans rather than reporting optimizer hints alone");
        RuntimeProfile heapProfile = new();
        await using NativeInterpreter heap = new(HeapControl(program), new() { Profile = heapProfile });
        _ = await heap.ExecuteAsync();
        RuntimeProfileSample[] controls = heapProfile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
        foreach (string metric in new[] { "logical_allocations", "logical_elements", "logical_storage_bytes" })
            Check(functions.Aggregate(0UL, (sum, sample) => sum + sample.Metrics[metric])
                == controls.Aggregate(0UL, (sum, sample) => sum + sample.Metrics[metric]),
                $"Placement preserves {metric} accounting; physical owner bytes are not logical Object bytes");
        Console.WriteLine($"Actual frame-placed Object allocations: {placed}");
    }

    private static async Task UntrustedHints(NativeProgram program, double expected)
    {
        NativeProgram hints = program with
        {
            Allocations = program.Allocations.Select(plan =>
            plan.Placement == NativeAllocationPlacement.LogicalHeap ? plan with { Placement = NativeAllocationPlacement.Frame } : plan).ToArray()
        };
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(hints, new() { Profile = profile });
        Check((await interpreter.ExecuteAsync()).AsNumber() == expected,
            "Untrusted Frame hints do not reuse cyclic storage or change live aliases");
        ulong placed = profile.Drain().Where(sample => sample.Name == "vm.function").Aggregate(0UL,
            (sum, sample) => sum + sample.Metrics["frame_placed_allocations"]);
        int safe = hints.Functions.Select((_, function) => NativeAllocationSafety.FrameSafe(hints, function).Count).Sum();
        Check(placed <= (ulong)safe, "Only independently proven noncyclic hints reach frame placement");
    }

    private static async Task DynamicCountHint()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 2)],
            [new("dynamic-count", [new(0), new(1), new(2), new(0)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Allocate, 1, 0, C: 0),
                 new(NativeOp.AddressOffset, 2, 1, 0, 0), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0),
                 new(NativeOp.Load, 3, 2, Type: 0), new(NativeOp.Return, A: 3)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
            [new([new("n", 0)])])
        { Allocations = [new(1, 0, NativeAllocationPlacement.Frame)] };
        NativeArtifact.Validate(program);
        Check(NativeAllocationSafety.FrameSafe(program, 0).Count == 0, "A dynamic element count has no static region proof");
        RuntimeProfile profile = new();
        await using NativeInterpreter interpreter = new(program, new() { Profile = profile });
        Check((await interpreter.ExecuteAsync()).AsNumber() == 2, "Unproved dynamic hint falls back to ordinary typed allocation");
        RuntimeProfileSample sample = profile.Drain().Single(item => item.Name == "vm.function");
        Check(sample.Metrics["frame_placed_allocations"] == 0 && sample.Metrics["logical_elements"] == 2,
            "Dynamic fallback counts real elements and does not misreport frame placement");
    }

    private static async Task OwnedRangeHint()
    {
        foreach (string? mode in new string?[] { "move", null })
        {
            NativeProgram program = new([new(NativeKind.Number, Number: 1)],
                [new("owned-range", [new(0), new(1), new(2), new(3)],
                    [new(NativeOp.Constant, 0, 0), new(NativeOp.Allocate, 1, 0),
                     new(NativeOp.AddressOffset, 2, 1, 0, 0), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0),
                     new(NativeOp.RangeFromMemory, 3, 1, 0, 0, Type: 3, Symbol: mode),
                     new(NativeOp.Return, A: 3)], new([], 3, []))],
                [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0), new(TypeKind.Range, Element: 0)],
                [new([new("n", 0)])])
            { Allocations = [new(1, 0, NativeAllocationPlacement.Frame)] };
            NativeArtifact.Validate(program);
            bool placed = mode != "move";
            Check(NativeAllocationSafety.FrameSafe(program, 0).Contains(1) == placed,
                "Owned Range backing escapes; copying a scalar snapshot does not export its source region");
            RuntimeProfile profile = new();
            await using NativeInterpreter interpreter = new(program, new() { Profile = profile });
            NativeRange range = (await interpreter.ExecuteAsync()).AsRange();
            int count = 0;
            await foreach (NativeValue item in range.Items)
            {
                Check(item.AsNumber() == 1, "Returned memory range survives source activation retirement");
                count++;
            }
            Check(count == 1, "Returned memory range has exactly one element");
            RuntimeProfileSample sample = profile.Drain().Single(item => item.Name == "vm.function");
            Check(sample.Metrics["frame_placed_allocations"] == (placed ? 1UL : 0UL),
                "Runtime honors ownership-sensitive backing-store proof despite a forged Frame hint");
        }
    }

    private static async Task LogicalRegionBounds()
    {
        foreach (bool registers in new[] { false, true })
            foreach (NativeOp operation in new[] { NativeOp.IndexAddress, NativeOp.RangeFromMemory })
            {
                SourceSpan source = new("logical-bounds.msync", 11, 7);
                List<NativeInstruction> code = [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
                    new(NativeOp.Constant, 9, 1), new(NativeOp.Allocate, 2, 0), new(NativeOp.Allocate, 3, 0),
                    new(NativeOp.AddressOffset, 4, 2, 0, 0), new(NativeOp.Store, A: 4, B: 0, C: 1, Type: 0),
                    new(NativeOp.AddressOffset, 5, 3, 0, 0), new(NativeOp.Store, A: 5, B: 1, C: 1, Type: 0)];
                if (registers) code.Add(new(NativeOp.FrameAddress, 4, 9));
                int address = registers ? 4 : 2;
                if (operation == NativeOp.IndexAddress)
                    code.AddRange([new(operation, 6, address, 1, 0, Source: source),
                        new(NativeOp.AddressOffset, 4, 6, 0, 0), new(NativeOp.Load, 7, 4, Type: 0), new(NativeOp.Return, A: 7)]);
                else code.AddRange([new(operation, 8, address, 0, 0, Type: 3, Source: source), new(NativeOp.Return, A: 8)]);
                NativeProgram program = new([new(NativeKind.Number, Number: 2), new(NativeKind.Number, Number: 1)],
                    [new("bounds", [new(0), new(0), new(1), new(1), new(2), new(2), new(1), new(0), new(3), new(0)],
                        code, new([], operation == NativeOp.IndexAddress ? 0 : 3, []))],
                    [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0), new(TypeKind.Range, Element: 0)],
                    [new([new("n", 0)])])
                { Allocations = [new(3, 0, NativeAllocationPlacement.Frame), new(4, 0, NativeAllocationPlacement.Frame)] };
                NativeArtifact.Validate(program);
                using MemoryStream bytes = new();
                NativeArtifact.WriteProduced(program, bytes);
                bytes.Position = 0;
                foreach (NativeProgram route in new[] { HeapControl(program), program, NativeArtifact.Read(bytes) })
                {
                    await using NativeInterpreter interpreter = new(route);
                    try
                    {
                        _ = await interpreter.ExecuteAsync();
                        throw new InvalidOperationException($"{operation} crossed an original logical allocation boundary");
                    }
                    catch (MsyncException error) when (error.InnerException is ArgumentOutOfRangeException)
                    {
                        Check(error.Span == source, "Logical bounds failure preserves the original instruction source");
                    }
                }
            }
    }

    private static async Task PendingScopeFutureJourney()
    {
        const string source = """
            fn export(flag: Bool) {
                let row = object { done: "payload" -> ./destination, n: 1 };
                if flag { row::n = 2; } else { row::n = 3; };
                if row::n > 0 { [row::done] } else { [] }
            };
            export(true)
            """;
        foreach (int level in new[] { 0, 2 })
        {
            NativeProgram compiled = SourceCompiler.CompileNative(source, "pending-scope-future.msync", level);
            using MemoryStream bytes = new();
            NativeArtifact.WriteProduced(compiled, bytes);
            bytes.Position = 0;
            foreach (NativeProgram program in new[] { compiled, NativeArtifact.Read(bytes) })
            {
                string directory = Path.GetFullPath(Path.Combine(".temp", "native-allocation-future", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(directory);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                NativeRuntimeContext context = new(new(directory))
                {
                    TransferAsync = async (value, path, origin, token) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(token);
                        await File.WriteAllTextAsync(origin.LocalPath(path), value.AsString(), token);
                        return true;
                    }
                };
                RuntimeProfile profile = new();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                await using NativeInterpreter interpreter = new(program, new() { Profile = profile }, context);
                NativeRange range = (await interpreter.ExecuteAsync(timeout.Token)).AsRange();
                await using IAsyncEnumerator<NativeValue> iterator = range.Items.GetAsyncEnumerator(timeout.Token);
                Task<bool> first = iterator.MoveNextAsync().AsTask();
                try
                {
                    await started.Task.WaitAsync(timeout.Token);
                    Check(!first.IsCompleted && !File.Exists(Path.Combine(directory, "destination")),
                        "A deferred Range starts transfer but cannot yield its pending field before publication");
                }
                finally { release.TrySetResult(); }
                Check(await first.WaitAsync(timeout.Token) && iterator.Current.AsBool(),
                    "A pending field future survives Object/frame retirement");
                Check(!await iterator.MoveNextAsync() && await File.ReadAllTextAsync(Path.Combine(directory, "destination"), timeout.Token) == "payload",
                    "Pending Object future has checked membership and an independent file-byte oracle");
                ulong placed = profile.Drain().Where(sample => sample.Name == "vm.function").Aggregate(0UL,
                    (sum, sample) => sum + sample.Metrics["frame_placed_allocations"]);
                Check(level == 0 ? placed == 0 : placed > 0, "Only optimized pending Object storage is actually frame placed");
            }
        }
    }

    private static async Task AllBuiltinJourney()
    {
        string source = """
            fn probe(flag: Bool) {
                let row = object { n: 1, text: "roots", path: ./bound, endpoint: $local, flag: true,
                    type: Number, values: [1, 2], function: fn(n: Number) { n + 1 }, none?: null,
                    nested: object { n: 3 } };
                if flag { row::n = 7; } else { row::n = 9; };
                let total = row::n + $std::ranges::size(row::values) + row::function(2) + row::nested::n;
                if row::flag and row::text == "roots" and (row::path as String) == "./bound"
                    and (row::endpoint as String) == "local" and row::type == Number and row::none is null {
                    total
                } else { -1 }
            };
            probe(true)
            """;
        NativeProgram program = await Journey("builtin-rooted-fields", source, 15);
        Check(program.Allocations.Any(plan => plan.Placement == NativeAllocationPlacement.LogicalHeap),
            "Reference-field call paths retain heap storage when nonescape proof is conservative");
        if (OperatingSystem.IsWindows()) return;
        NativeProgram process = await Journey("process-field", """
            fn probe(flag: Bool) {
                let row = object { child: $std::process::spawn(/bin/sh, ["-c", "printf rooted"]), n: 1 };
                if flag { row::n = 9; } else { row::n = 5; };
                if $std::process::stdout(row::child) == "rooted" { row::n } else { -1 }
            };
            probe(true)
            """, 9);
        await ProfilePlacement(process);
    }

    private static async Task Measure(NativeProgram program)
    {
        NativeProgram heap = HeapControl(program);
        foreach (NativeProgram warm in new[] { heap, program })
        {
            await using NativeInterpreter interpreter = new(warm);
            for (int index = 0; index < 32; index++) _ = await interpreter.ExecuteAsync();
        }
        List<long> heapBytes = [], frameBytes = [];
        for (int trial = 0; trial < 5; trial++)
        {
            heapBytes.Add(await Allocated(heap));
            frameBytes.Add(await Allocated(program));
        }
        heapBytes.Sort(); frameBytes.Sort();
        Console.WriteLine($"Same optimized IR, 200 executions/trial, GC allocated bytes median: heap={heapBytes[2]}, frame={frameBytes[2]}, saved={heapBytes[2] - frameBytes[2]}");
        Check(frameBytes[2] < heapBytes[2], "Actual frame placement reduces measured managed allocation traffic");
    }

    private static NativeProgram HeapControl(NativeProgram program) => program with
    {
        Allocations = program.Allocations.Select(plan => plan.Placement == NativeAllocationPlacement.Frame
            ? plan with { Placement = NativeAllocationPlacement.LogicalHeap } : plan).ToArray()
    };

    private static async Task<long> Allocated(NativeProgram program)
    {
        await using NativeInterpreter interpreter = new(program);
        long start = GC.GetTotalAllocatedBytes(precise: true);
        for (int index = 0; index < 200; index++)
        {
            NativeValue result = await interpreter.ExecuteAsync();
            if (result.AsNumber() != 27) throw new InvalidOperationException("Measured execution changed oracle");
        }
        return GC.GetTotalAllocatedBytes(precise: true) - start;
    }
}
