// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ValueHandle.h,
// IR/Value.cpp ValueHandleBase intrusive registration and notifications.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract class LlvmValueHandle : IDisposable
{
    internal enum HandleKind : byte { Callback, Weak, WeakTracking }
    private WeakReference<LlvmValue>? target;
    private readonly HandleKind kind;
    private LlvmValueHandleNode? node;

    private protected LlvmValueHandle(HandleKind kind, LlvmValue? value)
    { this.kind = kind; SetValue(value); }
    protected LlvmValue? HandleValue => target is not null && target.TryGetTarget(out LlvmValue? value) ? value : null;

    protected void SetValue(LlvmValue? value)
    {
        LlvmValue? oldValue = HandleValue;
        if (ReferenceEquals(oldValue, value) && value is not null) return;
        node?.RemoveFromUseList(oldValue);
        if (target is null && value is not null) target = new(value);
        else target?.SetTarget(value!);
        if (value is null) return;
        node ??= new(this);
        node.InsertAtHead(value);
    }

    public void Dispose() => SetValue(null);

    internal static void ValueIsDeleted(LlvmValue value)
    {
        LlvmValueHandleNode? entry = value.FirstValueHandle;
        Debug.Assert(entry is not null);
        Count(value.Context, "value_handles.deleted.values");
        var iterator = new LlvmValueHandleNode();
        iterator.InsertAtHead(value);
        while (entry is not null)
        {
            iterator.RemoveFromUseList(value);
            iterator.InsertAfter(entry);
            if (entry.TryGetHandle(out LlvmValueHandle? handle))
            {
                Count(value.Context, "value_handles.deleted.handles");
                switch (handle.kind)
                {
                    case HandleKind.Weak:
                    case HandleKind.WeakTracking:
                        Count(value.Context, "value_handles.deleted.null");
                        handle.SetValue(null);
                        break;
                    case HandleKind.Callback:
                        Count(value.Context, "value_handles.deleted.callbacks");
                        ((LlvmCallbackValueHandle)handle).DispatchDeleted();
                        break;
                }
            }
            else entry.RemoveFromUseList(value);
            entry = iterator.Next;
        }
        iterator.RemoveFromUseList(value);
        if (value.FirstValueHandle is not null)
            throw new InvalidOperationException("All value handles must remove their reference in the deletion callback.");
    }

    internal static void ValueIsReplaced(LlvmValue oldValue, LlvmValue replacement)
    {
        LlvmValueHandleNode? entry = oldValue.FirstValueHandle;
        Debug.Assert(entry is not null && !ReferenceEquals(oldValue, replacement));
        Debug.Assert(ReferenceEquals(oldValue.Type, replacement.Type));
        Count(oldValue.Context, "value_handles.rauw.values");
        var iterator = new LlvmValueHandleNode();
        iterator.InsertAtHead(oldValue);
        while (entry is not null)
        {
            iterator.RemoveFromUseList(oldValue);
            iterator.InsertAfter(entry);
            if (entry.TryGetHandle(out LlvmValueHandle? handle))
            {
                Count(oldValue.Context, "value_handles.rauw.handles");
                switch (handle.kind)
                {
                    case HandleKind.WeakTracking:
                        Count(oldValue.Context, "value_handles.rauw.follow");
                        handle.SetValue(replacement);
                        break;
                    case HandleKind.Callback:
                        Count(oldValue.Context, "value_handles.rauw.callbacks");
                        ((LlvmCallbackValueHandle)handle).DispatchReplaced(replacement);
                        break;
                }
            }
            else entry.RemoveFromUseList(oldValue);
            entry = iterator.Next;
        }
        iterator.RemoveFromUseList(oldValue);
    }

    private static void Count(LlvmContext context, string name)
    {
        if (context.ValueHandleMetrics is { } metrics) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}

// A registry entry is separate from its weakly held managed handle. This keeps
// an externally held WeakVH from rooting neighboring callbacks and their caches.
internal sealed class LlvmValueHandleNode
{
    private readonly WeakReference<LlvmValueHandle>? handle;
    internal LlvmValueHandleNode? Previous;
    internal LlvmValueHandleNode? Next;
    internal LlvmValueHandleNode() { }
    internal LlvmValueHandleNode(LlvmValueHandle valueHandle) => handle = new(valueHandle);
    internal bool TryGetHandle([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LlvmValueHandle? valueHandle)
    {
        valueHandle = null;
        return handle is not null && handle.TryGetTarget(out valueHandle);
    }
    internal void InsertAtHead(LlvmValue value)
    {
        Next = value.FirstValueHandle;
        if (Next is not null) Next.Previous = this;
        value.FirstValueHandle = this;
    }
    internal void InsertAfter(LlvmValueHandleNode entry)
    {
        Next = entry.Next;
        Previous = entry;
        entry.Next = this;
        if (Next is not null) Next.Previous = this;
    }
    internal void RemoveFromUseList(LlvmValue? value)
    {
        if (Previous is not null) Previous.Next = Next;
        else if (value is not null && ReferenceEquals(value.FirstValueHandle, this)) value.FirstValueHandle = Next;
        if (Next is not null) Next.Previous = Previous;
        Previous = Next = null;
    }
}

public sealed class LlvmWeakValueHandle(LlvmValue? value = null) : LlvmValueHandle(HandleKind.Weak, value)
{
    public LlvmValue? Value { get => HandleValue; set => SetValue(value); }
}

public sealed class LlvmWeakTrackingValueHandle(LlvmValue? value = null) : LlvmValueHandle(HandleKind.WeakTracking, value)
{
    public LlvmValue? Value { get => HandleValue; set => SetValue(value); }
    public bool PointsToAliveValue => HandleValue is not null;
}

public sealed class LlvmTrackingValueHandle<T> : IDisposable where T : LlvmValue
{
    private readonly LlvmWeakTrackingValueHandle handle;
    public LlvmTrackingValueHandle(T? value = null) => handle = new(value);
    public T? Value
    {
        get
        {
            Debug.Assert(handle.PointsToAliveValue && handle.Value is T);
            return (T?)handle.Value;
        }
        set => handle.Value = value;
    }
    public void Dispose() => handle.Dispose();
}

public abstract class LlvmCallbackValueHandle : LlvmValueHandle
{
    protected LlvmCallbackValueHandle(LlvmValue? value = null) : base(HandleKind.Callback, value) { }
    public LlvmValue? Value { get => HandleValue; protected set => SetValue(value); }
    protected virtual void Deleted() => Value = null;
    protected virtual void AllUsesReplacedWith(LlvmValue replacement) { }
    internal void DispatchDeleted() => Deleted();
    internal void DispatchReplaced(LlvmValue replacement) => AllUsesReplacedWith(replacement);
}
