using Msync.Core;
using Msync.Hir;
using Msync.Optimization;

namespace Msync.Cli;

internal static class HirCommands
{
    internal static bool IsArtifact(string path) => path.EndsWith(".mhir", StringComparison.OrdinalIgnoreCase);

    internal static Module Lower(HirModule module, int optimization) =>
        Optimizer.OptimizeInstructions(HirLowerer.Lower(module, new(optimization, RetainLayout: false)).Module,
            optimization);

    internal static void Write(HirModule module, string output)
    {
        if (output != "-") { HirArtifact.WriteProduced(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        HirArtifact.WriteProduced(module, stream);
    }

    internal static void Print(HirModule module, string color) =>
        Console.Write(TerminalPresentation.Syntax(HirPrinter.Print(module), TerminalPresentation.Colors(color)));
}
