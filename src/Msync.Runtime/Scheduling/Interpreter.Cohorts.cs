using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private sealed record TransferFootprint(RangeValue Source, ResourceKey[] Reads, ResourceKey[] Writes);

    private async ValueTask<Task<bool>> ScheduleCohortAsync(RangeValue source, Value destination,
        SourceSpan? span, bool local, CancellationToken cancellationToken)
    {
        if (pending.Count == 0 && source is { Selection: null, Resolve: null, OwnedSnapshot: not null })
            return await ScheduleTransferAsync(source, destination, span, local, cancellationToken);
        if (!context.DryRun && destination is PathValue or StringValue
            && context.AsPath(destination).Connection is null or { Kind: ConnectionKind.Local })
        {
            await ordering;
            while (source.Resolve is { } resolve) source = await resolve(cancellationToken);
            // Remote selectors retain metadata and payload on their selected transport channel.
            if (source.Selection?.Directory.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl))
            {
                if (source.OwnedSnapshot is null)
                {
                    List<Value> selected = [];
                    await foreach (Value member in source.Items.WithCancellation(cancellationToken))
                        selected.Add(member);
                    source = RangeValue.TakeSnapshotOwnership(selected, source.ItemType);
                }
                if (pending.Count == 0)
                    return await ScheduleTransferAsync(source, destination, span, local, cancellationToken);
                if (await LocalCohortFootprintAsync(source.OwnedSnapshot!, source.ItemType,
                    context.AsPath(destination), cancellationToken) is { } footprint)
                    return await ScheduleTransferAsync(footprint.Source, destination, span, local, cancellationToken, footprint: footprint);
            }
        }

        await DrainAsync();
        return await ScheduleTransferAsync(source, destination, span, local, cancellationToken);
    }

    private async ValueTask<TransferFootprint?> LocalCohortFootprintAsync(IReadOnlyList<Value> members,
        TypeRef itemType, PathValue destination, CancellationToken cancellationToken)
    {
        if (TransferEngine.LocalCohortTarget(destination, context) is not { } placement) return null;
        var (target, replace) = placement;
        PathValue[] sources = new PathValue[members.Count];
        string[] targets = new string[members.Count];
        for (int index = 0; index < members.Count; index++)
        {
            if (members[index] is not PathValue { Connection: null or { Kind: ConnectionKind.Local } } member)
                return null;
            sources[index] = member;
            targets[index] = Path.Combine(target, PathSemantics.FileName(member));
        }

        await AwaitNamespacePublicationsAsync(replace ? [new PathValue(target), .. sources]
            : [destination, .. sources, .. targets.Select(path => new PathValue(path))]);
        var reads = sources.Select(Key).ToArray();
        var writes = CohortWrites(target, targets, replace);
        var publications = resources.Dependencies(writes, []);
        if (namespaceProducers.Count != 0)
            publications.UnionWith(resources.Dependencies(reads, writes).Where(namespaceProducers.ContainsKey));
        if (publications.Count != 0)
        {
            await Task.WhenAll(publications);
            reads = sources.Select(Key).ToArray();
            writes = CohortWrites(target, targets, replace);
        }

        if (!replace)
            writes = await CohortMergeWritesAsync(sources, targets, writes, cancellationToken);
        return new(new RangeValue(sources, itemType), reads, writes);
    }

    private async ValueTask<ResourceKey[]> CohortMergeWritesAsync(PathValue[] sources, string[] targets,
        ResourceKey[] roots, CancellationToken cancellationToken)
    {
        List<PathValue> directories = [];
        for (int index = 0; index < sources.Length; index++)
        {
            if (!Directory.Exists(targets[index])) continue;
            string source = sources[index].LocalPath(context.WorkingDirectory, context.HomeDirectory);
            if (!Directory.Exists(source)) continue;
            foreach (string relative in CohortDirectories(source, cancellationToken))
                directories.Add(new PathValue(Path.Combine(targets[index], relative)));
        }
        if (directories.Count == 0) return roots;
        await AwaitNamespacePublicationsAsync(directories);
        ResourceKey[] writes = MergeAliases(roots, targets, directories);
        var aliases = resources.Dependencies([], writes).Where(namespaceProducers.ContainsKey).ToArray();
        if (aliases.Length == 0) return writes;
        await Task.WhenAll(aliases);
        return MergeAliases(roots, targets, directories);
    }

    private ResourceKey[] MergeAliases(ResourceKey[] roots, string[] members, List<PathValue> directories)
    {
        List<ResourceKey> writes = [.. roots];
        HashSet<ResourceKey> aliases = [];
        Dictionary<string, ResourceKey> containingRoots = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string member in members) containingRoots[member] = Key(new PathValue(member));
        foreach (PathValue directory in directories)
        {
            string name = directory.Raw;
            ResourceKey containing = containingRoots[Path.GetDirectoryName(name)!];
            ResourceKey physical = Key(directory);
            if (physical == containing || DescendsFrom(physical, containing))
                containingRoots[name] = containing;
            else
            {
                containingRoots[name] = physical;
                if (aliases.Add(physical)) writes.Add(physical);
            }
        }
        return [.. writes];
    }

    private static IEnumerable<string> CohortDirectories(string root, CancellationToken cancellationToken)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeFileMetadata.IsLink(current, File.GetAttributes(current))) continue;
            if (current != root) yield return Path.GetRelativePath(root, current);
            foreach (string directory in Directory.EnumerateDirectories(current)) pending.Push(directory);
        }
    }

    private static ResourceKey[] CohortWrites(string target, string[] members, bool replace)
    {
        string root = ResourcePath.Resolve(target);
        if (replace)
        {
            string? parent = Path.GetDirectoryName(target);
            string entry = parent is null ? root : Path.Combine(ResourcePath.Resolve(parent), Path.GetFileName(target));
            return entry == root ? [new("local", root)] : [new("local", entry), new("local", root)];
        }
        List<ResourceKey> writes = new(members.Length);
        foreach (string member in members)
        {
            string entry = Path.Combine(root, Path.GetFileName(member));
            writes.Add(new("local", entry));
            string referent = ResourcePath.Resolve(member);
            if (referent != entry) writes.Add(new("local", referent));
        }
        return [.. writes];
    }
}
