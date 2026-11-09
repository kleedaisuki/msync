using Msync.Core;
using Msync.Hir;
using Msync.Optimization;

namespace Msync.Cli;

internal static class HirCommands
{
    internal static bool IsArtifact(string path) => path.EndsWith(".mhir", StringComparison.OrdinalIgnoreCase);

    internal static Module Lower(HirModule module, int optimization)
    {
        string[] imports = module.UnresolvedImports.Select(location => module.Locations[location.Value].Name)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (imports.Length != 0)
            throw new MsyncException($"Typed HIR requires session locations: {string.Join(", ", imports)}. Export a self-contained program for standalone compilation or execution; live session values are not stored in .mhir files.");
        if (!module.SessionDispatches.IsDefaultOrEmpty)
            throw new MsyncException("Typed HIR requires live session callable dispatch. Inspect this semantic artifact in read-only mode, or export a self-contained program for standalone compilation or execution; live session values are not stored in .mhir files.");
        return Optimizer.Optimize(HirLowerer.Lower(module, new(optimization)).Module, optimization);
    }

    internal static void Write(HirModule module, string output)
    {
        if (output != "-") { HirArtifact.Write(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        HirArtifact.Write(module, stream);
    }

    internal static void Print(HirModule module, string color) =>
        Console.Write(TerminalPresentation.Syntax(HirPrinter.Print(module), TerminalPresentation.Colors(color)));
}
