using Msync.Optimization;
using Msync.Core;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

internal static class PathNamespaceChecks
{
    internal static async Task RunAsync(string scratch)
    {
        foreach (string mode in new[] { "source", "mso", "mexe" })
        {
            string directory = Path.Combine(scratch, mode);
            Directory.CreateDirectory(directory);
            await DirectoryLinkLifecycle(directory, mode);
            await FutureAlias(directory, mode);
            NoStages(directory);
        }
        Console.WriteLine("PASS: directory symlink publication/replacement/failed-copy retry/retarget and future namespace alias hazards through source, .mso and .mexe.");
    }

    private static async Task DirectoryLinkLifecycle(string directory, string mode)
    {
        string root = Path.Combine(directory, "links-可莉 folder");
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        string first = Path.Combine(root, "target-first");
        string second = Path.Combine(root, "target-second");
        foreach (string path in new[] { source, destination, first, second }) Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(first, "file"), "first target stays intact");
        await File.WriteAllTextAsync(Path.Combine(second, "file"), "second target stays intact");
        string original = Path.Combine(source, "link");
        string published = Path.Combine(destination, "link");
        Directory.CreateSymbolicLink(original, first);
        const string copy = "./source/link -> ./destination/";
        await ExpectTransfer(copy, root, mode, true);
        VerifyLink(published, first);
        await ExpectTransfer(copy, root, mode, true);
        VerifyLink(published, first);

        Directory.Delete(original);
        Directory.CreateSymbolicLink(original, second);
        await ExpectTransfer(copy, root, mode, true);
        VerifyLink(published, second);
        Require(await File.ReadAllTextAsync(Path.Combine(first, "file")) == "first target stays intact", "Retargeting a copied link changed its old target.");
        Require(await File.ReadAllTextAsync(Path.Combine(second, "file")) == "second target stays intact", "Retargeting a copied link changed its new target.");

        Directory.Delete(published);
        Directory.CreateDirectory(published);
        await File.WriteAllTextAsync(Path.Combine(published, "obstruction"), "keep on failure");
        await ExpectTransfer(copy, root, mode, false);
        Require(await File.ReadAllTextAsync(Path.Combine(published, "obstruction")) == "keep on failure", "Failed link publication destroyed an existing directory.");
        NoStages(root);
        Directory.Delete(published, recursive: true);
        await ExpectTransfer(copy, root, mode, true);
        VerifyLink(published, second);

        await File.WriteAllTextAsync(Path.Combine(destination, "stale"), "old cohort");
        await ExpectTransfer("./source -> ./destination/.", root, mode, true);
        Require(!File.Exists(Path.Combine(destination, "stale")), "Explicit directory replacement retained an old member.");
        VerifyLink(published, second);
        Require(await File.ReadAllTextAsync(Path.Combine(first, "file")) == "first target stays intact", "Replacement touched an unrelated old link target.");
        Require(await File.ReadAllTextAsync(Path.Combine(second, "file")) == "second target stays intact", "Replacement traversed and modified the link target.");
        NoStages(root);
    }

    private static async Task FutureAlias(string directory, string mode)
    {
        string root = Path.Combine(directory, "future");
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        byte[] payload = new byte[65536];
        for (int file = 0; file < 24; file++)
        {
            Array.Fill(payload, (byte)file);
            await File.WriteAllBytesAsync(Path.Combine(source, $"regular-{file:D2}"), payload);
        }
        await File.WriteAllTextAsync(Path.Combine(target, "file"), "original");
        Directory.CreateSymbolicLink(Path.Combine(source, "link"), target);
        const string sourceText = "./source -> ./destination; ./destination/link/file -> ./observed; \"later\" -> ./target/file;";
        Interpreter interpreter = new(Prepare(sourceText, root, mode), new(WorkingDirectory: root, Jobs: 4));
        await interpreter.ExecuteAsync();
        Require(interpreter.FailedTransfers == 0, "A future-alias transfer failed.");
        Require(await File.ReadAllTextAsync(Path.Combine(root, "observed")) == "original", "A later target write overtook a read through a newly published directory link.");
        Require(await File.ReadAllTextAsync(Path.Combine(target, "file")) == "later", "The final target write was lost.");
        VerifyLink(Path.Combine(root, "destination", "link"), target);
        for (int file = 0; file < 24; file++)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, "destination", $"regular-{file:D2}"));
            Require(bytes.Length == payload.Length && bytes.All(value => value == (byte)file), "A normal member was lost while refining a future namespace.");
        }
        NoStages(root);
    }

    private static async Task ExpectTransfer(string source, string directory, string mode, bool expected)
    {
        using StringWriter errors = new();
        Interpreter interpreter = new(Prepare(source, directory, mode), new(WorkingDirectory: directory, Error: errors, Jobs: 4));
        Value result = await interpreter.ExecuteAsync();
        Require(result is BoolValue boolean && boolean.Data == expected, $"{mode}: unexpected transfer result for {source}: {result}; {errors}");
        Require(interpreter.FailedTransfers == (expected ? 0 : 1), "Transfer failure accounting disagreed with its observed Bool result.");
    }

    private static Module Prepare(string source, string directory, string mode)
    {
        Module module = Frontend.Compile(source, Path.Combine(directory, "namespace.msync"));
        if (mode == "source") return module;
        if (mode == "mexe") module = Binding.Bind(module, new(directory, directory));
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }

    private static void VerifyLink(string path, string target)
    {
        DirectoryInfo link = new(path);
        Require(link.LinkTarget is not null, $"Directory link {path} became an ordinary directory.");
        string actual = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? "";
        Require(string.Equals(actual, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), $"Directory link {path} changed its endpoint: {actual}.");
    }

    private static void NoStages(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                Require(!Path.GetFileName(entry).StartsWith(".msync-", StringComparison.Ordinal), $"Owned transfer stage leaked: {entry}");
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory) pending.Push(entry);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
