using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class MachinePendingChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeType i64 = NativeMachineType.Integer(64).PhysicalType;
        TaskCompletionSource<NativeValue> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NativeValue> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum a = new(new NativeFuture(first.Task, physicalType: i64));
        NativeDatum b = new(new NativeFuture(second.Task, physicalType: i64));
        NativeDatum sum = NativeDatum.CombineMachine(a, b, i64,
            static (left, right, type) => NativeMachineOperations.Binary(LlvmOpcode.Add, left, right, type), i64);
        Require(!sum.IsReady && !sum.Future!.Completion.IsCompleted && sum.OptionalSourceType is null && sum.PhysicalType == i64,
            "Pending machine arithmetic must retain exact physical type without fabricated Number/Any metadata.");
        Require(sum.Future!.Dependencies.Count == 2 && sum.Future.Dependencies.Contains(a.Future!) && sum.Future.Dependencies.Contains(b.Future!),
            "A deferred machine operation retains operand futures, not the execution frame.");
        first.SetResult(NativeValue.FromMachineInteger(new(64, 9007199254740993)));
        Require(!sum.Future.Completion.IsCompleted, "One ready operand must not force or invent the other result.");
        second.SetResult(NativeValue.FromMachineInteger(new(64, 3)));
        Require((await sum.ResolveAsync(CancellationToken.None)).AsMachineInteger() == new LlvmAPInt(64, 9007199254740996),
            "Pending arithmetic must not route i64 values through binary64.");
        TaskCompletionSource<NativeValue> delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum input = new(new NativeFuture(delayed.Task, physicalType: i64));
        NativeType i8 = NativeMachineType.Integer(8).PhysicalType;
        NativeDatum narrowed = input.MapMachine(i8,
            static (value, type) => NativeMachineOperations.Cast(LlvmOpcode.Trunc, value, type), i8);
        Require(!narrowed.IsReady && narrowed.PhysicalType == i8 && narrowed.OptionalSourceType is null,
            "Pending truncation preserves its destination descriptor.");
        delayed.SetResult(NativeValue.FromMachineInteger(new(64, 0x123480)));
        Require((await narrowed.ResolveAsync(CancellationToken.None)).AsMachineInteger() == new LlvmAPInt(8, 128),
            "The deferred cast must consume exactly one resolved operand.");
        NativeDatum ready = NativeDatum.CombineMachine(new(NativeValue.FromMachineInteger(new(64, 7))),
            new(NativeValue.FromMachineInteger(new(64, 9))), i64,
            static (left, right, type) => NativeMachineOperations.Binary(LlvmOpcode.Mul, left, right, type), i64);
        Require(ready.IsReady && ready.Future is null && ready.Value.AsMachineInteger() == new LlvmAPInt(64, 63),
            "Ready machine operands must execute directly without manufacturing a future.");
        return 7;
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
