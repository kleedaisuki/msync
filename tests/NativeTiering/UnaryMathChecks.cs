using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class UnaryMathChecks
{
    internal static async Task<int> RunAsync()
    {
        (string Symbol, Func<double, double> Oracle)[] cases =
        [
            ("math.abs", Math.Abs), ("math.sqrt", Math.Sqrt), ("math.floor", Math.Floor),
            ("math.ceil", Math.Ceiling), ("math.trunc", Math.Truncate),
            ("math.round_even", value => Math.Round(value, MidpointRounding.ToEven))
        ];
        double[] samples =
        [
            -0d, 0d, -0.5, 0.5, -1.5, 1.5, -2.5, 2.5, double.Epsilon, -double.Epsilon,
            double.MaxValue, double.MinValue, double.PositiveInfinity, double.NegativeInfinity, double.NaN
        ];
        NativeFunctionCode[] functions = cases.Select(item => new NativeFunctionCode(item.Symbol,
            [new(0, NativeSlotRole.Parameter), new(0), new(0)],
            [new(NativeOp.Copy, 1, 0), new(NativeOp.CallNative, 2, Symbol: item.Symbol, Inputs: [1]),
             new(NativeOp.Return, A: 2)],
            new([0], 0, []), ParameterCount: 1)
        {
            Storage = new(new NativeSlotStorage[3], new NativeInstructionStorage[3])
        }).Append(new NativeFunctionCode("pending-floor",
            [new(1, NativeSlotRole.Parameter), new(0), new(0)],
            [new(NativeOp.CallNative, 1, Symbol: "process.exit_code", Inputs: [0]),
             new(NativeOp.CallNative, 2, Symbol: "math.floor", Inputs: [1]),
             new(NativeOp.Return, A: 2)], new([1], 0, []), ParameterCount: 1)
        {
            Storage = new(new NativeSlotStorage[3], new NativeInstructionStorage[3])
        }).ToArray();
        NativeProgram program = new([], functions, [new(TypeKind.Number), new(TypeKind.Process)], []);
        program = program with
        {
            Backend = new(Enumerable.Range(0, functions.Length)
                .Select(index => NativeStandardBackendEmitter.EmitFunction(program, index) with
                {
                    // Cover both a math op inside a register region and a
                    // demand exit when it consumes an unresolved process result.
                    RawWritable = index < cases.Length ? [false, true, true] : [false, false, false]
                }).ToArray(), [])
        };
        using MemoryStream stream = new();
        NativeArtifact.Write(program, stream);
        stream.Position = 0;
        program = NativeArtifact.Read(stream);

        int checks = 0;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            RuntimeProfile profile = new(instructionSites: true);
            await using NativeInterpreter vm = new(program, new() { Profile = profile, Tiers = new(policy, 1, 1, 1) });
            for (int index = 0; index < cases.Length; ++index)
            foreach (double sample in samples)
            {
                double actual = (await vm.InvokeAsync(NativeValue.FromFunction(new(index)),
                    [NativeValue.FromNumber(sample)])).AsNumber();
                double expected = cases[index].Oracle(sample);
                Require(double.IsNaN(expected) ? double.IsNaN(actual) :
                    BitConverter.DoubleToUInt64Bits(actual) == BitConverter.DoubleToUInt64Bits(expected),
                    $"{policy}: {cases[index].Symbol}({sample}) changed IEEE results.");
                ++checks;
            }
            TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<NativeValue> pending = vm.InvokeAsync(NativeValue.FromFunction(new(cases.Length)),
                [NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask();
            Require(!pending.IsCompleted, "A native rounding site consumed pending storage.");
            completion.SetResult(new(-7, "", ""));
            Require((await pending.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() == -7, "Pending rounding lost the native result.");
            checks += 2;

            var recorded = profile.Drain();
            foreach (var item in cases)
            {
                var native = recorded.Single(sample => sample.Name == "vm.native" && sample.Attributes["native.name"] == item.Symbol);
                Require(native.Metrics["calls"] == (ulong)(samples.Length + (item.Symbol == "math.floor" ? 1 : 0)),
                    $"{policy}: {item.Symbol} lost semantic native-call telemetry.");
                ++checks;
            }
            if (policy == NativeJitPolicy.Force && NativeCodePage.IsSupported)
            {
                bool rounding = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || Sse41.IsSupported;
                ulong count = recorded.Where(sample => sample.Name == "vm.function")
                    .Aggregate(0UL, (sum, sample) => sum + sample.Metrics["jit_unary_math_sites"]);
                Require(count == (rounding ? (ulong)cases.Length + 1 : 2UL), "Profile lost the direct unary-math site selection.");
                Require(vm.TierStatistics.RegisterRegions > 0 &&
                    vm.TierStatistics.RawOperations >= (rounding ? 2 * cases.Length : cases.Length + 2) * samples.Length,
                    "The typed unary-math journey did not execute direct machine operations.");
                checks += 2;
            }
        }
        return checks;
    }

    private static void Require(bool valid, string error)
    {
        if (!valid) throw new InvalidOperationException(error);
    }
}
