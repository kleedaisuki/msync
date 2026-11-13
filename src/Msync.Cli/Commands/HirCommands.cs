using Msync.Hir;
using Msync.Core;

namespace Msync.Cli;

internal static class HirCommands
{
    internal static bool IsArtifact(string path) => path.EndsWith(".mhir", StringComparison.OrdinalIgnoreCase);

    internal static void Write(HirModule module, string output, CompilationTrace? trace = null)
    {
        if (output != "-") { HirArtifact.WriteProduced(module, output, trace); return; }
        using Stream stream = Console.OpenStandardOutput();
        HirArtifact.WriteProduced(module, stream, trace);
    }

    internal static void Print(HirModule module, string color) =>
        Console.Write(TerminalPresentation.Syntax(HirPrinter.Print(module), TerminalPresentation.Colors(color)));
}
