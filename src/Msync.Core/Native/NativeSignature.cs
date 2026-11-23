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

    internal bool IsStreamReduction => Name is
        "ranges.front" or "ranges.first" or "ranges.back" or "ranges.last" or "ranges.at"
        or "ranges.size" or "ranges.empty" or "ranges.contains" or "ranges.find" or "ranges.find_if"
        or "ranges.any" or "ranges.any_of" or "ranges.all_of" or "ranges.none" or "ranges.none_of";

    internal bool MaySubmitScalar => ReturnsFileValue
        || (Behavior.GetValueOrDefault() & NativeBehavior.DeferredScalar) != 0;

    internal bool LiftsScalarArguments =>
        (Effect == NativeEffect.Pure || (Behavior.GetValueOrDefault() & NativeBehavior.DeferredScalar) != 0)
        && (Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.LocalMemory)) == 0
        && Parameters.All(static parameter => (parameter.Kind & (TypeKind.Range | TypeKind.Function | TypeKind.Scope)) == 0);

    // Deferred callbacks belong to consumption, not construction of the range.
    internal bool RequiresWorkScope => ReturnsFileValue
        || Effect != NativeEffect.Pure && !Behavior.GetValueOrDefault().HasFlag(NativeBehavior.LocalMemory)
        || Behavior.GetValueOrDefault().HasFlag(NativeBehavior.Enumerates)
        || Behavior.GetValueOrDefault().HasFlag(NativeBehavior.Invokes)
            && !Behavior.GetValueOrDefault().HasFlag(NativeBehavior.Deferred);

    internal bool ReturnsFileValue
    {
        get
        {
            if ((Behavior.GetValueOrDefault() & (NativeBehavior.LocalMemory | NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.Deferred)) != 0
                || Effect == NativeEffect.Write || (Result.Kind & ~TypeKind.Null) == TypeKind.Range) return false;
            for (int index = 0; index < Parameters.Count; index++)
                if ((Parameters[index].Kind & TypeKind.Path) != 0
                    && AccessOfPathArgument(index) is NativePathAccess.Read or NativePathAccess.ReadIfPath) return true;
            return false;
        }
    }

    public NativePathAccess AccessOfPathArgument(int argument) => PathAccess is { } access ? access[argument] : Effect switch
    {
        NativeEffect.Read => NativePathAccess.Read,
        NativeEffect.Write => NativePathAccess.Write,
        _ => NativePathAccess.None
    };
}
