using Msync.Compiler.Syntax;

namespace Msync.Cli;

internal static class SourceCommands
{
    internal static ValueTask<string> ReadSource(string file, TextReader? input = null) => file == "-"
        ? new((input ?? Console.In).ReadToEndAsync(CancellationToken.None)) : ValueTask.FromResult(File.ReadAllText(file));
    internal static int Format(string file, CommandOptions options)
    {
        string original = ReadSource(file).GetAwaiter().GetResult();
        string formatted = Formatter.Format(original);
        if (options.FormattingCheck)
        {
            if (formatted == original) return 0;
            Console.Error.WriteLine($"{file}: formatting differs"); return 1;
        }
        if (options.Stdout || file == "-") Console.Write(formatted);
        else File.WriteAllText(file, formatted);
        return 0;
    }
}
