namespace Msync.Compiler;

internal static class StandardSourceCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, StandardSource>> sources = new(Create);

    internal static IReadOnlyDictionary<string, StandardSource> Read() => sources.Value;

    private static IReadOnlyDictionary<string, StandardSource> Create()
    {
        Dictionary<string, StandardSource> result = [];
        var assembly = typeof(Compiler).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".msync", StringComparison.Ordinal)))
        {
            var module = name.Split('.')[^2];
            result[module] = new(name, module);
        }
        return result;
    }
}
