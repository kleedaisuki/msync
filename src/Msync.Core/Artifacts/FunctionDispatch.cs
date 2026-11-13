using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Msync.Core;

public static class FunctionDispatch
{
    // The format-5 string is the wire view. Share the typed operand for
    // its lifetime rather than parsing it in each analysis and consumer.
    private static readonly ConditionalWeakTable<string, Operand> operands = new();

    public static Operand Targets(string wire) =>
        operands.GetValue(wire, static wire => new(Read(wire)));

    public static string Encode(IEnumerable<KeyValuePair<int, int>> targets)
    {
        var table = targets.ToDictionary();
        string wire = string.Join(';', table.Select(pair => FormattableString.Invariant($"{pair.Key}:{pair.Value}")));
        if (table.Count != 0) operands.Add(wire, new(table));
        return wire;
    }

    public static IReadOnlyDictionary<int, int> Parse(string text) => Read(text);

    public sealed class Operand : IReadOnlyDictionary<int, int>
    {
        private readonly Dictionary<int, int> targets;

        internal Operand(Dictionary<int, int> targets) => this.targets = targets;
        public int Count => targets.Count;
        public IEnumerable<int> Keys => targets.Keys;
        public IEnumerable<int> Values => targets.Values;
        public int this[int key] => targets[key];
        public bool ContainsKey(int key) => targets.ContainsKey(key);
        public bool TryGetValue(int key, out int target) => targets.TryGetValue(key, out target);
        public Dictionary<int, int>.Enumerator GetEnumerator() => targets.GetEnumerator();
        IEnumerator<KeyValuePair<int, int>> IEnumerable<KeyValuePair<int, int>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static Dictionary<int, int> Read(string text)
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
