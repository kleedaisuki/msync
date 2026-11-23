namespace Msync.Core.Hpir;

public static class HpirOrdinal
{
    public const ulong Invalid = ulong.MaxValue;

    // Range ordinals occupy [0, 2^63). A single unsigned bounds check
    // rejects this sentinel before any address calculation.
    public static ulong FromNumber(double number) =>
        number >= 0 && number < 9223372036854775808d && number == Math.Truncate(number) ? (ulong)number : Invalid;
}
