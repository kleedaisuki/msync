using Msync.Core;

namespace Msync.Runtime;

public static partial class ContextualBackend
{
    private sealed partial class Preparation
    {
        private sealed record DirectoryMember(PathValue Source, PathValue Destination, string Kind, string Relative);

        private static int CompareDirectoryMembers(DirectoryMember left, DirectoryMember right)
        {
            ReadOnlySpan<char> first = left.Relative, second = right.Relative;
            while (true)
            {
                int a = first.IndexOf('/'), b = second.IndexOf('/');
                var x = a < 0 ? first : first[..a];
                var y = b < 0 ? second : second[..b];
                int order = x.CompareTo(y, StringComparison.Ordinal);
                if (order != 0) return order;
                if (a < 0 || b < 0) return a.CompareTo(b);
                first = first[(a + 1)..]; second = second[(b + 1)..];
            }
        }

        private bool AddDirectory(PathValue source, PathValue destination, TransferDestinationMode mode,
            SourceSpan? span, bool observed, int instruction, TransferGraph outputGraph)
        {
            List<TransferGraph.Node> nodes = [];
            try
            {
                if (!PrepareDirectory(source, destination, mode, span, observed, instruction, nodes)) return false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return false; }
            foreach (var node in nodes) outputGraph.Add(node);
            return true;
        }

        private bool AddDirectoryCohortReplacement(RangeValue snapshot, PathValue root,
            SourceSpan? span, bool observed, int instruction, TransferGraph outputGraph)
        {
            if (snapshot.SnapshotValues is not { } values || values.Any(value => value is not PathValue)) return false;
            if (values.Count == 0) return true;
            List<TransferGraph.Node> nodes = [];
            try
            {
                PlanResource replaced = filesystem.Resource(root, true);
                foreach (PathValue source in values)
                {
                    PlanResource input = filesystem.Resource(source, true, true);
                    if (RelatedDirectoryResources(input, replaced)) return false;
                }
                nodes.Add(new(Value.Null, root, PlannedTransferKind.EnsureDirectory,
                    filesystem.NamespaceResources(root), [replaced], span, true, observed, instruction,
                    TransferDestinationMode.Replace));
                filesystem.Prefetch(values.Cast<PathValue>(), includeKinds: true);
                foreach (PathValue source in values)
                {
                    string name = PathSemantics.FileName(source);
                    PathValue destination = PathSemantics.JoinValue(root, new StringValue(name));
                    string kind = filesystem.Kind(source);
                    if (kind == "directory")
                    {
                        if (!PrepareDirectory(source, destination, TransferDestinationMode.Merge, span,
                            observed, instruction, nodes, replaced, name)) return false;
                    }
                    else AddDirectoryLeaf(source, destination, kind, span, observed, instruction, nodes,
                        replaced, name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return false; }
            foreach (var node in nodes) outputGraph.Add(node);
            return true;
        }

        private bool PrepareDirectory(PathValue source, PathValue destination, TransferDestinationMode mode,
            SourceSpan? span, bool observed, int instruction, List<TransferGraph.Node> output,
            PlanResource? replacedRoot = null, string rootRelative = "")
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanResource input = filesystem.Resource(source, true, true);
            PlanResource target = replacedRoot is null ? filesystem.Resource(destination, true,
                mode != TransferDestinationMode.Replace) : DirectoryResource(replacedRoot, rootRelative, true);
            if (RelatedDirectoryResources(input, target)) return false;
            if (replacedRoot is null && mode != TransferDestinationMode.Replace)
            {
                string existing = filesystem.Kind(destination);
                if (existing == "directory" && mode == TransferDestinationMode.Exact) return false;
                if (existing is not ("directory" or "symlink" or "not_found")) return false;
            }

            var entries = PreparedTree(source);
            var members = entries.Select(entry => new DirectoryMember(entry.Path,
                JoinDirectoryMember(destination, DirectoryRelative(source, entry.Path)), entry.Kind,
                CombineRelative(rootRelative, DirectoryRelative(source, entry.Path)))).ToArray();
            // Keep recursive copy order: the entire "a" subtree precedes "a.txt".
            // Existing destination aliases can make those writes overlap.
            Array.Sort(members, CompareDirectoryMembers);
            // Clearing the root removes its old nested links. Descendant effects
            // belong to the newly constructed namespace, not those old targets.
            if (replacedRoot is null && mode == TransferDestinationMode.Replace) replacedRoot = target;
            if (replacedRoot is null)
            {
                filesystem.Prefetch(members.Select(member => member.Destination), includeKinds: false);
                // A nested destination link can turn disjoint roots into a
                // self-modifying copy; retain the composite kernel's read timing.
                foreach (var member in members.Where(member => member.Kind == "directory"))
                    if (RelatedDirectoryResources(input, filesystem.Resource(member.Destination, true, true)))
                        return false;
            }

            var rootDirectory = new DirectoryMember(source, destination, "directory", rootRelative);
            Stack<DirectoryMember> directories = new();
            directories.Push(rootDirectory);
            AddDirectoryOperation(rootDirectory, PlannedTransferKind.EnsureDirectory, mode,
                span, observed, instruction, output, replacedRoot);
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (!RelativeContains(directories.Peek().Relative, member.Relative))
                    AddDirectoryOperation(directories.Pop(), PlannedTransferKind.ApplyDirectoryMetadata,
                        TransferDestinationMode.Merge, span, observed, instruction, output, replacedRoot);
                if (member.Kind == "directory")
                {
                    directories.Push(member);
                    AddDirectoryOperation(member, PlannedTransferKind.EnsureDirectory,
                        TransferDestinationMode.Merge, span, observed, instruction, output, replacedRoot);
                }
                else AddDirectoryLeaf(member.Source, member.Destination, member.Kind, span, observed,
                    instruction, output, replacedRoot, member.Relative);
            }
            while (directories.TryPop(out var directory))
                AddDirectoryOperation(directory, PlannedTransferKind.ApplyDirectoryMetadata,
                    TransferDestinationMode.Merge, span, observed, instruction, output, replacedRoot);
            return true;
        }

        private void AddDirectoryOperation(DirectoryMember member, PlannedTransferKind operation,
            TransferDestinationMode mode, SourceSpan? span, bool observed, int instruction,
            List<TransferGraph.Node> output, PlanResource? replacedRoot)
        {
            bool replacing = operation == PlannedTransferKind.EnsureDirectory
                && mode == TransferDestinationMode.Replace;
            PlanResource target = replacedRoot is null
                ? filesystem.Resource(member.Destination, replacing, !replacing)
                : DirectoryResource(replacedRoot, member.Relative, replacing);
            var reads = new HashSet<PlanResource>(DirectorySourceReads(member.Source), ResourceIdentity.Instance);
            if (replacedRoot is null)
                reads.UnionWith(filesystem.NamespaceResources(member.Destination, !replacing));
            output.Add(new(member.Source, member.Destination, operation, reads.ToArray(), [target], span,
                replacing, observed, instruction, mode));
        }

        private void AddDirectoryLeaf(PathValue source, PathValue destination, string kind, SourceSpan? span,
            bool observed, int instruction, List<TransferGraph.Node> output,
            PlanResource? replacedRoot = null, string relative = "")
        {
            PlannedTransferKind operation = kind switch
            {
                "regular" or "not_found" => PlannedTransferKind.CopyFile,
                "symlink" => PlannedTransferKind.CopyLink,
                _ => PlannedTransferKind.GenericCopy
            };
            bool subtree = operation == PlannedTransferKind.GenericCopy;
            var reads = new HashSet<PlanResource>(DirectorySourceReads(source, subtree), ResourceIdentity.Instance);
            if (replacedRoot is null) reads.UnionWith(filesystem.NamespaceResources(destination));
            PlanResource target = replacedRoot is null ? filesystem.Resource(destination, subtree)
                : DirectoryResource(replacedRoot, relative, subtree);
            output.Add(new(source, destination, operation, reads.ToArray(), [target], span,
                !subtree, observed, instruction, TransferDestinationMode.Exact));
        }

        private IReadOnlyList<PlanResource> DirectorySourceReads(PathValue path, bool subtree = false)
        {
            var reads = new HashSet<PlanResource>(filesystem.NamespaceResources(path), ResourceIdentity.Instance)
            { filesystem.Resource(path, subtree) };
            return reads.ToArray();
        }

        private string DirectoryRelative(PathValue root, PathValue child)
        {
            if (root.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
                return child.Raw[(root.Raw.TrimEnd('/').Length + 1)..];
            string relative = Path.GetRelativePath(root.LocalPath(context.WorkingDirectory, context.HomeDirectory),
                child.LocalPath(context.WorkingDirectory, context.HomeDirectory));
            return OperatingSystem.IsWindows() ? relative.Replace('\\', '/') : relative;
        }

        private static PathValue JoinDirectoryMember(PathValue root, string relative) =>
            relative.Length == 0 ? root : PathSemantics.JoinValue(root, new StringValue(relative));

        private static string CombineRelative(string parent, string child) =>
            parent.Length == 0 ? child : parent + "/" + child;

        private static bool RelativeContains(string parent, string child) =>
            parent.Length == 0 || child.StartsWith(parent + "/", StringComparison.Ordinal);

        private static PlanResource DirectoryResource(PlanResource root, string relative, bool subtree)
        {
            if (relative.Length == 0) return root with { Subtree = subtree };
            bool windows = root.Endpoint == "local" && OperatingSystem.IsWindows();
            string suffix = windows ? relative.Replace('/', '\\') : relative;
            string prefix = windows ? root.Path.TrimEnd('/', '\\') : root.Path.TrimEnd('/');
            return new(root.Endpoint, prefix + (windows ? "\\" : "/") + suffix, subtree);
        }

        private static bool RelatedDirectoryResources(PlanResource source, PlanResource target) =>
            source.Endpoint == target.Endpoint
                && (TransferGraph.Contains(source, target) || TransferGraph.Contains(target, source));
    }
}
