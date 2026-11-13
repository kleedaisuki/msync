using System.Globalization;

namespace Msync.Hir;

public static class HirTelemetry
{
    public static IReadOnlyDictionary<string, ulong> Metrics(HirModule module) => new Dictionary<string, ulong>
    {
        ["hir.nodes"] = (ulong)module.Nodes.Length,
        ["hir.edges"] = (ulong)module.Edges.Length,
        ["hir.types"] = (ulong)module.Types.Length,
        ["hir.locations"] = (ulong)module.Locations.Length,
        ["hir.functions"] = (ulong)module.Functions.Length,
        ["hir.templates"] = (ulong)module.Templates.Length,
        ["hir.layouts"] = (ulong)module.ScopeLayouts.Length,
        ["hir.sources"] = (ulong)module.Sources.Length
    };

    public static Dictionary<string, string> Source(IReadOnlyList<HirSource> sources, HirSourceRange source) => new()
    {
        ["source.file"] = sources[source.Source.Value].Name,
        ["source.offset"] = source.Start.ToString(CultureInfo.InvariantCulture),
        ["source.length"] = source.Length.ToString(CultureInfo.InvariantCulture)
    };
}
