using Msync.Core;
using Msync.Runtime;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

internal static class BatchChecks
{
    internal static async Task RunAsync()
    {
        string root = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_WORK")
            ?? Path.GetFullPath(Path.Combine(".temp", "transfer-batch", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        NativeRuntimeContext context = new(new ExecutionOptions(WorkingDirectory: root));
        NativePathValue Local(string name) => new(name, root);
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

        var copies = names.Select(name => new NativeTransferItem(Local(name), Local("local/" + name), PlannedTransferKind.CopyFile)).ToArray();
        var local = await NativeTransfers.CopyBatchAsync(copies, context);
        Check(local.All(result => result.Success), "Prepared local file batch succeeds.");
        for (int index = 0; index < names.Count; index++)
            Check(await File.ReadAllTextAsync(Path.Combine(root, "local", names[index])) == $"payload {index} 可莉\n", "Local batch bytes.");
        var failure = await NativeTransfers.CopyBatchAsync([
            new(Local("missing"), Local("local/missing"), PlannedTransferKind.CopyFile),
            new(null, Local("local/after-failure"), PlannedTransferKind.WriteText, Text: "continues")
        ], context);
        Check(!failure[0].Success && failure[0].Error is IOException or UnauthorizedAccessException, "Actual missing source remains an item failure.");
        Check(failure[1].Success && await File.ReadAllTextAsync(Path.Combine(root, "local/after-failure")) == "continues", "Independent later item survives failure.");
        foreach (int size in new[] { 1, 17 })
        {
            string literal = string.Concat(Enumerable.Repeat("可莉🌋", size == 1 ? 2 : 1000));
            NativeRuntimeContext narrow = new(new ExecutionOptions(WorkingDirectory: root, BufferSize: size));
            var encoded = await NativeTransfers.CopyBatchAsync([
                new(null, Local("local/utf8-" + size), PlannedTransferKind.WriteText, Text: literal)
            ], narrow);
            Check(encoded[0].Success && await File.ReadAllTextAsync(Path.Combine(root, "local/utf8-" + size)) == literal,
                "Shared batch encoding retains complete Unicode scalars across narrow buffers.");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "changed-kind"), "before");
        File.Delete(Path.Combine(root, "changed-kind"));
        Directory.CreateDirectory(Path.Combine(root, "changed-kind"));
        var changed = await NativeTransfers.CopyBatchAsync([
            new(Local("changed-kind"), Local("local/changed"), PlannedTransferKind.CopyFile)
        ], context);
        Check(!changed[0].Success, "Prepared CopyFile executes as a file operation, not a reclassified directory copy.");
        Check(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "local"), ".msync-*").Any(), "Local batch retires only its owned stages.");
        Directory.CreateDirectory(Path.Combine(root, "tree", "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "tree", "nested", "file"), "directory payload");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(root, "tree", "dangling"), "missing");
        var tree = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Local("local-trees/"), PlannedTransferKind.CopyDirectory)
        ], context);
        Check(tree[0].Success && File.Exists(Path.Combine(root, "local-trees", "tree", "nested", "file")), "Prepared directory retains container placement.");
        if (!OperatingSystem.IsWindows()) Check(new FileInfo(Path.Combine(root, "local-trees", "tree", "dangling")).LinkTarget == "missing", "Batch directory retains dangling links.");
        Directory.CreateDirectory(Path.Combine(root, "merged-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "merged-root", "retained"), "old member");
        var merged = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Local("merged-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Merge)
        ], context);
        Check(merged[0].Success && File.Exists(Path.Combine(root, "merged-root", "retained"))
            && File.Exists(Path.Combine(root, "merged-root", "nested", "file"))
            && !Directory.Exists(Path.Combine(root, "merged-root", "tree")), "Explicit Merge uses its final leaf and preserves existing members.");
        var replaced = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Local("merged-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Replace)
        ], context);
        Check(replaced[0].Success && !File.Exists(Path.Combine(root, "merged-root", "retained"))
            && File.Exists(Path.Combine(root, "merged-root", "nested", "file")), "Explicit Replace replaces the final root without a spelling suffix.");
        DateTime directoryTime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "tree"), directoryTime);
        var ensured = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Local("expanded-local"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Merge)
        ], context);
        Check(ensured[0].Success && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "expanded-local")).Any(),
            "EnsureDirectory creates only its concrete root without copying children.");
        var child = await NativeTransfers.CopyBatchAsync([
            new(Local("tree/nested/file"), Local("expanded-local/file"), PlannedTransferKind.CopyFile)
        ], context);
        var applied = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Local("expanded-local"), PlannedTransferKind.ApplyDirectoryMetadata, TransferDestinationMode.Merge)
        ], context);
        Check(child[0].Success && applied[0].Success && File.Exists(Path.Combine(root, "expanded-local", "file"))
            && !Directory.Exists(Path.Combine(root, "expanded-local", "nested"))
            && Directory.GetLastWriteTimeUtc(Path.Combine(root, "expanded-local")) == directoryTime,
            "Directory metadata changes only attributes after explicitly selected children.");
        Directory.CreateDirectory(Path.Combine(root, "cohort-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "cohort-root", "obsolete"), "previous cohort");
        var cohortRoot = await NativeTransfers.CopyBatchAsync([
            new(null, Local("cohort-root"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Replace)
        ], context);
        Check(cohortRoot[0].Success && Directory.Exists(Path.Combine(root, "cohort-root"))
            && !File.Exists(Path.Combine(root, "cohort-root", "obsolete")), "A nonempty replacement cohort can clear its root without a fictional source directory.");
        if (Environment.GetEnvironmentVariable("MSYNC_TRANSFER_REMOTE") is not { } alias)
        {
            Console.WriteLine($"PASS {checks} local transfer batch checks");
            return;
        }

        NativeConnection connection = new(ConnectionKind.Ssh, alias);
        Directory.CreateDirectory(Path.Combine(root, "remote"));
        NativePathValue Remote(string name) => new(Path.Combine(root, "remote", name), Connection: connection);
        Check(await NativeHostIO.RunCommandAsync(connection, "printf 'metadata-ready'", [], context) == "metadata-ready", "Native metadata commands retain their output.");
        Check(await NativeHostIO.RunCommandAsync(connection, ":", [], context) == "", "An empty native response completes.");
        try { await NativeHostIO.RunCommandAsync(connection, "printf 'ordinary query failure' >&2; exit 7", [], context); throw new InvalidOperationException("Query failure must be reported."); }
        catch (IOException error) { Check(error.Message.Contains("ordinary query failure", StringComparison.Ordinal), "A query failure retains its concrete diagnostic."); }
        Check(await NativeHostIO.RunCommandAsync(connection, "printf 'after-failure'", [], context) == "after-failure", "An independent native query survives an earlier command failure.");
        Console.WriteLine("Batch metadata complete; uploading independent items.");
        var uploads = names.Select(name => new NativeTransferItem(Local(name), Remote(name), PlannedTransferKind.CopyFile)).ToList();
        uploads.Insert(1, new(Local("missing-upload"), Remote("missing-upload"), PlannedTransferKind.CopyFile));
        uploads.Add(new(Local("tree"), Remote("trees/"), PlannedTransferKind.CopyDirectory));
        Directory.CreateDirectory(Path.Combine(root, "remote", "merged-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "remote", "merged-root", "retained"), "old member");
        uploads.Add(new(Local("tree"), Remote("merged-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Merge));
        Directory.CreateDirectory(Path.Combine(root, "remote", "replace-root"));
        await File.WriteAllTextAsync(Path.Combine(root, "remote", "replace-root", "obsolete"), "old member");
        uploads.Add(new(Local("tree"), Remote("replace-root"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Replace));
        var uploaded = await NativeTransfers.CopyBatchAsync(uploads, context);
        Console.WriteLine("Batch upload responses complete; downloading independent items.");
        Check(uploaded.Count == uploads.Count && !uploaded[1].Success && uploaded.Where((_, index) => index != 1).All(outcome => outcome.Success), "Independent prepared uploads retain per-item failure and continue.");
        Check(File.Exists(Path.Combine(root, "remote", "merged-root", "retained"))
            && File.Exists(Path.Combine(root, "remote", "merged-root", "nested", "file")), "Remote upload Merge preserves root members without another basename.");
        Check(!File.Exists(Path.Combine(root, "remote", "replace-root", "obsolete"))
            && File.Exists(Path.Combine(root, "remote", "replace-root", "nested", "file")), "Remote upload Replace replaces the concrete root.");
        for (int index = 0; index < names.Count; index++)
            Check(await File.ReadAllTextAsync(Path.Combine(root, "remote", names[index])) == $"payload {index} 可莉\n", "Remote batch uploaded bytes and spelling.");
        Directory.CreateDirectory(Path.Combine(root, "remote", ".msync-batch-user"));
        await File.WriteAllTextAsync(Path.Combine(root, "remote", ".msync-batch-user", "visible"), "user-owned");
        var directoryMetadata = await NativeTransfers.DirectoryAsync(Remote(""), context);
        Check(directoryMetadata.Count(item => names.Contains(PathSemantics.FileName(item), StringComparer.Ordinal)) == names.Count,
            "Directory discovery retains every uploaded spelling.");
        Check(await NativeBuiltins.ReadFileStatus(Remote(names[0]), true, context, default) == "regular"
            && (await NativeBuiltins.ReadCanonicalPath(Remote(names[0]), true, context, default)).Raw == Path.Combine(root, "remote", names[0]),
            "Remote metadata retains regular kind and physical entry path.");
        var recursiveMetadata = await NativeTransfers.DirectoryAsync(Remote(""), context, recursive: true);
        Check(recursiveMetadata.Any(item => item.Raw == Path.Combine(root, "remote", ".msync-batch-user", "visible")),
            "User directories with a runtime-like spelling remain visible.");
        Check(await NativeBuiltins.ReadFileStatus(Remote("not-yet-created"), true, context, default) == "not_found",
            "Missing remote entries are ordinary metadata results.");
        Check((await NativeBuiltins.ReadCanonicalPath(Remote("not-yet-created"), false, context, default)).Raw == Path.Combine(root, "remote", "not-yet-created"),
            "Namespace projection retains a missing final entry.");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "remote", "directory-alias"), "trees/tree");
            Check(await NativeBuiltins.ReadFileStatus(Remote("directory-alias"), true, context, default) == "symlink"
                && (await NativeBuiltins.ReadCanonicalPath(Remote("directory-alias/nested/file"), true, context, default)).Raw == Path.Combine(root, "remote", "trees", "tree", "nested", "file"),
                "Metadata distinguishes the final symlink from its physical parent traversal.");
            var followed = await NativeTransfers.DirectoryAsync(Remote("directory-alias"), context);
            Check(followed.Any(item => item.Raw == Path.Combine(root, "remote", "directory-alias", "nested")),
                "Directory acquisition follows its selected root link while retaining raw spelling.");
        }
        var remoteEnsure = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Remote("expanded-actions"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Merge),
            new(null, Remote("cohort-actions"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Replace)
        ], context);
        Check(remoteEnsure.All(item => item.Success)
            && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "remote", "expanded-actions")).Any(),
            "Remote directory actions create roots without enumerating or copying payloads.");
        var remoteChild = await NativeTransfers.CopyBatchAsync([
            new(Local("tree/nested/file"), Remote("expanded-actions/file"), PlannedTransferKind.CopyFile)
        ], context);
        var remoteApplied = await NativeTransfers.CopyBatchAsync([
            new(Local("tree"), Remote("expanded-actions"), PlannedTransferKind.ApplyDirectoryMetadata, TransferDestinationMode.Merge)
        ], context);
        Check(remoteChild[0].Success && remoteApplied[0].Success
            && !Directory.Exists(Path.Combine(root, "remote", "expanded-actions", "nested"))
            && Directory.GetLastWriteTimeUtc(Path.Combine(root, "remote", "expanded-actions")) == directoryTime,
            "Remote metadata-only completion retains the directory timestamp without recursively recopying it.");
        var downloadEnsure = await NativeTransfers.CopyBatchAsync([
            new(Remote("trees/tree"), Local("download-actions"), PlannedTransferKind.EnsureDirectory, TransferDestinationMode.Merge)
        ], context);
        var downloadChild = await NativeTransfers.CopyBatchAsync([
            new(Remote("trees/tree/nested/file"), Local("download-actions/file"), PlannedTransferKind.CopyFile)
        ], context);
        var downloadApplied = await NativeTransfers.CopyBatchAsync([
            new(Remote("trees/tree"), Local("download-actions"), PlannedTransferKind.ApplyDirectoryMetadata, TransferDestinationMode.Merge)
        ], context);
        Check(downloadEnsure[0].Success && downloadChild[0].Success && downloadApplied[0].Success
            && Directory.GetLastWriteTimeUtc(Path.Combine(root, "download-actions")) == directoryTime,
            "Remote-source directory metadata uses its selected channel and preserves native time.");

        Directory.CreateDirectory(Path.Combine(root, "downloads", names[1]));
        var downloads = names.Select(name => new NativeTransferItem(Remote(name), Local("downloads/" + name), PlannedTransferKind.CopyFile)).ToList();
        downloads.Insert(2, new(Remote("missing-remote"), Local("downloads/missing-remote"), PlannedTransferKind.CopyFile));
        downloads.Add(new(Remote("trees/tree"), Local("downloads/tree-copy"), PlannedTransferKind.CopyDirectory));
        Directory.CreateDirectory(Path.Combine(root, "downloads", "merge"));
        await File.WriteAllTextAsync(Path.Combine(root, "downloads", "merge", "retained"), "old member");
        downloads.Add(new(Remote("trees/tree"), Local("downloads/merge"), PlannedTransferKind.CopyDirectory, TransferDestinationMode.Merge));
        var downloaded = await NativeTransfers.CopyBatchAsync(downloads, context);
        Console.WriteLine("Batch download responses complete; copying on the remote endpoint.");
        Check(!downloaded[1].Success && !downloaded[2].Success, "Download keeps both destination and missing-source failures.");
        Check(downloaded.Where((_, index) => index != 1 && index != 2).All(outcome => outcome.Success),
            "Independent downloads retain failed members and continue: " + string.Join("; ", downloaded
                .Select((outcome, index) => (outcome, index)).Where(item => item.index != 1 && item.index != 2 && !item.outcome.Success)
                .Select(item => $"#{item.index}: {item.outcome.Error}")));
        Check(await File.ReadAllTextAsync(Path.Combine(root, "downloads", "tree-copy", "nested", "file")) == "directory payload", "Directory batch downloads retain their root placement.");
        Check(File.Exists(Path.Combine(root, "downloads", "merge", "retained"))
            && File.Exists(Path.Combine(root, "downloads", "merge", "nested", "file")), "Remote download Merge preserves concrete-root members.");
        if (!OperatingSystem.IsWindows()) Check(new FileInfo(Path.Combine(root, "downloads", "tree-copy", "dangling")).LinkTarget == "missing", "Directory batch download retains dangling links.");
        for (int index = 0; index < names.Count; index++)
            if (index != 1) Check(await File.ReadAllTextAsync(Path.Combine(root, "downloads", names[index])) == $"payload {index} 可莉\n", "Downloaded bytes.");

        var remoteCopies = await NativeTransfers.CopyBatchAsync([
            new(Remote(names[0]), Remote("copied/" + names[0]), PlannedTransferKind.CopyFile),
            new(Remote("missing-remote"), Remote("copied/missing"), PlannedTransferKind.CopyFile),
            new(Remote(names[^1]), Remote("copied/" + names[^1]), PlannedTransferKind.CopyFile)
        ], context);
        Check(remoteCopies[0].Success && !remoteCopies[1].Success && remoteCopies[2].Success, "Independent same-endpoint copies retain individual errors.");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "remote", "copied", names[^1])) == $"payload {names.Count - 1} 可莉\n", "Same-endpoint final copy bytes.");
        Check(await NativeHostIO.RunCommandAsync(connection, "printf 'metadata-after-data'", [], context) == "metadata-after-data", "Metadata remains observable after all transfer directions.");
        Console.WriteLine("Independent prepared operations complete; finite-cohort channel acceptance is checked separately.");
        Console.WriteLine($"PASS {checks} local and remote prepared transfer checks");
    }
}
