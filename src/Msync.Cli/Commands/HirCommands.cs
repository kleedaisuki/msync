using Msync.Hir;

namespace Msync.Cli;

internal static class HirCommands
{
    internal static bool IsArtifact(string path) => path.EndsWith(".mhir", StringComparison.OrdinalIgnoreCase);

    internal static void Write(HirModule module, string output)
    {
        if (output != "-") { HirArtifact.WriteProduced(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        HirArtifact.WriteProduced(module, stream);
    }

    internal static void Print(HirModule module, string color) =>
        Console.Write(TerminalPresentation.Syntax(HirPrinter.Print(module), TerminalPresentation.Colors(color)));
}
