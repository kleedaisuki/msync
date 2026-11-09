using System.Globalization;

namespace Msync.Core;

public static class FunctionDispatch
{
    public static IReadOnlyDictionary<int, int> Parse(string text)
    {
        Dictionary<int, int> targets = [];
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split(':');
            if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var source)
                || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var target) || !targets.TryAdd(source, target))
                throw new MsyncException($"Invalid compiled function dispatch entry '{entry}'");
        }
        return targets;
    }
}
