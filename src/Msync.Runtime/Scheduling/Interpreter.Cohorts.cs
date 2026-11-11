using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private sealed record TransferFootprint(RangeValue Source, ResourceKey[] Reads, ResourceKey[] Writes);

    private async ValueTask<Task<bool>> ScheduleCohortAsync(RangeValue source, Value destination,
        SourceSpan? span, bool local, CancellationToken cancellationToken)
    {
        if (source is { Selection: null, Resolve: null, OwnedSnapshot: { } members })
        {
            if (pending.Count == 0)
                return await ScheduleTransferAsync(source, destination, span, local, cancellationToken);

            if (!context.DryRun && destination is PathValue or StringValue
                && await LocalCohortFootprintAsync(members, source.ItemType, context.AsPath(destination)) is { } footprint)
                return await ScheduleTransferAsync(footprint.Source, destination, span, local, cancellationToken, footprint: footprint);
        }

        await DrainAsync();
        return await ScheduleTransferAsync(source, destination, span, local, cancellationToken);
    }

    private async ValueTask<TransferFootprint?> LocalCohortFootprintAsync(IReadOnlyList<Value> members,
        TypeRef itemType, PathValue destination)
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

        // An existing member directory can merge through descendant links
        // outside this footprint. Fresh entries and atomic file publications cannot.
        if (!replace && targets.Any(Directory.Exists)) return null;
        return new(new RangeValue(sources, itemType), reads, writes);
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
