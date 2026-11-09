namespace Msync.Runtime;

internal static class ShellProgram
{
    internal static string Source(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal);
}
