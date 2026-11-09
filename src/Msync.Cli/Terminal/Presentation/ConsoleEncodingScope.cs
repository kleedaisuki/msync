using System.Text;

namespace Msync.Cli;

internal sealed class ConsoleEncodingScope(Encoding original) : IDisposable
{
    public static ConsoleEncodingScope? Open()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected && Console.IsErrorRedirected) return null;
        Encoding original = Console.OutputEncoding;
        if (original.CodePage == 65001) return null;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new(original);
    }

    public void Dispose() => Console.OutputEncoding = original;
}
