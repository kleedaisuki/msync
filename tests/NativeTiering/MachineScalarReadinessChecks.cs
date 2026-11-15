using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class MachineScalarReadinessChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        var descriptor = NativeMachineType.Integer(64, 8);
        var type = descriptor.PhysicalType;
        var input = new TaskCompletionSource<NativeValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum pending = new(new NativeFuture(input.Task, physicalType: type));
        Require(pending.OptionalSourceType is null && pending.PhysicalType == type && !pending.IsReady,
            "Pending machine scalars must have a physical type and no fabricated source type.");
        Counter state = new();
        NativeDatum mapped = pending.MapMachine(state, static (value, counter) =>
        {
            Interlocked.Increment(ref counter.Calls);
            return NativeValue.FromMachineInteger(value.AsMachineInteger() + new LlvmAPInt(64, 1), counter.Descriptor);
        }, type);
        Require(state.Calls == 0 && mapped.OptionalSourceType is null && mapped.Future!.Dependencies.Single() == pending.Future,
            "Installing a machine map retains the operand future without demanding or invoking its transform.");
        NativeAllocation storage = new(new NativeLayout(type, type));
        NativeMemory memory = new();
        memory.Write(storage.Field(0), mapped);
        memory.CopyCompiled(storage.Field(0), storage.Field(1));
        NativeDatum copied = memory.Read(storage.Field(1));
        Require(copied.Future == mapped.Future && state.Calls == 0 && !storage.Field(1).IsInitialized,
            "Pending machine storage/copy must forward the same physical future without initializing bytes.");
        var bits = new LlvmAPInt(64, 0xFEDCBA9876543210);
        input.SetResult(NativeValue.FromMachineInteger(bits));
        var result = await copied.ResolveAsync(CancellationToken.None);
        Require(result.AsMachineInteger() == bits + new LlvmAPInt(64, 1) && state.Calls == 1,
            "The machine transform executes once after its original dependency completes.");
        var ready = memory.Read(storage.Field(1));
        Require(ready.IsReady && ready.PhysicalType == type && ready.OptionalSourceType is null && storage.Field(1).IsInitialized,
            "Completing a future materializes the compiled raw machine slot without a source-type wrapper.");
        var leftCompletion = new TaskCompletionSource<NativeValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightCompletion = new TaskCompletionSource<NativeValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum left = new(new NativeFuture(leftCompletion.Task, physicalType: type));
        NativeDatum right = new(new NativeFuture(rightCompletion.Task, physicalType: type));
        Counter combinedState = new();
        NativeDatum combined = NativeDatum.CombineMachine(left, right, combinedState, static (first, second, counter) =>
        {
            Interlocked.Increment(ref counter.Calls);
            return NativeValue.FromMachineInteger(first.AsMachineInteger() ^ second.AsMachineInteger(), counter.Descriptor);
        }, type);
        leftCompletion.SetResult(NativeValue.FromMachineInteger(new(64, 0xF0)));
        Require(combinedState.Calls == 0 && combined.Future!.Dependencies.Count == 2 && combined.OptionalSourceType is null,
            "A combined machine result retains both operands and does not run on a partial dependency.");
        rightCompletion.SetResult(NativeValue.FromMachineInteger(new(64, 0x0F)));
        Require((await combined.ResolveAsync(CancellationToken.None)).AsMachineInteger().GetZExtValue() == 0xFF && combinedState.Calls == 1,
            "Combined exact machine operations run once after both operands complete.");
        var conditionCompletion = new TaskCompletionSource<NativeValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unchosenCompletion = new TaskCompletionSource<NativeValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum condition = new(new NativeFuture(conditionCompletion.Task, logicalType: TypeRef.Bool));
        NativeDatum unchosen = new(new NativeFuture(unchosenCompletion.Task, physicalType: type));
        var selected = NativeDatum.Select(condition, new(NativeValue.FromMachineInteger(bits)), unchosen, null, null, type);
        conditionCompletion.SetResult(NativeValue.True);
        Require((await selected.ResolveAsync(CancellationToken.None)).AsMachineInteger() == bits && !unchosenCompletion.Task.IsCompleted,
            "Pending machine Select chooses a candidate before resolving it, never demands the unchosen branch.");
        var sourceUnion = NativeType.Union(NativeType.Of(NativeKind.Null), NativeType.Of(NativeKind.String));
        var unionInput = new TaskCompletionSource<NativeValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeDatum unionDatum = new(new NativeFuture(unionInput.Task, logicalType: TypeRef.String));
        NativeDatum unionMap = unionDatum.MapMachine(0, static (value, _) => value, sourceUnion);
        Require(unionMap.OptionalSourceType == new TypeRef(TypeKind.Null | TypeKind.String),
            "Component projections to source unions retain the real source union rather than Any.");
        unionInput.SetResult(NativeValue.FromString("name"));
        _ = await unionMap.ResolveAsync(CancellationToken.None);
        var defaultSource = new NativeFuture(Task.FromResult(NativeValue.FromNumber(7)));
        Require(defaultSource.LogicalType == TypeRef.Any && defaultSource.PhysicalType.Equals(NativeType.FromTypeRef(TypeRef.Any)),
            "Existing source-future constructor defaults remain unchanged.");
        return checks;

        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            ++checks;
        }
    }

    private sealed class Counter
    {
        internal int Calls;
        internal NativeMachineType Descriptor { get; } = NativeMachineType.Integer(64, 8);
    }
}
