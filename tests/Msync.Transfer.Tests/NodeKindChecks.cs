using System.Diagnostics;
using System.Reflection;
using Msync.Core;
using Msync.Runtime;

internal static class NodeKindChecks
{
    internal static async Task RunAsync()
    {
        CheckTags();
        string root = Path.GetFullPath(Path.Combine(".temp", "node-kind", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        RuntimeContext context = new(new ExecutionOptions(WorkingDirectory: root));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "source", "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "source", "nested", "file"), "ordinary bytes");
            await CheckStatus("source", "directory", context);
            await CheckStatus("source/nested/file", "regular", context);
            await Copy("source", "copied/", context);
            Check(await File.ReadAllTextAsync(Path.Combine(root, "copied/source/nested/file")) == "ordinary bytes", "recursive file bytes");
            if (OperatingSystem.IsWindows()) await WindowsJourney(root, context);
            else
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "source", "alias"), "nested");
                File.CreateSymbolicLink(Path.Combine(root, "source", "dangling"), "missing");
                await CheckStatus("source/alias", "symlink", context);
                await CheckStatus("source/dangling", "symlink", context);
                await Copy("source", "linked/", context);
                Check(new DirectoryInfo(Path.Combine(root, "linked/source/alias")).LinkTarget == "nested", "directory link preserved");
                Check(new FileInfo(Path.Combine(root, "linked/source/dangling")).LinkTarget == "missing", "dangling link preserved");
            }
            Console.WriteLine("Node-kind tag contract and local filesystem journey passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckTags()
    {
        Type owner = typeof(TransferEngine).Assembly.GetType("Msync.Runtime.NativeFileMetadata", throwOnError: true)!;
        var isLink = owner.GetMethod("IsNameSurrogate", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<uint, bool>>();
        Check(isLink(0xa000000c), "symbolic-link tag");
        Check(isLink(0xa0000003), "junction tag");
        Check(!isLink(0x80000017), "WOF data filter");
        Check(!isLink(0x80000018), "WCI data filter");
        for (uint variant = 0; variant < 16; ++variant)
            Check(!isLink(0x9000001a | variant << 12), $"cloud data-filter variant {variant}");
        for (uint tag = 0; tag < 65536; ++tag)
        {
            Check(!isLink(0x80000000 | tag), "tag number is not link capability");
            Check(isLink(0xa0000000 | tag), "name-surrogate bit owns capability");
        }
    }

    private static async Task WindowsJourney(string root, RuntimeContext context)
    {
        string filtered = Path.Combine(root, "source", "filtered");
        string bytes = new('x', 256 * 1024);
        await File.WriteAllTextAsync(filtered, bytes);
        await Run("compact.exe", ["/c", "/exe:lzx", "/i", "/q", filtered]);
        Check((File.GetAttributes(filtered) & FileAttributes.ReparsePoint) != 0, "compact fixture must exercise WOF reparse metadata");
        await CheckStatus("source/filtered", "regular", context);
        await Copy("source/filtered", "filtered-copy", context);
        Check(await File.ReadAllTextAsync(Path.Combine(root, "filtered-copy")) == bytes, "native WOF copy bytes");
        await Copy("source", "source/overlap/", context);
        Check(await File.ReadAllTextAsync(Path.Combine(root, "source/overlap/source/filtered")) == bytes, "WOF overlap snapshot bytes");

        string junction = Path.Combine(root, "source", "junction");
        await Run("cmd.exe", ["/d", "/c", "mklink", "/J", junction, Path.Combine(root, "source", "nested")]);
        await CheckStatus("source/junction", "symlink", context);
        Value range = await Call("fs.recursive_directory_iterator", "source", context);
        await foreach (Value entry in ((RangeValue)range).Items)
            Check(!((PathValue)entry).Raw.Replace('\\', '/').Contains("junction/", StringComparison.Ordinal), "recursive enumeration does not follow junctions");
        Check(await Call("fs.remove_all", "source/junction", context) == Value.True, "junction removal");
        Check(File.Exists(Path.Combine(root, "source", "nested", "file")), "junction removal preserves referent");
    }

    private static async Task Run(string command, string[] arguments)
    {
        ProcessStartInfo start = new(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, $"{command}: {await output}\n{await error}");
    }

    private static async Task CheckStatus(string path, string expected, RuntimeContext context)
    {
        Check(await Call("fs.symlink_status", path, context) == new StringValue(expected), $"status: {path}");
    }

    private static ValueTask<Value> Call(string operation, string path, RuntimeContext context) =>
        NativeRegistry.InvokeAsync(operation, [new PathValue(path, context.WorkingDirectory)], context,
            (_, _, _) => throw new InvalidOperationException("No callback in filesystem classification."));

    private static async Task Copy(string source, string target, RuntimeContext context) =>
        Check(await TransferEngine.CopyAsync(new PathValue(source, context.WorkingDirectory),
            new PathValue(target, context.WorkingDirectory), context), $"copy: {source}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
