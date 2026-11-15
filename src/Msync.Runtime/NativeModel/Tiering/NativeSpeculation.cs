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

internal sealed record NativeSiteShape(NativeKind First, NativeKind Second);

internal enum NativeFastOperation : byte
{
    Bridge, Nop, Jump, Branch, NumberReturn, BoolReturn, NumberConstant, BoolConstant,
    NumberCopy, BoolCopy, NumberMove, BoolMove, IsNull, NumberNegate, BoolNot,
    BoolEqual, BoolNotEqual,
    NumberAdd, NumberSubtract, NumberMultiply, NumberDivide, NumberRemainder,
    NumberEqual, NumberNotEqual, NumberLess, NumberLessEqual, NumberGreater, NumberGreaterEqual
}

internal static class NativeFastOperations
{
    internal static bool IsBoolResult(this NativeFastOperation op) =>
        op is NativeFastOperation.BoolConstant or NativeFastOperation.BoolCopy or NativeFastOperation.BoolMove
            or NativeFastOperation.IsNull or NativeFastOperation.BoolNot
            or NativeFastOperation.BoolEqual or NativeFastOperation.BoolNotEqual or >= NativeFastOperation.NumberEqual;

    internal static bool TakesSecondOperand(this NativeFastOperation op) =>
        op >= NativeFastOperation.BoolEqual;

    internal static bool ExpectsBoolInputs(this NativeFastOperation op) =>
        op is NativeFastOperation.BoolCopy or NativeFastOperation.BoolMove or NativeFastOperation.BoolNot
            or NativeFastOperation.BoolEqual or NativeFastOperation.BoolNotEqual;
}
