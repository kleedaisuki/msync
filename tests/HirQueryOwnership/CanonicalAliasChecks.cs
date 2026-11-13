using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class CanonicalAliasChecks
{
    internal static int Run()
    {
        Type provenance = typeof(SemanticAnalyzer).GetNestedType("KeyProvenance", BindingFlags.NonPublic)!;
        Random random = new(1979);
        int checks = 0;
        for (int trial = 0; trial < 250; trial++)
        {
            int count = random.Next(1, 9);
            var edges = Enumerable.Range(0, count).SelectMany(source => Enumerable.Range(0, count + 4)
                .Where(_ => random.Next(3) == 0).Select(target => (Source: source, Kind: random.Next(2) == 0 ? 'A' : 'V', Target: target))).ToArray();
            int[] ids = Enumerable.Range(1, count + 4).ToArray();
            int[] renamed = ids.OrderBy(_ => random.Next()).Select(identity => identity * 17 + 19).ToArray();
            Check(Encode(count, ids, edges) == Encode(count, renamed, edges.Reverse().ToArray()),
                "An allocation permutation changed the canonical alias relation.");
            if (edges.Length == 0) continue;
            var changed = edges.ToArray();
            changed[0] = changed[0] with { Kind = changed[0].Kind == 'A' ? 'V' : 'A' };
            if (!edges.Skip(1).Contains(changed[0]))
                Check(Encode(count, ids, edges) != Encode(count, ids, changed), "Alias relation kinds were conflated.");
        }
        Check(Encode(2, [1, 2, 10, 11], [(0, 'V', 2), (1, 'V', 2)])
            != Encode(2, [1, 2, 10, 11], [(0, 'V', 2), (1, 'V', 3)]),
            "Shared and distinct external alias targets were conflated.");
        return checks;

        string Encode(int count, int[] identities, (int Source, char Kind, int Target)[] edges)
        {
            object state = Activator.CreateInstance(provenance, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [false, false], null)!;
            MethodInfo region = provenance.GetMethod("Region")!;
            MethodInfo alias = provenance.GetMethod("Alias")!;
            for (int index = 0; index < count; index++) region.Invoke(state, [identities[index]]);
            foreach (var group in edges.GroupBy(edge => (edge.Source, edge.Kind)))
            {
                SemanticScope source = new(identities[group.Key.Source], 0, ImmutableDictionary<string, SemanticValue>.Empty,
                    ImmutableDictionary<string, HirLocationId>.Empty);
                alias.Invoke(state, [source, group.Key.Kind, group.Select(edge => identities[edge.Target])]);
            }
            StringBuilder key = new();
            provenance.GetMethod("AppendAliases")!.Invoke(state, [key]);
            return key.ToString();
        }

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
