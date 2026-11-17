using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;
using Msync.Runtime.Native;

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
        if (args is ["--bindings"])
        { await NativeBindingLoweringChecks.Run(Check); Console.WriteLine($"PASS {checks} binding contracts"); return; }
        await NativeBindingLoweringChecks.Run(Check);
        if (args is ["--emit", var source, var directory]) { Emit(source, directory); return; }
        if (args is ["--optimize", var artifact, var output])
        {
            NativeProgram raw = NativeArtifact.Read(artifact);
            if (raw.Stage != ArtifactStage.Relocatable) throw new InvalidOperationException("Optimization diagnostics require relocatable native IR.");
            Emit(raw, Path.GetFullPath(artifact), output);
            return;
        }
        await AllBuiltinInlining();
        await AllBuiltinRegisters();
        await PointerProofRegisters();
        await FrameAliasReferences();
        await NativeFrameAccessChecks.Run(Check);
        await CapturedInlining();
        await BoundLocationFlow();
        await ControlRelocation();
        await PlanRelocation();
        await GlobalMemoryFacts();
        await CrossBlockNarrowReuse();
        await SourceJourneys();
        await OwnershipBoundaries();
        Console.WriteLine($"PASS {checks} native middle-end contracts");
    }

    private static void Emit(string source, string directory)
    {
        Directory.CreateDirectory(directory);
        HirModule hir = Compiler.Analyze(File.ReadAllText(source), Path.GetFullPath(source));
        NativeProgram raw = Compiler.LowerInstructions(hir, 0);
        Emit(raw, Path.GetFullPath(source), directory);
    }

    private static void Emit(NativeProgram raw, string source, string directory)
    {
        Directory.CreateDirectory(directory);
        using StreamWriter report = new(Path.Combine(directory, "code.tsv"));
        report.WriteLine("level\tfunctions\tinstructions\tslots\tcalls\tnops\tallocations\tframe_addresses\tloads\tstores\tnarrows");
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = NativeOptimizer.Optimize(raw, level);
            NativeArtifact.Write(program, Path.Combine(directory, $"O{level}.mso"));
            NativeProgram prepared = NativePreparation.Prepare(program,
                new BindingContext(Path.GetFullPath(directory), HostContext.HomeDirectory, Path.GetFullPath(source)));
            NativeArtifact.Write(prepared, Path.Combine(directory, $"O{level}.mexe"));
            report.WriteLine(string.Join('\t', level, program.Functions.Count,
                program.Functions.Sum(function => function.Instructions.Count),
                program.Functions.Sum(function => function.Slots.Count),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.Call)),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.Nop)),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.Allocate)),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.FrameAddress)),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.Load)),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.Store)),
                program.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == NativeOp.Narrow))));
        }
    }

    private static NativeProgram Detached(NativeProgram program)
    {
        NativeArtifact.Validate(program);
        using MemoryStream encoded = new();
        NativeArtifact.Write(program, encoded);
        encoded.Position = 0;
        return NativeArtifact.Read(encoded);
    }

    private static async Task AllBuiltinInlining()
    {
        NativeAllocation allocation = new(new NativeLayout(), 0);
        NativeValue[] values = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(-0d),
            NativeValue.FromString("可莉𐀀"), NativeValue.FromPath(new("relative", "/origin")),
            NativeValue.FromConnection(new(ConnectionKind.Ssh, "configured-alias")), NativeValue.FromFunction(new(1)),
            NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(7)], TypeRef.Number)),
            NativeValue.FromProcess(new(0, "actual", "")), NativeValue.FromType(TypeRef.Number), NativeValue.FromAddress(allocation.Address)];
        foreach (NativeValue value in values)
        {
            NativeCodeType type = new(value.Type.Kind, Element: value.Kind == NativeKind.Range ? 2 : -1,
                Layouts: value.Kind == NativeKind.Address ? [0] : null);
            NativeProgram program = new([], [
                new("caller", [new(0, NativeSlotRole.Parameter), new(1), new(0)],
                    [new(NativeOp.MakeFunction, 1, 1), new(NativeOp.Call, 2, 1, 1, Inputs: [0]), new(NativeOp.Return, A: 2)],
                    new([0], 0, []), ParameterCount: 1),
                new("identity", [new(0, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)],
                    new([0], 0, []), ParameterCount: 1)
            ], [type, new(TypeKind.Function), new(TypeKind.Number)], [new([])]);
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                NativeProgram optimized = NativeOptimizer.Optimize(program, level);
                Check(optimized.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Call) == (level < 2),
                    value.Kind + ": the configured inline policy did not select a general typed call");
                foreach (NativeProgram route in new[] { optimized, Detached(optimized) })
                {
                    await using NativeInterpreter interpreter = new(route);
                    NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
                    Check(actual == value, value.Kind + ": typed inlining changed a builtin descriptor or identity");
                }
            }
        }
    }

    private static async Task ControlRelocation()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 41), new(NativeKind.Number, Number: 1)],
            [new("jump-chain", [new(0), new(0), new(1), new(0)],
                [new(NativeOp.Nop), new(NativeOp.Jump, A: 4), new(NativeOp.Allocate, 2, 0), new(NativeOp.Unreachable),
                 new(NativeOp.Nop), new(NativeOp.Constant, 0, 0), new(NativeOp.Jump, A: 8), new(NativeOp.Nop),
                 new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 3, 0, 1, Immediate: (long)NativeBinary.Add),
                 new(NativeOp.Return, A: 3)], new([], 0, []))], [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0])], [new([])])
        {
            Allocations = [new(2, 0)]
        };
        NativeProgram optimized = NativeOptimizer.Optimize(program);
        Check(optimized.Allocations.Count == 0, "An unreachable allocation retained a placement hint");
        Check(optimized.Functions[0].Instructions.All(instruction => instruction.Op is not (NativeOp.Nop or NativeOp.Jump or NativeOp.Allocate)),
            "Jump threading, unreachable removal and actual instruction compaction did not converge");
        foreach (NativeProgram route in new[] { program, Detached(optimized) })
        {
            await using NativeInterpreter interpreter = new(route);
            Check((await interpreter.ExecuteAsync()).AsNumber() == 42, "Relocation changed selected control flow");
        }
    }

    private static async Task CapturedInlining()
    {
        foreach (NativeCaptureMode mode in Enum.GetValues<NativeCaptureMode>())
        {
            NativeProgram program = new([new(NativeKind.String, "changed")], [
                new("caller", [new(0, NativeSlotRole.Parameter), new(1), new(0), new(2), new(0)],
                    [new(NativeOp.FrameAddress, 3, 0),
                     new(NativeOp.MakeFunction, 1, 1, 0, Inputs: [mode == NativeCaptureMode.Value ? 0 : 3]),
                     new(NativeOp.Constant, 0, 0),
                     new(NativeOp.Call, 2, 1, 1, Inputs: []),
                     new(NativeOp.Binary, 4, 0, 2, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 4)],
                    new([0], 0, []), ParameterCount: 1),
                new("capture", [new(0, NativeSlotRole.Capture)], [new(NativeOp.Return, A: 0)],
                    new([], 0, [new(0, mode)]), CaptureCount: 1)
            ], [new(TypeKind.String), new(TypeKind.Function), new(TypeKind.Never, Pointee: 0)],
                [new([new("0", mode == NativeCaptureMode.Value ? 0 : 2)])]);
            NativeProgram optimized = NativeOptimizer.Optimize(program);
            Check(optimized.Functions[0].Instructions.All(instruction => instruction.Op != NativeOp.Call),
                mode + ": the proven closure call was not inlined");
            foreach (NativeProgram route in new[] { program, Detached(optimized) })
            {
                await using NativeInterpreter interpreter = new(route);
                Check((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromString("capture-𐀀")])).AsString()
                    == (mode == NativeCaptureMode.Value ? "changedcapture-𐀀" : "changedchanged"),
                    mode + ": environment capture storage was copied or rebound incorrectly");
            }
        }
    }

    private static async Task AllBuiltinRegisters()
    {
        NativeValue[] samples = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(13), NativeValue.FromString("register-𐀀"),
            NativeValue.FromPath(new("relative", "/origin")), NativeValue.FromConnection(new(ConnectionKind.Ssh, "alias")),
            NativeValue.FromFunction(new(1)), NativeValue.FromRange(NativeRange.Snapshot([NativeValue.True], TypeRef.Bool)),
            NativeValue.FromProcess(new(0, "out", "err")), NativeValue.FromType(TypeRef.Number),
            NativeValue.FromAddress(new NativeAllocation(new NativeLayout(), 0).Address)];
        foreach (NativeValue sample in samples)
        {
            NativeCodeType type = new(sample.Type.Kind, Element: sample.Kind == NativeKind.Range ? 2 : -1,
                Layouts: sample.Kind == NativeKind.Address ? [0] : null);
            NativeProgram input = new([], [new("register-pressure", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter),
                new(1, NativeSlotRole.Parameter), new(0), new(0), new(0), new(0)],
                [new(NativeOp.Call, 3, 2, Inputs: [0]), new(NativeOp.Call, 4, 2, Inputs: [1]),
                 new(NativeOp.Move, 5, 3), new(NativeOp.Call, 6, 2, Inputs: [4]), new(NativeOp.Return, A: 6)],
                new([0, 0, 1], 0, []), ParameterCount: 3),
                new("identity", [new(0, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], new([0], 0, []), ParameterCount: 1)],
                [type, new(TypeKind.Function), new(TypeKind.Bool)], [new([])]);
            NativeProgram compact = Detached(NativeOptimizer.Optimize(input));
            Check(compact.Functions[0].Slots.Count < input.Functions[0].Slots.Count,
                sample.Kind + ": nonoverlapping typed temporaries did not share activation storage");
            foreach (NativeProgram route in new[] { input, compact })
            {
                NativeValue first = sample.Kind == NativeKind.Number ? NativeValue.FromNumber(7) : sample;
                await using NativeInterpreter interpreter = new(route);
                NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                    [first, sample, NativeValue.FromFunction(new(1))]);
                Check(result == sample, sample.Kind + ": Move consumed another live virtual register's storage");
            }
        }
    }

    private static async Task FrameAliasReferences()
    {
        NativeProgram input = new([], [new("frame-reference-write",
            [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Local),
             new(1), new(2), new(3)],
            [new(NativeOp.Copy, 2, 0), new(NativeOp.FrameAddress, 3, 2), new(NativeOp.Store, A: 3, B: 1, C: 0, Type: 0),
             new(NativeOp.AddressOffset, 4, 2, 0, 0), new(NativeOp.Load, 5, 4, Type: 3), new(NativeOp.Return, A: 5)],
            new([0, 0], 3, []), ParameterCount: 2)],
            [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0),
             new(TypeKind.Never, Pointee: 3), new(TypeKind.Number)], [new([new("key", 3)])]);
        NativeLayout layout = new([new NativeField("key", NativeType.Of(NativeKind.Number))]);
        NativeAllocation first = new(layout, 0), second = new(layout, 0);
        first.Field(0).Store(NativeValue.FromNumber(7));
        second.Field(0).Store(NativeValue.FromNumber(11));
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram optimized = Detached(NativeOptimizer.Optimize(input, level));
            await using NativeInterpreter interpreter = new(optimized);
            Check((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromAddress(first.Address), NativeValue.FromAddress(second.Address)])).AsNumber() == 11,
                "A store through FrameAddress was disconnected from a direct Scope register read");
        }
    }

    private static async Task PointerProofRegisters()
    {
        NativeProgram input = new([], [new("environment-layouts",
            [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter),
             new(1), new(3), new(2), new(1), new(3), new(2), new(2)],
            [new(NativeOp.EnvironmentAddress, 2, 0, 0), new(NativeOp.AddressOffset, 3, 2, 0, 0),
             new(NativeOp.Load, 4, 3, Type: 2),
             new(NativeOp.EnvironmentAddress, 5, 1, 1), new(NativeOp.AddressOffset, 6, 5, 1, 1),
             new(NativeOp.Load, 7, 6, Type: 2), new(NativeOp.Binary, 8, 4, 7, Immediate: (long)NativeBinary.Add),
             new(NativeOp.Return, A: 8)], new([0, 0], 2, []), ParameterCount: 2)],
            [new(TypeKind.Function), new(TypeKind.Scope), new(TypeKind.Number), new(TypeKind.Never, Pointee: 2)],
            [new([new("0", 2)]), new([new("0", 2), new("1", 2)])]);
        NativeEnvironment first = new(new([new(NativeType.Of(NativeKind.Number))]),
            [new(NativeValue.FromNumber(7))]);
        NativeEnvironment second = new(new([new(NativeType.Of(NativeKind.Number)), new(NativeType.Of(NativeKind.Number))]),
            [new(NativeValue.FromNumber(100)), new(NativeValue.FromNumber(11))]);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram optimized = Detached(NativeOptimizer.Optimize(input, level));
            NativeInstruction[] environments = optimized.Functions[0].Instructions.Where(item => item.Op == NativeOp.EnvironmentAddress).ToArray();
            Check(environments[0].Result != environments[1].Result,
                "Disjoint Scope lifetimes with different allocation proofs shared one proof register");
            if (level > 0)
            {
                NativeInstruction[] pointers = optimized.Functions[0].Instructions.Where(item => item.Op == NativeOp.AddressOffset).ToArray();
                Check(pointers[0].Result == pointers[1].Result,
                    "Compatible physical pointer proofs prevented disjoint temporary reuse");
                Check(optimized.Functions[0].Slots.Count < input.Functions[0].Slots.Count,
                    "Pointer-proof preservation disabled compatible temporary reuse");
            }
            await using NativeInterpreter interpreter = new(optimized);
            NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromFunction(new(0, first)), NativeValue.FromFunction(new(0, second))]);
            Check(actual.AsNumber() == 18, "Typed slot allocation changed closure environment selection");
        }
        NativeProgram invalid = input with
        {
            Functions = [input.Functions[0] with
        {
            Instructions = input.Functions[0].Instructions.Select(item => item.Op == NativeOp.EnvironmentAddress && item.Result == 5
                ? item with { Result = 2 } : item.Op == NativeOp.AddressOffset && item.A == 5 ? item with { A = 2 } : item).ToArray()
        }]
        };
        try
        {
            NativeArtifact.Validate(invalid);
            throw new InvalidOperationException("A mixed-layout register lost its direct-address proof without rejection");
        }
        catch (MsyncException error) when (error.Message.Contains("one allocation layout", StringComparison.Ordinal)) { checks++; }
    }

    private static async Task PlanRelocation()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 17)], [new("prepared", [new(0)],
            [new(NativeOp.Nop), new(NativeOp.ExecutePlan, A: 0), new(NativeOp.Constant, 0, 0),
             new(NativeOp.Nop), new(NativeOp.Return, A: 0)], new([], 0, []))], [new(TypeKind.Number)], [])
        {
            Stage = ArtifactStage.Executable,
            Context = new(Environment.CurrentDirectory, Environment.CurrentDirectory),
            ExecutionPlans = [new(0, 1, 4, [], [], 0)]
        };
        NativeProgram compact = Detached(NativeBackendCompiler.Compile(NativeOptimizer.Optimize(program), new()));
        Check(compact.ExecutionPlans![0].Start == 0 && compact.ExecutionPlans[0].End == 2,
            "Code compaction did not relocate the prepared plan's exclusive PC interval");
        await using NativeInterpreter interpreter = new(compact);
        Check((await interpreter.ExecuteAsync()).AsNumber() == 17, "Prepared plan relocation changed its residual continuation");
        NativeProgram cycle = new([], [new("cycle", [], [new(NativeOp.Nop), new(NativeOp.Jump, A: 0)], new([], 0, []))], [new(TypeKind.Null)], []);
        NativeProgram loop = Detached(NativeOptimizer.Optimize(cycle));
        Check(loop.Functions[0].Instructions.Count == 1 && loop.Functions[0].Instructions[0] is { Op: NativeOp.Jump, A: 0 },
            "Jump threading erased an infinite empty loop");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(20));
        await using NativeInterpreter running = new(loop);
        try { await running.ExecuteAsync(cancellation.Token); throw new InvalidOperationException("Compacted cycle ignored cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private static async Task BoundLocationFlow()
    {
        SourceSpan span = new("bound-location.msync", 7, 9);
        NativeCodeType[] types = [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Function)];
        NativeProgram uninitialized = new([], [new("alias-read", [new(0), new(1), new(0)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.BindLocation, A: 2, B: 1), new(NativeOp.Return, A: 2, Source: span)],
            new([], 0, []))], types, []);
        NativeProgram valid = Detached(uninitialized);
        await using (NativeInterpreter interpreter = new(valid))
        {
            try { await interpreter.ExecuteAsync(); throw new InvalidOperationException("Binding invented initialized data"); }
            catch (MsyncException error)
            {
                Check(error.Span == span && error.Message.Contains("uninitialized", StringComparison.Ordinal),
                    "Bound uninitialized data lost the actual memory-access diagnostic");
            }
        }
        foreach (NativeInstruction[] code in new NativeInstruction[][] {
            [new(NativeOp.BindLocation, A: 2, B: 1), new(NativeOp.Return, A: 2)],
            [new(NativeOp.Return, A: 2)] })
        {
            try
            {
                NativeArtifact.Validate(uninitialized with { Functions = [uninitialized.Functions[0] with { Instructions = code }] });
                throw new InvalidOperationException("Register initialization rule was weakened beyond alias binding");
            }
            catch (MsyncException error)
            { Check(error.Message.Contains("not definitely initialized", StringComparison.Ordinal), "Unexpected invalid-register diagnostic"); }
        }
        NativeProgram writeFirst = new([new(NativeKind.Number, Number: 19)], [
            new("caller", [new(0, NativeSlotRole.Local), new(1), new(2), new(0)],
                [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.MakeFunction, 2, 1, 0, Inputs: [1]),
                 new(NativeOp.Call, 3, 2, 1, Inputs: []), new(NativeOp.Load, 3, 1, Type: 0), new(NativeOp.Return, A: 3)], new([], 0, [])),
            new("initialize-capture", [new(0, NativeSlotRole.Capture)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, [new(0, NativeCaptureMode.Location)]), CaptureCount: 1)
        ], types, [new([new("0", 1)])]);
        foreach (int level in new[] { 0, 2, 3 })
        {
            NativeProgram route = Detached(NativeOptimizer.Optimize(writeFirst, level));
            await using NativeInterpreter interpreter = new(route);
            Check((await interpreter.ExecuteAsync()).AsNumber() == 19,
                "Inlining read a write-before-read capture before its initializer");
        }
    }

    private static async Task SourceJourneys()
    {
        (string Name, string Source, double Result)[] cases = [
            ("value-capture", "let offset = 3; fn add(value: Number) { value + offset }; add(4)", 7),
            ("location-capture", "let state = 1; fn next(value: Number) { state = state + value; state }; let a = next(3); state = 10; a + next(2)", 16),
            ("scope-result", "fn make(value: Number) { scope { n: value, text: \"record\", path: ./file, endpoint: $local } }; let first = make(3); let second = make(7); first::n = 11; first::n + second::n", 18),
            ("branch-results", "fn choose(flag: Bool, value: Number) { if flag { return value + 1; }; value + 2 }; choose(true, 3) + choose(false, 5)", 11),
            ("captured-parameter-lifetime", "fn make(start: Number) { fn() { start = start + 1; start } }; let a = make(3); let b = make(10); a() + b() + a()", 20),
            ("rebound-scope-capture", "let current = scope { n: 1 }; fn replace() { current = scope { n: 2 }; null }; replace(); current::n", 2),
            ("nullable-scope-frame", "fn next(value?: Number | Null) { if value is null { 0 } else { value + 1 } }; let initial?: Number = null; let node = scope { key?: initial }; fn from_node(node) { let key? = node::key; if key is null { 0 } else { key + 1 } }; let empty = from_node(node); node::key = 7; next(null) + next(3) + empty + from_node(node)", 12),
            ("scope-capture", "let shared = scope { n: 1 }; fn change() { shared::n = shared::n + 2; shared::n }; change() + change()", 8),
            ("loop-call", "fn increment(value: Number) { value + 1 }; let sum = 0; for value in $std::ranges::iota(0, 20) { sum = sum + increment(value); }; sum", 210),
            ("changed-function", "let pick = fn(value: Number) { value + 1 }; let before = pick(2); pick = fn(value: Number) { value + 10 }; before + pick(2)", 15),
            ("callee-before-argument", "let pick = fn(value: Number) { value + 1 }; fn mutate() { pick = fn(value: Number) { value + 100 }; 4 }; pick(mutate())", 5)
        ];
        foreach ((string name, string source, double expected) in cases)
        {
            HirModule hir = Compiler.Analyze(source, name + ".msync");
            using MemoryStream encoded = new();
            HirArtifact.Write(hir with { Sources = [.. hir.Sources.Select(document => document with { Text = null })] }, encoded);
            encoded.Position = 0;
            HirModule standalone = HirArtifact.Read(encoded);
            foreach (int level in new[] { 0, 1, 2, 3 })
                foreach (HirModule input in new[] { hir, standalone })
                {
                    NativeProgram program = Compiler.LowerInstructions(input, level);
                    if (name == "loop-call" && level >= 2)
                        Check(program.Functions[program.EntryPoint].Instructions.All(instruction => instruction.Op != NativeOp.Call),
                            "A private immutable function cell lost its construction proof across loop backedges");
                    foreach (NativeProgram route in new[] { program, Detached(program) })
                    {
                        await using NativeInterpreter interpreter = new(route);
                        Check((await interpreter.ExecuteAsync()).AsNumber() == expected,
                            $"{name} O{level}: source/stripped-HIR/native artifact mismatch");
                    }
                }
        }
    }

    private static async Task GlobalMemoryFacts()
    {
        NativeCodeType[] types = [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Never, Pointee: 0)];
        NativeInstruction[] code = [new(NativeOp.FrameAddress, 3, 2), new(NativeOp.Store, A: 3, B: 1, C: 1, Type: 0),
            new(NativeOp.Branch, A: 0, B: 3, C: 5), new(NativeOp.Nop), new(NativeOp.Jump, A: 7),
            new(NativeOp.Nop), new(NativeOp.Jump, A: 7), new(NativeOp.Load, 4, 3, Type: 0), new(NativeOp.Return, A: 4)];
        NativeProgram input = new([new(NativeKind.Number, Number: 99)], [new("private-memory",
            [new(1, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(0), new(2), new(0)],
            code, new([1, 0], 0, []), ParameterCount: 2)], types, []);
        NativeProgram optimized = Detached(NativeOptimizer.Optimize(input));
        Check(optimized.Functions[0].Instructions.All(instruction => instruction.Op != NativeOp.Load),
            "A must-reaching private store was not forwarded across both CFG predecessors");
        NativeProgram redefined = input with
        {
            Functions = [input.Functions[0] with
        {
            Slots = [.. input.Functions[0].Slots, new(0)],
            Instructions = [.. code.Take(8).Select((instruction, pc) => pc == 3 ? new NativeInstruction(NativeOp.Constant, 1, 0) : instruction),
                new(NativeOp.Binary, 5, 4, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 5)]
        }]
        };
        NativeProgram guarded = Detached(NativeOptimizer.Optimize(redefined));
        Check(guarded.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Load),
            "A stored value was forwarded from a register redefined on one predecessor");
        foreach (bool condition in new[] { false, true })
            foreach ((NativeProgram route, double expected) in new[] { (input, 7d), (optimized, 7d),
                (redefined, condition ? 106d : 14d), (guarded, condition ? 106d : 14d) })
            {
                await using NativeInterpreter interpreter = new(route);
                NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                    [NativeValue.FromBool(condition), NativeValue.FromNumber(7)]);
                Check(result.AsNumber() == expected, "Global private-memory facts changed the stored value through a CFG join");
            }
    }

    private static async Task CrossBlockNarrowReuse()
    {
        foreach (int blocks in new[] { 2, 3, 5 })
        {
            List<NativeSlot> slots = [new(0, NativeSlotRole.Parameter), new(1)];
            List<NativeInstruction> code =
            [
                new(NativeOp.Unary, 1, 0, Immediate: (long)NativeUnary.IsNull),
                new(NativeOp.Branch, A: 1, B: 2 + 2 * blocks, C: 2)
            ];
            for (int block = 0; block < blocks; block++)
            {
                slots.Add(new(2));
                code.Add(new(NativeOp.Narrow, 2 + block, 0, Type: 2));
                code.Add(block == blocks - 1 ? new(NativeOp.Return, A: 2 + block)
                    : new(NativeOp.Jump, A: code.Count + 1));
            }
            slots.Add(new(2));
            code.Add(new(NativeOp.Constant, slots.Count - 1, 0));
            code.Add(new(NativeOp.Return, A: slots.Count - 1));
            NativeProgram source = new([new(NativeKind.Number, Number: -1)],
                [new("cross-block-narrow-reuse", slots, code, new([0], 2, []), ParameterCount: 1)],
                [new(TypeKind.Number | TypeKind.Null), new(TypeKind.Bool), new(TypeKind.Number)], []);
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                NativeProgram program = Detached(NativeOptimizer.Optimize(source, level));
                if (level > 0)
                    Check(program.Functions[0].Instructions.Count(instruction => instruction.Op == NativeOp.Narrow) == 1,
                        $"O{level}: repeated cross-block narrows did not share one live definition");
                await using NativeInterpreter interpreter = new(program);
                foreach (NativeValue input in new[] { NativeValue.Null, NativeValue.FromNumber(42), NativeValue.FromNumber(-0d) })
                {
                    NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [input]);
                    double expected = input.Kind == NativeKind.Null ? -1 : input.AsNumber();
                    Check(BitConverter.DoubleToUInt64Bits(result.AsNumber()) == BitConverter.DoubleToUInt64Bits(expected),
                        $"O{level}: cross-block narrow reuse changed the selected branch or number bits");
                }
            }
        }
    }

    private static async Task OwnershipBoundaries()
    {
        List<(string Name, string Source, NativeOp Effect)> cases = [
            ("selected-transfer", "fn publish() { \"ready\" -> ./published; null }; publish(); if $std::fs::exists(./published) { 1 } else { 0 }", NativeOp.Transfer)
        ];
        if (!OperatingSystem.IsWindows() && File.Exists("/bin/sh"))
            cases.Add(("owned-process", "fn launch() { let child = /bin/sh(\"-c\", \"sleep 0.02; printf done > child-done\"); null }; launch(); if $std::fs::exists(./child-done) { 1 } else { 0 }", NativeOp.ProcessCall));
        foreach ((string name, string source, NativeOp effect) in cases)
            foreach (int level in new[] { 0, 2, 3 })
            {
                NativeProgram program = Compiler.Compile(source, name + ".msync", level);
                Check(program.Functions[program.EntryPoint].Instructions.Any(instruction => instruction.Op == NativeOp.Call
                    && instruction.B >= 0 && program.Functions[instruction.B].Instructions.Any(operation => operation.Op == effect)),
                    "Inlining erased a call's selected-work ownership boundary");
                int number = 0;
                foreach (NativeProgram route in new[] { program, Detached(program) })
                {
                    string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "native-middleend-ownership", name, level.ToString(), (number++).ToString());
                    Directory.CreateDirectory(directory);
                    string output = Path.Combine(directory, effect == NativeOp.Transfer ? "published" : "child-done");
                    File.Delete(output);
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    await using NativeInterpreter interpreter = new(route, new(WorkingDirectory: directory));
                    Check((await interpreter.ExecuteAsync(timeout.Token)).AsNumber() == 1,
                        "A caller advanced before a callee's private selected work completed");
                    Check(await File.ReadAllTextAsync(output) == (effect == NativeOp.Transfer ? "ready" : "done"),
                        "Selected work produced incorrect bytes after an optimized call");
                }
            }
    }
}
