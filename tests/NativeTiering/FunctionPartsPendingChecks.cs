using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class FunctionPartsPendingChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeType number = NativeType.Of(NativeKind.Number);
        var layout = new NativeEnvironmentLayout([new(number, Location: true)]);
        var cell = new NativeAllocation(new([new NativeField("cell", number)])).Field(0);
        cell.Store(NativeValue.FromNumber(4));
        var environment = new NativeEnvironment(layout, [new NativeCapture(cell)]);
        TaskCompletionSource<NativeValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new NativeDatum(new NativeFuture(completion.Task, logicalType: new(TypeKind.Function),
            physicalType: NativeType.Of(NativeKind.Function)));
        var code = input.MapMachine(0, NativeFunctionParts.Component, NativeMachineType.Integer(64, 8).PhysicalType);
        var env = input.MapMachine(1, NativeFunctionParts.Component, NativeType.FromTypeRef(new(TypeKind.Null | TypeKind.Scope)));
        var name = input.MapMachine(2, NativeFunctionParts.Component, NativeType.FromTypeRef(new(TypeKind.Null | TypeKind.String)));
        var composed = NativeDatum.ComposeFunction(code, env, name, [layout], null);
        Require(!code.IsReady && !env.IsReady && !name.IsReady && !composed.IsReady && !completion.Task.IsCompleted,
            "Projection and packing must not synchronously demand a pending Function.");
        Require(code.Future!.Dependencies.Single() == input.Future && env.Future!.Dependencies.Single() == input.Future &&
            name.Future!.Dependencies.Single() == input.Future, "Each projection retains the same whole-datum dependency.");
        completion.SetResult(NativeValue.FromFunction(new(0, environment)));
        NativeValue result = await composed.ResolveAsync(CancellationToken.None);
        Require(result.AsFunction().Environment!.Address == environment.Address,
            "Pending packing retains the same existing environment region.");
        cell.Store(NativeValue.FromNumber(13));
        Require(result.AsFunction().Environment![0].Read().AsNumber() == 13,
            "Completing a pending Function must not snapshot its capture contents.");
        return 4;
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
