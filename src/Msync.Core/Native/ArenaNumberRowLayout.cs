namespace Msync.Core;

// Runtime SetArenaRow<double> uses four consecutive Int32 fields and one Double.
public static class ArenaNumberRowLayout
{
    public const int Left = 0;
    public const int Right = 4;
    public const int Parent = 8;
    public const int Color = 12;
    public const int Key = 16;
    public const int Stride = 24;
}
