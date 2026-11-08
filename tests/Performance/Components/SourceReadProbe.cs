using System.Security.Cryptography;
using System.Text;

namespace Msync.Components;

internal static class SourceReadProbe
{
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;
        switch (args[0])
        {
            case "--read-source-sync": Console.WriteLine(ReadSynchronously(FileArgument(args)).Length); break;
            case "--read-source-async": Console.WriteLine(ReadAsynchronously(FileArgument(args)).Length); break;
            case "--read-source-check": Check(FileArgument(args)); break;
            default: return false;
        }
        return true;
    }

    private static string FileArgument(string[] args) => args.Length == 2 ? args[1]
        : throw new ArgumentException("Source read modes require exactly one filename.");

    private static string ReadSynchronously(string path) => File.ReadAllText(path);

    private static string ReadAsynchronously(string path) => File.ReadAllTextAsync(path).GetAwaiter().GetResult();

    private static void Check(string path)
    {
        string synchronous = ReadSynchronously(path);
        string asynchronous = ReadAsynchronously(path);
        if (!string.Equals(synchronous, asynchronous, StringComparison.Ordinal))
            throw new InvalidOperationException("Synchronous and asynchronous reads decoded different source text.");
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(synchronous)));
        Console.WriteLine($"{synchronous.Length} {digest}");
    }
}
