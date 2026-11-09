namespace Msync.Core;

public enum NativeEffect : byte { Pure, Read, Write }
[Flags]
public enum NativeBehavior : byte { None = 0, MayFail = 1, Enumerates = 2, Deferred = 4, Invokes = 8, EagerRange = 16, LocalMemory = 32 }
public enum NativeResultRule : byte
{
    Fixed, FirstArgument, RangeOfArguments, RangeItem, Transform, CastTarget,
    RangeConcat, RangeFlatten, RangeZip, RangeEnumerate, RangeNested, RangeScan, RangeRepeat
}
public sealed record NativeSignature(string Name, IReadOnlyList<TypeRef> Parameters, TypeRef Result,
    NativeEffect Effect = NativeEffect.Pure, bool Variadic = false, NativeResultRule ResultRule = NativeResultRule.Fixed,
    NativeBehavior? Behavior = null);
