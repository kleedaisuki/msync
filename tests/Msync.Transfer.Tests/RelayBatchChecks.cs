using Msync.Core;
using Msync.Runtime;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

internal static class RelayBatchChecks
{
    internal static async Task RunAsync()
    {
        string root = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_WORK")!;
        NativeConnection source = new(ConnectionKind.Ssh, Environment.GetEnvironmentVariable("MSYNC_TRANSFER_REMOTE")!);
        NativeConnection target = new(ConnectionKind.Ssh, Environment.GetEnvironmentVariable("MSYNC_TRANSFER_SECOND_REMOTE")!);
        Directory.CreateDirectory(Path.Combine(root, "source"));
        Directory.CreateDirectory(Path.Combine(root, "target"));
        NativePathValue A(string name) => new(Path.Combine(root, "source", name), Connection: source);
        NativePathValue B(string name) => new(Path.Combine(root, "target", name), Connection: target);
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }
        List<string> names = Enumerable.Range(0, 32).Select(index => $"file-{index:000}").ToList();
        names.AddRange(["two words", "line\n可莉%0A%25\\'"]);
        for (int index = 0; index < names.Count; index++)
            await File.WriteAllTextAsync(Path.Combine(root, "source", names[index]), $"relay payload {index}\n");
        Check(await NativeHostIO.RunCommandAsync(source, "printf 'source metadata'", [], null) == "source metadata", "Source metadata remains observable.");
        Check(await NativeHostIO.RunCommandAsync(target, "printf 'target metadata'", [], null) == "target metadata", "Target metadata remains observable.");
        Check((await NativeTransfers.DirectoryAsync(A(""), new())).Count == names.Count, "Selected source metadata is acquired before relay.");
        Directory.CreateDirectory(Path.Combine(root, "target", names[10]));
        var items = names.Select(name => new NativeTransferItem(A(name), B(name), PlannedTransferKind.CopyFile)).ToList();
        items.Insert(5, new(A("missing"), B("missing"), PlannedTransferKind.CopyFile));
        var outcomes = await NativeTransfers.CopyBatchAsync(items, new NativeRuntimeContext(), CancellationToken.None);
        Check(!outcomes[5].Success && !outcomes[11].Success, "Relay retains missing-source and obstructed-target failures.");
        Check(outcomes.Where((_, index) => index != 5 && index != 11).All(item => item.Success),
            "A failed relay item does not consume the next archive or suppress later independent members: "
            + string.Join("; ", outcomes.Select((item, index) => (item, index)).Where(pair => pair.index != 5 && pair.index != 11 && !pair.item.Success)
                .Select(pair => $"#{pair.index} {pair.item.Error}")));
        for (int index = 0; index < names.Count; index++)
            if (index != 10) Check(await File.ReadAllTextAsync(Path.Combine(root, "target", names[index])) == $"relay payload {index}\n", "Relay output bytes and spelling.");

        Directory.CreateDirectory(Path.Combine(root, "source", "tree", "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "source", "tree", "nested", "file"), "tree data");
        const string linkTarget = "missing%0A\n可莉";
        File.CreateSymbolicLink(Path.Combine(root, "source", "link"), linkTarget);
        var structured = await NativeTransfers.CopyBatchAsync([
            new(A("tree"), B("trees/"), PlannedTransferKind.CopyDirectory),
            new(A("link"), B("link"), PlannedTransferKind.CopyLink)
        ], new NativeRuntimeContext());
        Check(structured.All(item => item.Success), "Directory and link archives retain their relay modes.");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "target", "trees", "tree", "nested", "file")) == "tree data", "Container relay directory placement.");
        Check(new FileInfo(Path.Combine(root, "target", "link")).LinkTarget == linkTarget, "Relay wire decoding restores exact symbolic-link targets.");

        await File.WriteAllTextAsync(Path.Combine(root, "source", "left"), "left");
        await File.WriteAllTextAsync(Path.Combine(root, "target", "right"), "right");
        var forward = NativeTransfers.CopyBatchAsync([new NativeTransferItem(A("left"), B("left-out"), PlannedTransferKind.CopyFile)], new NativeRuntimeContext());
        var reverse = NativeTransfers.CopyBatchAsync([new NativeTransferItem(B("right"), A("right-out"), PlannedTransferKind.CopyFile)], new NativeRuntimeContext());
        var opposite = await Task.WhenAll(forward, reverse);
        Check(opposite.All(batch => batch[0].Success), "Opposite-direction disjoint relays both finish.");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "target", "left-out")) == "left"
            && await File.ReadAllTextAsync(Path.Combine(root, "source", "right-out")) == "right", "Both relay directions finish without ABBA deadlock.");
        Check(await NativeHostIO.RunCommandAsync(source, "printf 'source after data'", [], null) == "source after data"
            && await NativeHostIO.RunCommandAsync(target, "printf 'target after data'", [], null) == "target after data", "Both endpoints remain available after independent relay failures.");
        Console.WriteLine($"PASS {checks} prepared relay checks");
    }
}
