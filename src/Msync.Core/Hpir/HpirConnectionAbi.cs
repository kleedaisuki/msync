namespace Msync.Core.Hpir;

// An endpoint kind travels with its immutable, aligned address Text pointer.
// The artifact carries the same kind alongside a Text literal ordinal.
public static class HpirConnectionAbi
{
    public const int TagBits = 3;
    public const int Alignment = 1 << TagBits;
    public const int Mask = Alignment - 1;

    public static long Literal(ConnectionKind kind, int text) => ((long)text << TagBits) | (byte)kind;

    public static ConnectionKind Kind(long word) => (ConnectionKind)(word & Mask);
    public static int Text(long literal) => (int)(literal >> TagBits);

    internal static bool ValidLiteral(long literal, int texts) =>
        (ulong)literal >> TagBits < (ulong)texts && Kind(literal) <= ConnectionKind.SearchPath;
}
