using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeSiteProfile
{
    private readonly object gate = new();
    private NativeSiteShape? shape;
    private bool polymorphic;
    internal void Observe(NativeSiteShape value)
    {
        lock (gate)
        {
            if (polymorphic) return;
            if (shape is null) shape = value;
            else if (shape != value) { shape = null; polymorphic = true; }
        }
    }
    internal NativeSiteShape? Snapshot() { lock (gate) return shape; }
}

internal sealed record NativeSiteShape(NativeKind First, NativeKind Second = NativeKind.Null,
    int Function = -1, string? NativeName = null, int Layout = -1);

internal enum NativeFastOperation : byte
{
    Bridge, NumberConstant, BoolConstant, NumberCopy, BoolCopy, NumberNegate, BoolNot,
    NumberAdd, NumberSubtract, NumberMultiply, NumberDivide,
    NumberEqual, NumberNotEqual, NumberLess, NumberLessEqual, NumberGreater, NumberGreaterEqual
}
