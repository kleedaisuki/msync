using System.Runtime.InteropServices;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using NativeMemory = Msync.Runtime.Native.NativeMemory;

internal static class RegisterRegionChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = await PressureAsync();
        checks += await SourcesAsync();
        checks += await ConcurrentInvalidationAsync();
        checks += await RefinementsAsync();
        checks += await SafepointsAsync();
        checks += await ContextualLoopAsync();
        checks += PinLifetimes();
        checks += await PendingBranchAsync();
        checks += await FusedLoadAsync();
        return checks;
    }

    private static NativeProgram PressureProgram(int inputs)
    {
        List<NativeInstruction> code = [];
        int previous = 0;
        for (int input = 1; input < inputs; input++)
        {
            int result = inputs + input - 1;
            code.Add(new(NativeOp.Binary, result, previous, input, Immediate: (long)NativeBinary.Add));
            previous = result;
        }
        code.Add(new(NativeOp.Return, A: previous));
        return new([], [new("register-pressure", Enumerable.Range(0, inputs * 2 - 1)
            .Select(slot => new NativeSlot(0, slot < inputs ? NativeSlotRole.Parameter : NativeSlotRole.Temporary)).ToArray(),
            code, new(Enumerable.Repeat(0, inputs).ToArray(), 0, []), ParameterCount: inputs)], [new(TypeKind.Number)], []);
    }

    private static async Task<int> PressureAsync()
    {
        NativeProgram program = PressureProgram(12);
        NativeArtifact.Validate(program);
        using MemoryStream artifact = new();
        NativeArtifact.Write(program, artifact); artifact.Position = 0;
        program = NativeArtifact.Read(artifact);
        NativeExecutionFunction function = new(new(program, new()), 0, new(NativeJitPolicy.Force));
        NativeFastOperation[] operations = Enumerable.Repeat(NativeFastOperation.NumberAdd, 11).Append(NativeFastOperation.Bridge).ToArray();
        NativeTierLayout layout = NativeTierLayout.Create(function, operations);
        NativeRegisterRegion region = layout.Regions.Single();
        if (region.Inputs.Length != 12 || region.Outputs.Length != 1 || region.Spills == 0 || layout.ScalarCount != 13)
            throw new InvalidOperationException("Register planning retained dead frame intermediates or did not exercise spills.");
        if (region.Values.Count(value => value.Register >= 0) == 0
            || Enumerable.Range(12, 10).Any(slot => layout.Scalars[slot] >= 0))
            throw new InvalidOperationException("Private intermediate values still require canonical frame descriptors.");
        Directory.CreateDirectory(".temp/jit-register-backend");
        foreach ((Architecture architecture, bool windows, string name) in new[]
        {
            (Architecture.X64, false, "pressure-linux-x64"), (Architecture.X64, true, "pressure-windows-x64"),
            (Architecture.Arm64, false, "pressure-linux-arm64")
        })
            File.WriteAllBytes($".temp/jit-register-backend/{name}.bin", NativeMachineCode.Emit(function, operations,
                architecture, windows, NativeInterpreter.TierBridgeAddress, NativeInterpreter.TierNumberAddress,
                NativeInterpreter.TierBoolAddress, program.Constants, layout, NativeInterpreter.TierCommitAddress));
        int checks = 2;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy, 1, 1, 1) });
            Random random = new(9017);
            for (int sample = 0; sample < 48; sample++)
            {
                double[] input = Enumerable.Range(0, 12).Select(_ => random.NextDouble() * 200 - 100).ToArray();
                double expected = input.Skip(1).Aggregate(input[0], (sum, value) => sum + value);
                double actual = (await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), input.Select(NativeValue.FromNumber).ToArray())).AsNumber();
                if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected))
                    throw new InvalidOperationException($"{policy}: register allocation/spilling changed sample {sample}.");
                checks++;
            }
            if (policy != NativeJitPolicy.Force) continue;
            NativeTierStatistics stats = interpreter.TierStatistics;
            if (stats.RegisterRegions != 1 || stats.RegisterSpills == 0 || stats.EliminatedFrameStores != 10 || stats.RawOperations != 48 * 11)
                throw new InvalidOperationException("Codegen telemetry does not describe the executed register/spill plan.");
            checks++;
        }
        return checks;
    }

    private static async Task<int> SourcesAsync()
    {
        string[] sources =
        [
            "fn kernel(a:Number,b:Number,c:Number) { let t=a+b; let u=t*c; u - a }; kernel(2,3,4)",
            "fn kernel(a:Number,b:Number) { let x=a+b; let y=x*b; if y>10 {y - x} else {x - y} }; kernel(2,3)",
            "fn kernel(a:Number,b:Number) { let x=a+b; x=x*b; x=x - a; x }; kernel(2,3)",
            "let i=0; let total=0; loop { if i>=600 {break;}; total=(total+i)*1; i=i+1; }; total"
        ];
        double[] expected = [18, 10, 13, 179700];
        int checks = 0;
        for (int fixture = 0; fixture < sources.Length; fixture++)
        {
            NativeProgram portable = Compiler.Compile(sources[fixture], "register-region-source.msync", 0);
            foreach (bool bound in new[] { false, true })
            {
                NativeProgram selected = bound ? NativePreparation.Prepare(portable) : portable;
                using MemoryStream artifact = new();
                NativeArtifact.Write(selected, artifact); artifact.Position = 0;
                NativeProgram restored = NativeArtifact.Read(artifact);
                foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
                {
                    await using NativeInterpreter interpreter = new(restored, new() { Tiers = new(policy, 1, 1, 1) });
                    if ((await interpreter.ExecuteAsync()).AsNumber() != expected[fixture])
                        throw new InvalidOperationException($"Source/artifact region journey {fixture}/{bound}/{policy} changed its result.");
                    checks++;
                }
            }
        }
        return checks;
    }

    private static async Task<int> ConcurrentInvalidationAsync()
    {
        NativeProgram program = new([], [new("dynamic-sum", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(0)],
            [new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)],
            new([0, 0], 0, []), ParameterCount: 2)], [new(TypeKind.Number | TypeKind.String)], []);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 2, 2, 2) });
        for (int warm = 0; warm < 2; warm++)
            _ = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(2), NativeValue.FromNumber(3)]);
        Task[] tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (int sample = 0; sample < 32; sample++)
            {
                bool text = (worker & 1) != 0;
                NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), text
                    ? [NativeValue.FromString("Klee"), NativeValue.FromString("𐀀")]
                    : [NativeValue.FromNumber(worker), NativeValue.FromNumber(sample)]);
                if (text ? actual.AsString() != "Klee𐀀" : actual.AsNumber() != worker + sample)
                    throw new InvalidOperationException("Concurrent native invalidation changed a real union operation.");
            }
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        if (interpreter.TierStatistics.GuardMisses == 0 || interpreter.TierStatistics.Entries == 0)
            throw new InvalidOperationException("Concurrent union changes did not exercise native code invalidation.");
        return 257;
    }

    private static async Task<int> RefinementsAsync()
    {
        SourceSpan first = new("region-refinement.msync", 10, 3), second = new("region-refinement.msync", 20, 3);
        NativeProgram program = new([], [new("incompatible-refinements", [new(0, NativeSlotRole.Parameter), new(1), new(2)],
            [new(NativeOp.Narrow, 1, 0, Type: 1, Source: first), new(NativeOp.Narrow, 2, 0, Type: 2, Source: second),
             new(NativeOp.Return, A: 2)], new([0], 2, []), ParameterCount: 1)],
            [new(TypeKind.Number | TypeKind.Bool), new(TypeKind.Number), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(program);
        int checks = 0;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        foreach (NativeValue input in new[] { NativeValue.FromNumber(1), NativeValue.True })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy, 1, 1, 1) });
            try
            {
                _ = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [input]);
                throw new InvalidOperationException("Register reuse bypassed an actual checked refinement.");
            }
            catch (MsyncException error) when (error.Span == (input.Kind == NativeKind.Number ? second : first)) { checks++; }
        }
        return checks;
    }

    private static async Task<int> SafepointsAsync()
    {
        List<NativeInstruction> code = [];
        for (int operation = 0; operation < 901; operation++)
            code.Add(new(NativeOp.Binary, 2, operation == 0 ? 0 : 2, 1, Immediate: (long)NativeBinary.Add));
        code.Add(new(NativeOp.Return, A: 2));
        NativeProgram program = new([], [new("region-safepoints", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(0)],
            code, new([0, 0], 0, []), ParameterCount: 2)], [new(TypeKind.Number)], []);
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes); bytes.Position = 0;
        program = NativeArtifact.Read(bytes);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        if ((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(3), NativeValue.FromNumber(2)])).AsNumber() != 1805)
            throw new InvalidOperationException("A register region lost its live-out at a cancellation safepoint.");
        NativeTierStatistics stats = interpreter.TierStatistics;
        if (stats.RegisterRegions != 4 || stats.RawOperations != 901 || stats.CancellationPolls < 3)
            throw new InvalidOperationException("Safepoint continuations replayed or double-counted register regions.");
        return 2;
    }

    private static async Task<int> ContextualLoopAsync()
    {
        string directory = Path.GetFullPath(".temp/jit-register-backend/contextual-loop");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "input.txt"), output = Path.Combine(directory, "output.txt");
        File.WriteAllText(input, "Klee𐀀");
        int checks = 0;
        foreach (int optimization in new[] { 0, 2 })
        {
            NativeProgram portable = Compiler.Compile("./input.txt -> ./output.txt; let i=0; let sum=0; "
                + "loop { if i>=100 {break;}; sum=sum+i; i=i+1; }; sum", Path.Combine(directory, "loop.msync"), optimization);
            File.Delete(output);
            NativeProgram prepared = NativePreparation.Prepare(portable, new BindingContext(directory, directory, null));
            if (File.Exists(output)) throw new InvalidOperationException("Preparing a loop executed its preceding transfer.");
            using MemoryStream wire = new();
            NativeArtifact.Write(prepared, wire); wire.Position = 0;
            NativeProgram detached = NativeArtifact.Read(wire);
            foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
            {
                File.Delete(output);
                await using NativeInterpreter interpreter = new(detached, new(WorkingDirectory: directory) { Tiers = new(policy, 1, 1, 1) });
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                if ((await interpreter.ExecuteAsync(timeout.Token)).AsNumber() != 4950 || File.ReadAllText(output) != "Klee𐀀")
                    throw new InvalidOperationException($"Contextual preparation froze the first loop iteration at O{optimization}/{policy}.");
                checks++;
            }
        }
        return checks;
    }

    private static unsafe int PinLifetimes()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 7)],
            [new("pin-leases", [new(0, NativeSlotRole.Local), new(0), new(1)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.FrameAddress, 2, 1),
                 new(NativeOp.BindLocation, A: 0, B: 2), new(NativeOp.Return, A: 1)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeImage image = new(program, new());
        NativeMemory memory = new();
        NativeFrame frame = new(image, 0, null, [], memory);
        NativeExecutionFunction function = new(image, 0, new(NativeJitPolicy.Force));
        using NativeTierMemory native = new(frame, memory, function, 0);
        nint workspace = (nint)native.Context;
        for (int binding = 0; binding < 12; binding++)
        {
            NativeAddress address = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
            address.Store(NativeValue.FromNumber(binding));
            frame.Bind(0, address);
            native.Refresh(0);
        }
        if (native.PinnedOwners != 13) throw new InvalidOperationException("Rebinding did not exercise distinct native pin owners.");
        native.Continue();
        if (native.PinnedOwners != 2 || (nint)native.Context != workspace || frame.GetDatum(0).Value.AsNumber() != 11)
            throw new InvalidOperationException("A poll recreated its workspace or retained obsolete allocation leases.");
        native.ReleasePins();
        if (native.PinnedOwners != 0) throw new InvalidOperationException("Suspension retained primitive-buffer pins.");
        return 3;
    }

    private static async Task<int> PendingBranchAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 0)],
            [new("pending-branch", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Local), new(2)],
                [new(NativeOp.BindLocation, A: 1, B: 0), new(NativeOp.Branch, A: 1, B: 2, C: 4),
                 new(NativeOp.Constant, 2, 0), new(NativeOp.Return, A: 2),
                 new(NativeOp.Constant, 2, 1), new(NativeOp.Return, A: 2)], new([0], 2, []), ParameterCount: 1),
             new("pending-writer", [new(0, NativeSlotRole.Parameter), new(3, NativeSlotRole.Parameter), new(2), new(2), new(1), new(2)],
                [new(NativeOp.CallNative, 2, Symbol: "process.exit_code", Inputs: [1]), new(NativeOp.Constant, 3, 0),
                 new(NativeOp.Binary, 4, 2, 3, Immediate: (long)NativeBinary.Equal), new(NativeOp.Store, A: 0, B: 4, C: 0, Type: 1),
                 new(NativeOp.Constant, 5, 1), new(NativeOp.Return, A: 5)], new([0, 3], 2, []), ParameterCount: 2)],
            [new(TypeKind.Never, Pointee: 1), new(TypeKind.Bool), new(TypeKind.Number), new(TypeKind.Process)], []);
        NativeArtifact.Validate(program);
        foreach (int exit in new[] { 0, 1 })
        {
            TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
            NativeAddress address = new NativeAllocation(new(NativeType.Of(NativeKind.Bool))).Field(0);
            await interpreter.InvokeAsync(NativeValue.FromFunction(new(1)),
                [NativeValue.FromAddress(address), NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]);
            address.Store(NativeValue.False);
            Task<NativeValue> branch = interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(address)]).AsTask();
            if (branch.IsCompleted) throw new InvalidOperationException("A native branch used shadowed physical Bool bytes instead of its pending value.");
            completion.SetResult(new(exit, "", ""));
            if ((await branch.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() != exit || interpreter.TierStatistics.GuardMisses < 2)
                throw new InvalidOperationException("A native branch did not resume its actual pending condition.");
        }
        return 4;
    }

    private static async Task<int> FusedLoadAsync()
    {
        NativeProgram program = new([],
            [new("fused-load", [new(0, NativeSlotRole.Parameter), new(1), new(0, NativeSlotRole.Local)],
                [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0), new(NativeOp.Return, A: 2)],
                new([0], 0, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeArtifact.Validate(program);
        NativeExecutionFunction function = new(new(program, new()), 0, new(NativeJitPolicy.Force));
        if (function.Instructions[0].Span != 2 || function.CanWriteRaw(2))
            throw new InvalidOperationException("Fused-load journey did not retain a managed location write.");
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy, 1, 1, 1) });
            if ((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(7)])).AsNumber() != 7)
                throw new InvalidOperationException("Native continuation replayed the interior of a fused Load.");
        }
        return 3;
    }
}
