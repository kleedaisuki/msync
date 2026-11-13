using System.Diagnostics;
using Msync.Core;
using Msync.Runtime;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePath = Msync.Core.Native.NativePath;

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "native-journey-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
NativeRuntimeContext context = new(new(WorkingDirectory: root));
int assertions = 0;
int callbackCalls = 0;
ValueTask<NativeValue> Callback(NativeValue function, IReadOnlyList<NativeValue> args, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    ++callbackCalls;
    if (args[0].Kind == NativeKind.Path)
    {
        NativePath path = args[0].AsPath();
        return ValueTask.FromResult(function.AsFunction().NativeName == "identity-path" ? args[0] : NativeValue.FromBool(!path.Raw.EndsWith("skip", StringComparison.Ordinal)));
    }
    return ValueTask.FromResult(NativeValue.FromBool(args[0].AsString() != "skip"));
}
ValueTask<NativeValue> Call(string name, params NativeValue[] args) => NativeBuiltins.InvokeAsync(name, args, context, Callback);
void Equal(object? expected, object? actual)
{
    ++assertions;
    if (!Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', received '{actual}'");
}
NativePath P(string path) => new(path, root);
NativeValue S(string text) => NativeValue.FromString(text);
async Task<List<NativeValue>> Collect(NativeValue range)
{
    List<NativeValue> values = [];
    await foreach (NativeValue value in range.AsRange().Items) values.Add(value);
    return values;
}
try
{
    // A complete filesystem user journey includes new data, mutation, links, enumeration and removal.
    Equal(NativeValue.True, await Call("fs.create_directories", P("input/sub")));
    await File.WriteAllTextAsync(Path.Combine(root, "input", "sub", "β file\n.txt"), "native bytes");
    Equal(NativeValue.True, await Call("fs.exists", P("input/sub/β file\n.txt")));
    Equal(NativeValue.False, await Call("fs.exists", P("missing")));
    Equal(S("regular"), await Call("fs.status", P("input/sub/β file\n.txt")));
    Equal(NativeValue.True, await Call("fs.is_regular_file", P("input/sub/β file\n.txt")));
    Equal(NativeValue.False, await Call("fs.is_empty", P("input/sub/β file\n.txt")));
    Equal(NativeValue.True, await Call("fs.copy_file", P("input/sub/β file\n.txt"), P("copy")));
    Equal("native bytes", await File.ReadAllTextAsync(Path.Combine(root, "copy")));
    Equal(NativeValue.True, await Call("fs.copy_file", S("copy"), S("copy-string")));
    Equal("native bytes", await File.ReadAllTextAsync(Path.Combine(root, "copy-string")));
    await Call("fs.create_hard_link", P("copy"), P("hardlink"));
    Equal(NativeValue.True, await Call("fs.equivalent", P("copy"), P("hardlink")));
    Equal(NativeValue.False, await Call("fs.equivalent", P("copy"), P("input/sub/β file\n.txt")));
    await Call("fs.rename", P("hardlink"), P("renamed"));
    Equal(NativeValue.True, await Call("fs.exists", P("renamed")));
    if (!OperatingSystem.IsWindows())
    {
        await Call("fs.create_symlink", P("missing-target"), P("dangling"));
        Equal(NativeValue.True, await Call("fs.is_symlink", P("dangling")));
        Equal(NativeValue.False, await Call("fs.exists", P("dangling")));
        Equal(S("symlink"), await Call("fs.symlink_status", P("dangling")));
        Equal(S("not_found"), await Call("fs.status", P("dangling")));
        Equal("missing-target", (await Call("fs.read_symlink", P("dangling"))).AsPath().Raw);
        await Call("fs.copy_symlink", P("dangling"), P("dangling-copy"));
        Equal(NativeValue.True, await Call("fs.remove", P("dangling-copy")));
        await Call("fs.set_permissions", P("copy"), S("owner_read owner_write owner_exec group_read others_read"));
        Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(Path.Combine(root, "copy")));
        var permissions = await Collect(await Call("fs.permissions", P("copy")));
        Equal(true, permissions.Contains(S("owner_exec")));
        using Process fifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { Path.Combine(root, "pipe") }, UseShellExecute = false })!;
        await fifo.WaitForExitAsync();
        Equal(0, fifo.ExitCode);
        Equal(S("fifo"), await Call("fs.status", P("pipe")));
        Equal(NativeValue.True, await Call("fs.is_fifo", P("pipe")));
        Equal(NativeValue.True, await Call("fs.is_other", P("pipe")));
        await Call("fs.set_permissions", P("copy"), S("0o640"));
        Equal((UnixFileMode)0x1a0, File.GetUnixFileMode(Path.Combine(root, "copy")));
        await Call("fs.set_permissions", P("copy"), S("owner_all group_all others_all"));
        Equal((UnixFileMode)0x1ff, File.GetUnixFileMode(Path.Combine(root, "copy")));
        await Call("fs.set_readonly", P("copy"), NativeValue.True);
        Equal(NativeValue.True, await Call("fs.is_readonly", P("copy")));
        await Call("fs.set_readonly", P("copy"), NativeValue.False);
        Equal(NativeValue.False, await Call("fs.is_readonly", P("copy")));
        await Call("fs.remove", P("pipe"));
    }
    var entries = await Collect(await Call("fs.recursive_directory_iterator", P("input")));
    Equal(2, entries.Count);
    Equal("input/sub", entries[0].AsPath().Raw.Replace('\\', '/'));
    Directory.CreateDirectory(Path.Combine(root, "ordering"));
    string[] orderedNames = ["a", "same-\uE000", "same-\U00010000", "\uE000", "\U00010000"];
    foreach (string name in orderedNames.Reverse()) await File.WriteAllTextAsync(Path.Combine(root, "ordering", name), name);
    List<NativeValue> orderedLocal = await Collect(await Call("fs.directory_iterator", P("ordering")));
    Equal(string.Join('|', orderedNames), string.Join('|', orderedLocal.Select(value => Path.GetFileName(value.AsPath().Raw))));
    Equal(NativeValue.True, await Call("fs.remove_all", P("input")));
    Equal(NativeValue.False, await Call("fs.remove_all", P("input")));

    // POSIX lexical semantics must hold on every host, including when the path is remote.
    NativeConnection remote = new(ConnectionKind.Ssh, "never-connect");
    foreach ((string input, string normalized) in new[] { ("a/./b/..", "a/"), ("a/./b/../", "a/"), ("/../../a", "/a"), ("a/..", "."), ("../a/..", ".."), ("a/.", "a/"), ("", ""), ("/", "/") })
    {
        NativePath result = (await Call("fs.lexically_normal", new NativePath(input, Connection: remote))).AsPath();
        Equal(normalized, result.Raw);
        Equal(remote, result.Connection);
    }
    Equal(".profile", (await Call("fs.stem", P(".profile"))).AsPath().Raw);
    Equal("", (await Call("fs.extension", P(".profile"))).AsPath().Raw);
    Equal(".gz", (await Call("fs.extension", P("β.tar.gz"))).AsPath().Raw);
    Equal("../../d", (await Call("fs.lexically_relative", new NativePath("/a/d", Connection: remote), new NativePath("/a/b/c", Connection: remote))).AsPath().Raw);
    NativePath qualified = (await Call("path.qualify", remote, new NativePath("~/code", root, HomeDirectory: "/local-home"))).AsPath();
    Equal(null, qualified.HomeDirectory);
    Equal(null, qualified.WorkingDirectory);
    Equal(remote, qualified.Connection);

    if (!OperatingSystem.IsWindows())
    {
        string tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        string ssh = Path.Combine(tools, "ssh");
        await File.WriteAllTextAsync(ssh, "#!/bin/sh\nfor argument do command=$argument; done\nexec sh -c \"$command\"\n");
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + originalPath);
        try
        {
            NativePath remoteDirectory = new(Path.Combine(root, "remote directory"), Connection: remote);
            Equal(NativeValue.True, await Call("fs.create_directories", remoteDirectory));
            NativePath remoteFile = new(remoteDirectory.Raw + "/β back\\slash\n", Connection: remote);
            await File.WriteAllTextAsync(remoteFile.Raw, "remote metadata");
            Equal(NativeValue.True, await Call("fs.exists", remoteFile));
            Equal(NativeValue.True, await Call("fs.is_regular_file", remoteFile));
            Equal(S("regular"), await Call("fs.status", remoteFile));
            Equal(remoteFile.Raw, (await Call("fs.absolute", remoteFile)).AsPath().Raw);
            Equal(remoteFile.Raw, (await Call("fs.canonical", remoteFile)).AsPath().Raw);
            NativePath remoteLink = remoteFile with { Raw = remoteDirectory.Raw + "/link\n" };
            await Call("fs.create_symlink", new NativePath("missing\n", Connection: remote), remoteLink);
            Equal(NativeValue.True, await Call("fs.is_symlink", remoteLink));
            Equal(NativeValue.False, await Call("fs.exists", remoteLink));
            Equal("missing\n", (await Call("fs.read_symlink", remoteLink)).AsPath().Raw);
            NativePath remoteCopy = remoteLink with { Raw = remoteDirectory.Raw + "/link-copy\n" };
            await Call("fs.copy_symlink", remoteLink, remoteCopy);
            Equal("missing\n", (await Call("fs.read_symlink", remoteCopy)).AsPath().Raw);
            await Call("fs.set_permissions", remoteFile, S("owner_read owner_write owner_exec"));
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(remoteFile.Raw));
            NativeRange directDirectory = (await Call("fs.directory_iterator", remoteDirectory)).AsRange();
            Equal(remoteDirectory, directDirectory.Selection?.Directory);
            Equal(3, (await Collect(directDirectory)).Count);
            NativeRange recursiveDirectory = (await Call("fs.recursive_directory_iterator", remoteDirectory)).AsRange();
            Equal(remoteDirectory, recursiveDirectory.Selection?.Directory);
            Equal(true, recursiveDirectory.Selection?.Recursive);
            NativeRange recursiveProjected = (await Call("ranges.take", recursiveDirectory, NativeValue.FromNumber(1))).AsRange();
            Equal(true, recursiveProjected.Selection?.Recursive);
            Equal(1, (await Collect(recursiveProjected)).Count);
            NativePath remoteOrdering = remoteDirectory with { Raw = remoteDirectory.Raw + "/ordering" };
            Directory.CreateDirectory(remoteOrdering.Raw);
            foreach (string name in orderedNames.Reverse()) await File.WriteAllTextAsync(Path.Combine(remoteOrdering.Raw, name), name);
            List<NativeValue> orderedRemote = await Collect(await Call("fs.directory_iterator", remoteOrdering));
            Equal(string.Join('|', orderedNames), string.Join('|', orderedRemote.Select(value => Path.GetFileName(value.AsPath().Raw))));
            Equal(NativeValue.True, await Call("fs.remove_all", remoteDirectory));
            Equal(NativeValue.False, await Call("fs.exists", remoteDirectory));
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
    }

    Equal(S("β"), await Call("string.trim", S("  β  ")));
    Equal(S("STRASSE"), await Call("string.to_uppercase", S("strasse")));
    Equal(4, (await Collect(await Call("string.split", S("β🚀"), S("")))).Count);
    Equal(NativeValue.True, await Call("bool.implies", NativeValue.False, NativeValue.False));

    // Zero demand never opens a directory or calls an effectful predicate.
    Equal(0, (await Collect(await Call("ranges.of"))).Count);
    NativeValue missingRange = await Call("fs.directory_iterator", P("missing-directory"));
    Equal(0, (await Collect(await Call("ranges.take", missingRange, NativeValue.FromNumber(0)))).Count);
    NativeValue source = await Call("ranges.of", S("keep"), S("skip"), S("last"));
    NativeValue filtered = await Call("ranges.filter", source, NativeValue.FromFunction(new(0)));
    Equal(0, callbackCalls);
    Equal(S("keep"), await Call("ranges.front", filtered));
    Equal(1, callbackCalls);
    Equal(2, (await Collect(filtered)).Count);
    Equal(4, callbackCalls);
    Equal(NativeValue.True, await Call("ranges.contains", source, S("skip")));
    Equal(NativeValue.True, await Call("ranges.any_of", source, NativeValue.FromFunction(new(0))));
    Equal(5, callbackCalls);
    Equal(S("last"), await Call("ranges.front", await Call("ranges.reverse", source)));
    Equal(2, (await Collect(await Call("ranges.unique", await Call("ranges.of", S("a"), S("b"), S("a"))))).Count);
    async IAsyncEnumerable<NativeValue> UnexpectedEnumeration()
    {
        await Task.Yield();
        yield return await ValueTask.FromException<NativeValue>(new InvalidOperationException("Selector source must be enumerated through discovered candidate values only"));
    }
    NativePath selectedDirectory = new("/selected", Connection: remote);
    NativeRange selector = new(UnexpectedEnumeration(), TypeRef.Path, selection: new NativePathSelection(selectedDirectory, ".+"));
    NativeValue[] candidates = [new NativePath("/selected/a", Connection: remote), new NativePath("/selected/skip", Connection: remote), new NativePath("/selected/c", Connection: remote)];
    async Task<List<NativeValue>> Project(NativeValue value)
    {
        NativeRange range = value.AsRange();
        while (range.Resolve is not null) range = await range.Resolve(CancellationToken.None);
        Equal(true, range.Selection is not null);
        IAsyncEnumerable<NativeValue> projected = NativeRange.Snapshot(candidates, TypeRef.Path).Items;
        if (range.Selection!.Apply is not null) projected = range.Selection.Apply(projected, CancellationToken.None);
        return await Collect(NativeValue.FromRange(new(projected, range.ItemType)));
    }
    Equal(2, (await Project(await Call("ranges.take", selector, NativeValue.FromNumber(2)))).Count);
    Equal(candidates[1], (await Project(await Call("ranges.drop", selector, NativeValue.FromNumber(1))))[0]);
    Equal(candidates[2], (await Project(await Call("ranges.stride", selector, NativeValue.FromNumber(2))))[1]);
    Equal(candidates[2], (await Project(await Call("ranges.reverse", selector)))[0]);
    Equal(3, (await Project(await Call("ranges.unique", selector))).Count);
    Equal(2, (await Project(await Call("ranges.filter", selector, NativeValue.FromFunction(new(0))))).Count);
    Equal(1, (await Project(await Call("ranges.take_while", selector, NativeValue.FromFunction(new(0))))).Count);
    Equal(candidates[1], (await Project(await Call("ranges.drop_while", selector, NativeValue.FromFunction(new(0)))))[0]);
    Equal(candidates[0], (await Project(await Call("ranges.transform", selector, NativeValue.FromFunction(new(0, NativeName: "identity-path")))))[0]);
    Equal(NativeValue.FromRange(selector), await Call("ranges.identity", selector));
    int resolutions = 0;
    NativeRange deferredSelector = new(UnexpectedEnumeration(), TypeRef.Path, resolve: cancellation =>
    {
        cancellation.ThrowIfCancellationRequested();
        ++resolutions;
        return ValueTask.FromResult(selector);
    });
    NativeValue deferredChain = await Call("ranges.reverse", await Call("ranges.take", await Call("ranges.filter", deferredSelector, NativeValue.FromFunction(new(0))), NativeValue.FromNumber(2)));
    Equal(0, resolutions);
    Equal(candidates[2], (await Project(deferredChain))[0]);
    Equal(1, resolutions);
    NativeRange zeroDemand = (await Call("ranges.take", deferredSelector, NativeValue.FromNumber(0))).AsRange();
    Equal(null, zeroDemand.Resolve);
    Equal(null, zeroDemand.Selection);
    Equal(0, (await Collect(zeroDemand)).Count);
    Equal(1, resolutions);
    Console.WriteLine($"native filesystem/string/range journeys: {assertions} assertions passed");
}
finally { Directory.Delete(root, true); }
