using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class FloatingPredicateChecks
{
    internal static async Task<int> RunAsync()
    {
        var predicates = Enum.GetValues<LlvmCmpPredicate>().Where(predicate => predicate <= LlvmCmpPredicate.FCmpTrue).ToArray();
        double[] values = [-0d, 0d, -3d, 2d, double.NegativeInfinity, double.PositiveInfinity,
            double.NaN, BitConverter.UInt64BitsToDouble(0x7ff0000000000001UL)];
        NativeFunctionCode[] functions = predicates.Select(predicate => new NativeFunctionCode(predicate.ToString(),
            [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter), new(1)],
            [new(NativeOp.MachineCompare, 2, 0, 1, Immediate: (int)predicate), new(NativeOp.Return, A: 2)],
            new([0, 0], 1, []), ParameterCount: 2)
        {
            Storage = new(new NativeSlotStorage[3], new NativeInstructionStorage[2])
        }).Concat(predicates.Select(predicate => new NativeFunctionCode("pending." + predicate,
            [new(0, NativeSlotRole.Parameter), new(2, NativeSlotRole.Parameter), new(1), new(0)],
            [new(NativeOp.CallNative, 3, Symbol: "process.exit_code", Inputs: [1]),
             new(NativeOp.MachineCompare, 2, 0, 3, Immediate: (int)predicate), new(NativeOp.Return, A: 2)],
            new([0, 2], 1, []), ParameterCount: 2)
        {
            Storage = new(new NativeSlotStorage[4], new NativeInstructionStorage[3])
        })).ToArray();
        NativeProgram program = new([], functions, [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Process)], []);
        program = program with
        {
            Backend = new(Enumerable.Range(0, functions.Length)
                .Select(index => NativeStandardBackendEmitter.EmitFunction(program, index)).ToArray(), [])
        };
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes); bytes.Position = 0;
        program = NativeArtifact.Read(bytes);
        var image = new NativeImage(program, new());
        using var selected = new NativeExecutionFunction(image, 0, new(NativeJitPolicy.Force));
        var layout = NativeTierLayout.Create(selected,
            [NativeFastOperations.FloatingCompare(predicates[0]), NativeFastOperation.BoolReturn], image);
        Require(layout.Scalars[0] >= 0 && layout.Scalars[1] >= 0 && layout.Scalars[2] >= 0,
            "A direct floating comparison requires both operand scalars and its Bool result in the tier layout.");
        int checks = 1;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy, 1, 1, 1) });
            for (int index = 0; index < predicates.Length; ++index)
            foreach (double left in values)
            foreach (double right in values)
            {
                NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(index)),
                    [NativeValue.FromNumber(left), NativeValue.FromNumber(right)]);
                bool expected = Oracle(predicates[index], left, right);
                Require(actual.AsBool() == expected, $"{policy}/{predicates[index]} changed IEEE binary64 comparison.");
                ++checks;
            }
            if (policy == NativeJitPolicy.Force)
                Require(interpreter.TierStatistics.RawOperations > 0, "The predicate journey did not enter direct native comparison.");
            for (int index = 0; index < predicates.Length; ++index)
            {
                TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<NativeValue> result = interpreter.InvokeAsync(NativeValue.FromFunction(new(index + predicates.Length)),
                    [NativeValue.FromNumber(double.NaN), NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask();
                Require(!result.IsCompleted, "A pending runtime comparison consumed an unresolved numeric operand.");
                completion.SetResult(new(0, "", ""));
                Require((await result.WaitAsync(TimeSpan.FromSeconds(5))).AsBool() == Oracle(predicates[index], double.NaN, 0),
                    $"{policy}/{predicates[index]} changed pending unordered comparison.");
                checks += 2;
            }
        }
        foreach (var predicate in predicates)
        {
            TaskCompletionSource<NativeValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new NativeDatum(new NativeFuture(completion.Task, logicalType: TypeRef.Number,
                physicalType: NativeType.Of(NativeKind.Number)));
            NativeDatum result = NativeDatum.CombineMachine(pending, new(NativeValue.FromNumber(0)), predicate,
                static (left, right, selected) => NativeMachineOperations.Compare(selected, left, right), NativeType.Of(NativeKind.Bool));
            Require(!result.IsReady && result.Future!.Dependencies.Contains(pending.Future!), "Comparison forced or lost a pending operand.");
            completion.SetResult(NativeValue.FromNumber(double.NaN));
            Require((await result.ResolveAsync(CancellationToken.None)).AsBool() == Oracle(predicate, double.NaN, 0),
                $"Pending {predicate} changed unordered behavior.");
            checks += 2;
        }
        return checks;
    }

    private static bool Oracle(LlvmCmpPredicate predicate, double left, double right)
    {
        var a = new LlvmAPFloat(LlvmAPFloatSemantics.IEEEdouble, new LlvmAPInt(64, BitConverter.DoubleToUInt64Bits(left)));
        var b = new LlvmAPFloat(LlvmAPFloatSemantics.IEEEdouble, new LlvmAPInt(64, BitConverter.DoubleToUInt64Bits(right)));
        var order = a.Compare(b);
        int bit = order switch
        {
            LlvmFloatCompare.Equal => 1,
            LlvmFloatCompare.GreaterThan => 2,
            LlvmFloatCompare.LessThan => 4,
            _ => 8
        };
        return ((int)predicate & bit) != 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
