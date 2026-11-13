namespace Msync.Core;

public enum NativeEffect : byte { Pure, Read, Write }
public enum NativePathAccess : byte { None, Read, Write, ReadIfPath }
public enum NumericRepresentation : byte { Binary64, Word32 }
public enum NumericIntrinsic : byte
{
    None, Floor, Power,
    And, Or, Xor, Not, ShiftLeft, ShiftRight, ShiftRightSigned, RotateLeft, RotateRight,
    PopCount, LeadingZeroCount, TrailingZeroCount, ReverseBytes
}
public sealed record NativeElementCallback(int FunctionArgument, int RangeArgument)
{
    public int Arity { get; init; } = 1;
}
[Flags]
public enum NativeBehavior : byte { None = 0, MayFail = 1, Enumerates = 2, Deferred = 4, Invokes = 8, EagerRange = 16, LocalMemory = 32, PreservesIdentity = 64, DeferredScalar = 128 }
public enum NativeResultRule : byte
{
    Fixed, FirstArgument, RangeOfArguments, RangeItem, Transform, CastTarget,
    RangeConcat, RangeFlatten, RangeZip, RangeEnumerate, RangeNested, RangeScan, RangeRepeat,
    RangeOfType, RangeMerge, RangeMergeLeft
}
public sealed record NativeSignature(string Name, IReadOnlyList<TypeRef> Parameters, TypeRef Result,
    NativeEffect Effect = NativeEffect.Pure, bool Variadic = false, NativeResultRule ResultRule = NativeResultRule.Fixed,
    NativeBehavior? Behavior = null)
{
    public IReadOnlyList<NativePathAccess>? PathAccess { get; init; }
    public NativeElementCallback? ElementCallback { get; init; }
    public NumericIntrinsic Numeric { get; init; }

    public NativePathAccess AccessOfPathArgument(int argument) => PathAccess is { } access ? access[argument] : Effect switch
    {
        NativeEffect.Read => NativePathAccess.Read,
        NativeEffect.Write => NativePathAccess.Write,
        _ => NativePathAccess.None
    };
}
