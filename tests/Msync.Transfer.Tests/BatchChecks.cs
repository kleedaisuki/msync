using Msync.Core;
using Msync.Runtime;

internal static class BatchChecks
{
    internal static async Task RunAsync()
    {
        string root = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_WORK")
            ?? Path.GetFullPath(Path.Combine(".temp", "transfer-batch", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        RuntimeContext context = new(new ExecutionOptions(WorkingDirectory: root));
        PathValue Local(string name) => new(name, root);
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }
        List<string> names = Enumerable.Range(0, 32).Select(index => $"file-{index:000}").ToList();
        names.AddRange(["two words", "one'quote", "可莉", "long-" + new string('x', 180)]);
        if (!OperatingSystem.IsWindows()) names.AddRange(["left\\right", "line\nbreak", "line\nslash\\ 可莉'%0A%25\n", "literal%0A%25"]);
        for (int index = 0; index < names.Count; index++)
            await File.WriteAllTextAsync(Path.Combine(root, names[index]), $"payload {index} 可莉\n");

        var copies = names.Select(name => new TransferBatchItem(Local(name), Local("local/" + name), PlannedTransferKind.CopyFile)).ToArray();
        var local = await TransferEngine.CopyBatchAsync(copies, context);
        Check(local.All(result => result.Success), "Prepared local file batch succeeds.");
        for (int index = 0; index < names.Count; index++)
            Check(await File.ReadAllTextAsync(Path.Combine(root, "local", names[index])) == $"payload {index} 可莉\n", "Local batch bytes.");
        var failure = await TransferEngine.CopyBatchAsync([
            new(Local("missing"), Local("local/missing"), PlannedTransferKind.CopyFile),
            new(new StringValue("continues"), Local("local/after-failure"), PlannedTransferKind.WriteText)
        ], context);
        Check(!failure[0].Success && failure[0].Error is IOException or UnauthorizedAccessException, "Actual missing source remains an item failure.");
        Check(failure[1].Success && await File.ReadAllTextAsync(Path.Combine(root, "local/after-failure")) == "continues", "Independent later item survives failure.");
        foreach (int size in new[] { 1, 17 })
        {
            string literal = string.Concat(Enumerable.Repeat("可莉🌋", size == 1 ? 2 : 1000));
            RuntimeContext narrow = new(new ExecutionOptions(WorkingDirectory: root, BufferSize: size));
            var encoded = await TransferEngine.CopyBatchAsync([
                new(new StringValue(literal), Local("local/utf8-" + size), PlannedTransferKind.WriteText)
            ], narrow);
            Check(encoded[0].Success && await File.ReadAllTextAsync(Path.Combine(root, "local/utf8-" + size)) == literal,
                "Shared batch encoding retains complete Unicode scalars across narrow buffers.");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "changed-kind"), "before");
        File.Delete(Path.Combine(root, "changed-kind"));
        Directory.CreateDirectory(Path.Combine(root, "changed-kind"));
        var changed = await TransferEngine.CopyBatchAsync([
            new(Local("changed-kind"), Local("local/changed"), PlannedTransferKind.CopyFile)
        ], context);
        Check(!changed[0].Success, "Prepared CopyFile executes as a file operation, not a reclassified directory copy.");
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "local"), ".msync-*").Any(), "Local batch retires only its owned stages.");
        Directory.CreateDirectory(Path.Combine(root, "tree", "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "tree", "nested", "file"), "directory payload");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(root, "tree", "dangling"), "missing");
        var tree = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Local("local-trees/"), PlannedTransferKind.CopyDirectory)
        ], context);
        Check(tree[0].Success && File.Exists(Path.Combine(root, "local-trees", "tree", "nested", "file")), "Prepared directory retains container placement.");
        if (!OperatingSystem.IsWindows()) Check(new FileInfo(Path.Combine(root, "local-trees", "tree", "dangling")).LinkTarget == "missing", "Batch directory retains dangling links.");
        Directory.CreateDirectory(Path.Combine(root, "merged-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "merged-root", "retained"), "old member");
        var merged = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Local("merged-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Merge)
        ], context);
        Check(merged[0].Success && File.Exists(Path.Combine(root, "merged-root", "retained"))
            && File.Exists(Path.Combine(root, "merged-root", "nested", "file"))
            && !Directory.Exists(Path.Combine(root, "merged-root", "tree")), "Explicit Merge uses its final leaf and preserves existing members.");
        var replaced = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Local("merged-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Replace)
        ], context);
        Check(replaced[0].Success && !File.Exists(Path.Combine(root, "merged-root", "retained"))
            && File.Exists(Path.Combine(root, "merged-root", "nested", "file")), "Explicit Replace replaces the final root without a spelling suffix.");
        DateTime directoryTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "tree"), directoryTime);
        var ensured = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Local("expanded-local"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Merge)
        ], context);
        Check(ensured[0].Success && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "expanded-local")).Any(),
            "EnsureDirectory creates only its concrete root without copying children.");
        var child = await TransferEngine.CopyBatchAsync([
            new(Local("tree/nested/file"), Local("expanded-local/file"), PlannedTransferKind.CopyFile)
        ], context);
        var applied = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Local("expanded-local"), PlannedTransferKind.ApplyDirectoryMetadata, TransferDestinationMode.Merge)
        ], context);
        Check(child[0].Success && applied[0].Success && File.Exists(Path.Combine(root, "expanded-local", "file"))
            && !Directory.Exists(Path.Combine(root, "expanded-local", "nested"))
            && Directory.GetLastWriteTimeUtc(Path.Combine(root, "expanded-local")) == directoryTime,
            "Directory metadata changes only attributes after explicitly selected children.");
        Directory.CreateDirectory(Path.Combine(root, "cohort-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "cohort-root", "obsolete"), "previous cohort");
        var cohortRoot = await TransferEngine.CopyBatchAsync([
            new(Value.Null, Local("cohort-root"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Replace)
        ], context);
        Check(cohortRoot[0].Success && Directory.Exists(Path.Combine(root, "cohort-root"))
            && !File.Exists(Path.Combine(root, "cohort-root", "obsolete")), "A nonempty replacement cohort can clear its root without a fictional source directory.");
        if (Environment.GetEnvironmentVariable("MSYNC_TRANSFER_REMOTE") is not { } alias)
        {
            Console.WriteLine($"PASS {checks} local transfer batch checks");
            return;
        }

        ConnectionValue connection = new(ConnectionKind.Ssh, alias);
        Directory.CreateDirectory(Path.Combine(root, "remote"));
        PathValue Remote(string name) => new(Path.Combine(root, "remote", name), Connection: connection);
        await using var session = TransferEngine.OpenSession(connection);
        using var selected = TransferEngine.UseSession(session);
        Check(await TransferEngine.RunCommandAsync(connection, "printf 'metadata-ready'", []) == "metadata-ready", "Selected metadata uses the retained native channel.");
        Check(await session.QueryAsync(":", []) == "", "An empty framed response completes on the current command.");
        try { await session.QueryAsync("printf 'ordinary query failure' >&2; exit 7", []); throw new InvalidOperationException("Query failure must be reported."); }
        catch (IOException error) { Check(error.Message.Contains("ordinary query failure", StringComparison.Ordinal), "A query failure retains its concrete diagnostic."); }
        Check(session.IsActive, "A failed native operation does not retire its healthy transport.");
        string ownedDirectory = await session.QueryAsync("readlink -n /proc/self/fd/9", []);
        var ownedPath = new PathValue(ownedDirectory, Connection: connection);
        Check((await session.ReadDirectoryMetadataAsync(ownedPath, recursive: true)).Count == 0,
            "Even an explicitly selected owned directory is pruned by its retained identity.");
        var nativeOwned = (RangeValue)await NativeRegistry.InvokeAsync("fs.recursive_directory_iterator", [ownedPath], context,
            static (_, _, _) => throw new InvalidOperationException("The native directory probe has no source callback."));
        int ownedMembers = 0;
        await foreach (var _ in nativeOwned.Items) ownedMembers++;
        Check(ownedMembers == 0, "Ordinary leased native directory discovery excludes the same owned identity.");
        Console.WriteLine("Batch metadata complete; uploading independent items.");
        var uploads = names.Select(name => new TransferBatchItem(Local(name), Remote(name), PlannedTransferKind.CopyFile)).ToList();
        uploads.Insert(1, new(Local("missing-upload"), Remote("missing-upload"), PlannedTransferKind.CopyFile));
        uploads.Add(new(Local("tree"), Remote("trees/"), PlannedTransferKind.CopyDirectory));
        Directory.CreateDirectory(Path.Combine(root, "remote", "merged-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "remote", "merged-root", "retained"), "old member");
        uploads.Add(new(Local("tree"), Remote("merged-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Merge));
        Directory.CreateDirectory(Path.Combine(root, "remote", "replace-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "remote", "replace-root", "obsolete"), "old member");
        uploads.Add(new(Local("tree"), Remote("replace-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Replace));
        var uploaded = await TransferEngine.CopyBatchAsync(uploads, context);
        Console.WriteLine("Batch upload responses complete; downloading independent items.");
        Check(uploaded.Count == uploads.Count && !uploaded[1].Success && uploaded.Where((_, index) => index != 1).All(outcome => outcome.Success), "Framed uploads retain per-item failure and continue.");
        Check(File.Exists(Path.Combine(root, "remote", "merged-root", "retained"))
            && File.Exists(Path.Combine(root, "remote", "merged-root", "nested", "file")), "Remote upload Merge preserves root members without another basename.");
        Check(!File.Exists(Path.Combine(root, "remote", "replace-root", "obsolete"))
            && File.Exists(Path.Combine(root, "remote", "replace-root", "nested", "file")), "Remote upload Replace replaces the concrete root.");
        for (int index = 0; index < names.Count; index++)
            Check(await File.ReadAllTextAsync(Path.Combine(root, "remote", names[index])) == $"payload {index} 可莉\n", "Remote batch uploaded bytes and spelling.");
        Directory.CreateDirectory(Path.Combine(root, "remote", ".msync-batch-user"));
        await File.WriteAllTextAsync(Path.Combine(root, "remote", ".msync-batch-user", "visible"), "user-owned");
        var directoryMetadata = await session.ReadDirectoryMetadataAsync(Remote(""));
        Check(directoryMetadata.Count(item => names.Contains(PathSemantics.FileName(item.Path), StringComparer.Ordinal)) == names.Count
            && directoryMetadata.Where(item => names.Contains(PathSemantics.FileName(item.Path), StringComparer.Ordinal))
            .All(item => item.Kind == "regular" && item.CanonicalPath == Path.Combine(root, "remote", PathSemantics.FileName(item.Path))),
            "One directory metadata query supplies kind and physical entry paths without one query per file.");
        var recursiveMetadata = await session.ReadDirectoryMetadataAsync(Remote(""), recursive: true);
        Check(recursiveMetadata.Any(item => item.Path.Raw == Path.Combine(root, "remote", ".msync-batch-user", "visible")),
            "User directories with a runtime-like spelling remain visible.");
        var metadata = await session.ReadMetadataBatchAsync([Remote(names[0]), Remote("not-yet-created")]);
        Check(metadata[0].Kind == "regular" && metadata[1].Kind == "not_found"
            && metadata.All(item => item.Error is null), "Bulk metadata retains missing entries without failing the channel.");
        var namespaceOnly = await session.ReadMetadataBatchAsync([Remote("not-yet-created"), Remote(names[0])], includeKinds: false);
        Check(namespaceOnly.All(item => item.Error is null && item.Kind.Length == 0)
            && namespaceOnly[0].CanonicalPath == Path.Combine(root, "remote", "not-yet-created"),
            "Namespace-only metadata skips unused per-file kind acquisition.");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "remote", "directory-alias"), "trees/tree");
            var aliasMetadata = await session.ReadMetadataBatchAsync([Remote("directory-alias"), Remote("directory-alias/nested/file")]);
            Check(aliasMetadata[0].Kind == "symlink" && aliasMetadata[0].CanonicalPath == Path.Combine(root, "remote", "directory-alias")
                && aliasMetadata[1].CanonicalPath == Path.Combine(root, "remote", "trees", "tree", "nested", "file"),
                "Metadata canonicalizes parents but retains the final symlink entry identity.");
            var followed = await session.ReadDirectoryMetadataAsync(Remote("directory-alias"));
            Check(followed.Any(item => item.Kind == "directory"
                && item.Path.Raw == Path.Combine(root, "remote", "directory-alias", "nested")
                && item.CanonicalPath == Path.Combine(root, "remote", "trees", "tree", "nested")),
                "Directory acquisition follows its selected root link while retaining raw spelling.");
        }
        var remoteEnsure = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Remote("expanded-actions"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Merge),
            new(Value.Null, Remote("cohort-actions"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Replace)
        ], context);
        Check(remoteEnsure.All(item => item.Success)
            && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "remote", "expanded-actions")).Any(),
            "Remote directory actions create roots without enumerating or copying payloads.");
        var remoteChild = await TransferEngine.CopyBatchAsync([
            new(Local("tree/nested/file"), Remote("expanded-actions/file"), PlannedTransferKind.CopyFile)
        ], context);
        var remoteApplied = await TransferEngine.CopyBatchAsync([
            new(Local("tree"), Remote("expanded-actions"), PlannedTransferKind.ApplyDirectoryMetadata, TransferDestinationMode.Merge)
        ], context);
        Check(remoteChild[0].Success && remoteApplied[0].Success
            && !Directory.Exists(Path.Combine(root, "remote", "expanded-actions", "nested"))
            && Directory.GetLastWriteTimeUtc(Path.Combine(root, "remote", "expanded-actions")) == directoryTime,
            "Remote metadata-only completion retains the directory timestamp without recursively recopying it.");
        var downloadEnsure = await TransferEngine.CopyBatchAsync([
            new(Remote("trees/tree"), Local("download-actions"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Merge)
        ], context);
        var downloadChild = await TransferEngine.CopyBatchAsync([
            new(Remote("trees/tree/nested/file"), Local("download-actions/file"), PlannedTransferKind.CopyFile)
        ], context);
        var downloadApplied = await TransferEngine.CopyBatchAsync([
            new(Remote("trees/tree"), Local("download-actions"), PlannedTransferKind.ApplyDirectoryMetadata, TransferDestinationMode.Merge)
        ], context);
        Check(downloadEnsure[0].Success && downloadChild[0].Success && downloadApplied[0].Success
            && Directory.GetLastWriteTimeUtc(Path.Combine(root, "download-actions")) == directoryTime,
            "Remote-source directory metadata uses its selected channel and preserves native time.");

        Directory.CreateDirectory(Path.Combine(root, "downloads", names[1]));
        var downloads = names.Select(name => new TransferBatchItem(Remote(name), Local("downloads/" + name), PlannedTransferKind.CopyFile)).ToList();
        downloads.Insert(2, new(Remote("missing-remote"), Local("downloads/missing-remote"), PlannedTransferKind.CopyFile));
        downloads.Add(new(Remote("trees/tree"), Local("downloads/tree-copy"), PlannedTransferKind.CopyDirectory));
        Directory.CreateDirectory(Path.Combine(root, "downloads", "merge"));
        await File.WriteAllTextAsync(Path.Combine(root, "downloads", "merge", "retained"), "old member");
        downloads.Add(new(Remote("trees/tree"), Local("downloads/merge"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Merge));
        var downloaded = await TransferEngine.CopyBatchAsync(downloads, context);
        Console.WriteLine("Batch download responses complete; copying on the remote endpoint.");
        Check(!downloaded[1].Success && !downloaded[2].Success, "Download keeps both destination and missing-source failures.");
        Check(downloaded.Where((_, index) => index != 1 && index != 2).All(outcome => outcome.Success),
            "Download drains failed members and continues on the same stream: " + string.Join("; ", downloaded
                .Select((outcome, index) => (outcome, index)).Where(item => item.index != 1 && item.index != 2 && !item.outcome.Success)
                .Select(item => $"#{item.index}: {item.outcome.Error}")));
        Check(await File.ReadAllTextAsync(Path.Combine(root, "downloads", "tree-copy", "nested", "file")) == "directory payload", "Directory batch downloads retain their root placement.");
        Check(File.Exists(Path.Combine(root, "downloads", "merge", "retained"))
            && File.Exists(Path.Combine(root, "downloads", "merge", "nested", "file")), "Remote download Merge preserves concrete-root members.");
        if (!OperatingSystem.IsWindows()) Check(new FileInfo(Path.Combine(root, "downloads", "tree-copy", "dangling")).LinkTarget == "missing", "Directory batch download retains dangling links.");
        for (int index = 0; index < names.Count; index++)
            if (index != 1) Check(await File.ReadAllTextAsync(Path.Combine(root, "downloads", names[index])) == $"payload {index} 可莉\n", "Downloaded bytes.");

        var remoteCopies = await TransferEngine.CopyBatchAsync([
            new(Remote(names[0]), Remote("copied/" + names[0]), PlannedTransferKind.CopyFile),
            new(Remote("missing-remote"), Remote("copied/missing"), PlannedTransferKind.CopyFile),
            new(Remote(names[^1]), Remote("copied/" + names[^1]), PlannedTransferKind.CopyFile)
        ], context);
        Check(remoteCopies[0].Success && !remoteCopies[1].Success && remoteCopies[2].Success, "Same-endpoint copies retain individual errors without another authentication.");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "remote", "copied", names[^1])) == $"payload {names.Count - 1} 可莉\n", "Same-endpoint final copy bytes.");
        Check(await session.QueryAsync("printf 'metadata-after-data'", []) == "metadata-after-data", "Metadata still uses the data channel after all batch directions.");
        Console.WriteLine("Batch commands complete; disposing the selected native channel.");
        Console.WriteLine($"PASS {checks} local and single-channel remote batch checks");
    }
}
