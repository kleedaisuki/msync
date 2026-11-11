using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Msync.Runtime;

internal sealed class NamespaceProducerIndex
{
    private Dictionary<Task, IReadOnlyList<ResourceKey>>? roots;
    private Dictionary<string, ResourceIndex.TaskSet>? entries;

    internal bool HasProducers => roots is { Count: > 0 };
    internal bool HasLocalEntries => entries is { Count: > 0 };
    internal bool Contains(Task task) => roots?.ContainsKey(task) ?? false;

    internal bool TryGetRoots(Task task, [NotNullWhen(true)] out IReadOnlyList<ResourceKey>? result)
    {
        result = null;
        return roots?.TryGetValue(task, out result) ?? false;
    }

    internal void Add(Task task, IReadOnlyList<ResourceKey> produced)
    {
        (roots ??= []).Add(task, produced);
        foreach (var root in produced)
            if (IsLocalEntry(root))
            {
                entries ??= new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                CollectionsMarshal.GetValueRefOrAddDefault(entries, root.Path, out _).Add(task);
            }
    }

    internal void Remove(Task task)
    {
        if (roots is null || !roots.Remove(task, out var produced) || entries is null) return;
        foreach (var root in produced)
        {
            if (!IsLocalEntry(root)) continue;
            ref var tasks = ref CollectionsMarshal.GetValueRefOrNullRef(entries, root.Path);
            // A batch can publish the same entry more than once.
            if (Unsafe.IsNullRef(ref tasks)) continue;
            tasks.Remove(task);
            if (tasks.IsEmpty) entries.Remove(root.Path);
        }
    }

    internal void CollectPending(string entry, ref HashSet<Task>? result)
    {
        if (entries is not null && entries.TryGetValue(entry, out var tasks)) tasks.CollectPending(ref result);
    }

    private static bool IsLocalEntry(ResourceKey root) => root.Endpoint == "local" && root.Path.Length != 0;
}
