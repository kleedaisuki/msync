using System.Runtime.CompilerServices;
using Msync.Core.Llvm;

internal static class ValueHandleProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new() { ValueHandleMetrics = new() };
        LlvmModule module = new("handles", context);
        LlvmType type = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "handles", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        LlvmConstantInt one = LlvmConstantInt.Get((LlvmIntegerType)type, 1);
        var oldValue = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], one);
        var replacement = new LlvmBinaryOperator(LlvmOpcode.Sub, function.Arguments[0], one);
        var user = new LlvmBinaryOperator(LlvmOpcode.Add, oldValue, one);
        block.Append(oldValue); block.Append(replacement); block.Append(user);
        block.Append(new LlvmReturnInstruction(context, user));
        using var weak = new LlvmWeakValueHandle(oldValue);
        using var tracking = new LlvmWeakTrackingValueHandle(oldValue);
        using var typed = new LlvmTrackingValueHandle<LlvmBinaryOperator>(oldValue);
        using var callback = new Probe(oldValue);
        bool observedBeforeUses = false;
        callback.Replaced = next =>
        {
            observedBeforeUses = ReferenceEquals(user.Left, oldValue);
            callback.MoveTo(next);
        };
        oldValue.ReplaceAllUsesWith(replacement);
        Require(observedBeforeUses && ReferenceEquals(user.Left, replacement), "RAUW callbacks run before the source use-list rewrite.");
        Require(ReferenceEquals(weak.Value, oldValue) && ReferenceEquals(tracking.Value, replacement) && ReferenceEquals(typed.Value, replacement),
            "WeakVH does not follow RAUW; WeakTrackingVH and TrackingVH do.");
        Require(ReferenceEquals(callback.Value, replacement), "CallbackVH may migrate its own registration during RAUW.");
        oldValue.EraseFromParent();
        Require(weak.Value is null && tracking.Value is not null, "Logical deletion nulls weak handles without deleting the replacement.");
        user.ReplaceAllUsesWith(one); user.EraseFromParent();
        replacement.EraseFromParent();
        Require(tracking.Value is null && callback.Value is null && callback.DeleteCount == 1,
            "Deleting the tracked replacement nulls both weak-tracking and default callback references.");

        var first = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], one);
        var second = new LlvmBinaryOperator(LlvmOpcode.Sub, function.Arguments[0], one);
        block.InsertBefore(first, block.GetTerminator()); block.InsertBefore(second, block.GetTerminator());
        using var skipped = new Probe(first);
        using var remover = new Probe(first);
        using var tail = new Probe(first);
        bool tailVisited = false;
        tail.Replaced = _ => { tailVisited = true; tail.Dispose(); };
        remover.Replaced = _ => { skipped.Dispose(); remover.Dispose(); };
        first.ReplaceAllUsesWith(second);
        Require(tailVisited && tail.ReplaceCount == 1 && remover.ReplaceCount == 1 && skipped.ReplaceCount == 0,
            "Source cursor permits self-removal and removal of pending callback neighbors without skipping surviving entries.");
        first.EraseFromParent(); second.EraseFromParent();
        Require(context.ValueHandleMetrics.GetValueOrDefault("value_handles.rauw.callbacks") >= 3 &&
            context.ValueHandleMetrics.GetValueOrDefault("value_handles.deleted.null") >= 2,
            "Handle notifications record actual callback and weak-null outcomes.");

        (LlvmWeakValueHandle retainedHandle, WeakReference<LlvmValue> collectedValue) = CreateUnrootedValueWithCallback();
        for (int attempt = 0; attempt < 3; ++attempt) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Require(!collectedValue.TryGetTarget(out _) && retainedHandle.Value is null,
            "Weak handle registry links do not root a neighboring callback, its retained value, or the target value.");
        retainedHandle.Dispose();
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LlvmWeakValueHandle, WeakReference<LlvmValue>) CreateUnrootedValueWithCallback()
    {
        LlvmContext context = new();
        LlvmIntegerType type = LlvmType.GetInt32Ty(context);
        LlvmValue value = new LlvmBinaryOperator(LlvmOpcode.Add, LlvmConstantInt.Get(type, 1), LlvmConstantInt.Get(type, 2));
        _ = new RetainingProbe(value);
        return (new LlvmWeakValueHandle(value), new WeakReference<LlvmValue>(value));
    }

    private sealed class Probe(LlvmValue value) : LlvmCallbackValueHandle(value)
    {
        internal Action<LlvmValue>? Replaced;
        internal int ReplaceCount;
        internal int DeleteCount;
        internal void MoveTo(LlvmValue value) => Value = value;
        protected override void AllUsesReplacedWith(LlvmValue replacement) { ++ReplaceCount; Replaced?.Invoke(replacement); }
        protected override void Deleted() { ++DeleteCount; base.Deleted(); }
    }
    private sealed class RetainingProbe(LlvmValue value) : LlvmCallbackValueHandle(value)
    {
        private readonly LlvmValue retained = value;
        protected override void Deleted() { GC.KeepAlive(retained); base.Deleted(); }
    }
}
