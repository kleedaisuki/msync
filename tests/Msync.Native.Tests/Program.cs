using System.Diagnostics;
using Msync.Core;
using Msync.Runtime;

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "native-journey-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
RuntimeContext context = new(new(WorkingDirectory: root));
int assertions = 0;
int callbackCalls = 0;
ValueTask<Value> Callback(Value function, IReadOnlyList<Value> args, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    ++callbackCalls;
    return ValueTask.FromResult<Value>(new BoolValue(((StringValue)args[0]).Data != "skip"));
}
ValueTask<Value> Call(string name, params Value[] args) => NativeRegistry.InvokeAsync(name, args, context, Callback);
void Equal(object? expected, object? actual)
{
    ++assertions;
    if (!Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', received '{actual}'");
}
PathValue P(string path) => new(path, root);
StringValue S(string text) => new(text);
async Task<List<Value>> Collect(Value range)
{
    List<Value> values = [];
    await foreach (Value value in ((RangeValue)range).Items) values.Add(value);
    return values;
}
try
{
    // A complete filesystem user journey includes new data, mutation, links, enumeration and removal.
    Equal(Value.True, await Call("fs.create_directories", P("input/sub")));
    await File.WriteAllTextAsync(Path.Combine(root, "input", "sub", "β file\n.txt"), "native bytes");
    Equal(Value.True, await Call("fs.exists", P("input/sub/β file\n.txt")));
    Equal(Value.False, await Call("fs.exists", P("missing")));
    Equal(S("regular_file"), await Call("fs.status", P("input/sub/β file\n.txt")));
    Equal(Value.True, await Call("fs.is_regular_file", P("input/sub/β file\n.txt")));
    Equal(Value.False, await Call("fs.is_empty", P("input/sub/β file\n.txt")));
    Equal(Value.True, await Call("fs.copy_file", P("input/sub/β file\n.txt"), P("copy")));
    Equal("native bytes", await File.ReadAllTextAsync(Path.Combine(root, "copy")));
    await Call("fs.create_hard_link", P("copy"), P("hardlink"));
    Equal(Value.True, await Call("fs.equivalent", P("copy"), P("hardlink")));
    Equal(Value.False, await Call("fs.equivalent", P("copy"), P("input/sub/β file\n.txt")));
    await Call("fs.rename", P("hardlink"), P("renamed"));
    Equal(Value.True, await Call("fs.exists", P("renamed")));
    if (!OperatingSystem.IsWindows())
    {
        await Call("fs.create_symlink", P("missing-target"), P("dangling"));
        Equal(Value.True, await Call("fs.is_symlink", P("dangling")));
        Equal(Value.False, await Call("fs.exists", P("dangling")));
        Equal(S("symlink"), await Call("fs.symlink_status", P("dangling")));
        Equal(S("not_found"), await Call("fs.status", P("dangling")));
        Equal("missing-target", ((PathValue)await Call("fs.read_symlink", P("dangling"))).Raw);
        await Call("fs.copy_symlink", P("dangling"), P("dangling-copy"));
        Equal(Value.True, await Call("fs.remove", P("dangling-copy")));
        await Call("fs.set_permissions", P("copy"), S("owner_read owner_write owner_exec group_read others_read"));
        Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(Path.Combine(root, "copy")));
        var permissions = await Collect(await Call("fs.permissions", P("copy")));
        Equal(true, permissions.Contains(S("owner_exec")));
        using Process fifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { Path.Combine(root, "pipe") }, UseShellExecute = false })!;
        await fifo.WaitForExitAsync();
        Equal(0, fifo.ExitCode);
        Equal(S("fifo"), await Call("fs.status", P("pipe")));
        Equal(Value.True, await Call("fs.is_fifo", P("pipe")));
        await Call("fs.remove", P("pipe"));
    }
    var entries = await Collect(await Call("fs.recursive_directory_iterator", P("input")));
    Equal(2, entries.Count);
    Equal("input/sub", ((PathValue)entries[0]).Raw.Replace('\\', '/'));
    Equal(Value.True, await Call("fs.remove_all", P("input")));
    Equal(Value.False, await Call("fs.remove_all", P("input")));

    // POSIX lexical semantics must hold on every host, including when the path is remote.
    ConnectionValue remote = new(ConnectionKind.Ssh, "never-connect");
    foreach ((string input, string normalized) in new[] { ("a/./b/..", "a/"), ("a/./b/../", "a/"), ("/../../a", "/a"), ("a/..", "."), ("../a/..", ".."), ("a/.", "a/"), ("", ""), ("/", "/") })
    {
        PathValue result = (PathValue)await Call("fs.lexically_normal", new PathValue(input, Connection: remote));
        Equal(normalized, result.Raw);
        Equal(remote, result.Connection);
    }
    Equal(".profile", ((PathValue)await Call("fs.stem", P(".profile"))).Raw);
    Equal("", ((PathValue)await Call("fs.extension", P(".profile"))).Raw);
    Equal(".gz", ((PathValue)await Call("fs.extension", P("β.tar.gz"))).Raw);
    Equal("../../d", ((PathValue)await Call("fs.lexically_relative", new PathValue("/a/d", Connection: remote), new PathValue("/a/b/c", Connection: remote))).Raw);
    PathValue qualified = (PathValue)await Call("path.qualify", remote, new PathValue("~/code", root, HomeDirectory: "/local-home"));
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
            PathValue remoteDirectory = new(Path.Combine(root, "remote directory"), Connection: remote);
            Equal(Value.True, await Call("fs.create_directories", remoteDirectory));
            PathValue remoteFile = new(remoteDirectory.Raw + "/β back\\slash\n", Connection: remote);
            await File.WriteAllTextAsync(remoteFile.Raw, "remote metadata");
            Equal(Value.True, await Call("fs.exists", remoteFile));
            Equal(Value.True, await Call("fs.is_regular_file", remoteFile));
            Equal(S("regular_file"), await Call("fs.status", remoteFile));
            Equal(remoteFile.Raw, ((PathValue)await Call("fs.absolute", remoteFile)).Raw);
            Equal(remoteFile.Raw, ((PathValue)await Call("fs.canonical", remoteFile)).Raw);
            PathValue remoteLink = remoteFile with { Raw = remoteDirectory.Raw + "/link\n" };
            await Call("fs.create_symlink", new PathValue("missing\n", Connection: remote), remoteLink);
            Equal(Value.True, await Call("fs.is_symlink", remoteLink));
            Equal(Value.False, await Call("fs.exists", remoteLink));
            Equal("missing\n", ((PathValue)await Call("fs.read_symlink", remoteLink)).Raw);
            PathValue remoteCopy = remoteLink with { Raw = remoteDirectory.Raw + "/link-copy\n" };
            await Call("fs.copy_symlink", remoteLink, remoteCopy);
            Equal("missing\n", ((PathValue)await Call("fs.read_symlink", remoteCopy)).Raw);
            await Call("fs.set_permissions", remoteFile, S("owner_read owner_write owner_exec"));
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(remoteFile.Raw));
            Equal(3, (await Collect(await Call("fs.directory_iterator", remoteDirectory))).Count);
            Equal(Value.True, await Call("fs.remove_all", remoteDirectory));
            Equal(Value.False, await Call("fs.exists", remoteDirectory));
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
    }

    Equal(S("β"), await Call("string.trim", S("  β  ")));
    Equal(S("STRASSE"), await Call("string.to_uppercase", S("strasse")));
    Equal(4, (await Collect(await Call("string.split", S("β🚀"), S("")))).Count);
    Equal(Value.True, await Call("bool.implies", Value.False, Value.False));

    // Zero demand never opens a directory or calls an effectful predicate.
    Value missingRange = await Call("fs.directory_iterator", P("missing-directory"));
    Equal(0, (await Collect(await Call("ranges.take", missingRange, new NumberValue(0)))).Count);
    Value source = await Call("ranges.of", S("keep"), S("skip"), S("last"));
    Value filtered = await Call("ranges.filter", source, new FunctionValue(0));
    Equal(0, callbackCalls);
    Equal(S("keep"), await Call("ranges.front", filtered));
    Equal(1, callbackCalls);
    Equal(2, (await Collect(filtered)).Count);
    Equal(4, callbackCalls);
    Equal(Value.True, await Call("ranges.contains", source, S("skip")));
    Equal(Value.True, await Call("ranges.any_of", source, new FunctionValue(0)));
    Equal(5, callbackCalls);
    Equal(S("last"), await Call("ranges.front", await Call("ranges.reverse", source)));
    Equal(2, (await Collect(await Call("ranges.unique", await Call("ranges.of", S("a"), S("b"), S("a"))))).Count);
    Console.WriteLine($"native filesystem/string/range journeys: {assertions} assertions passed");
}
finally { Directory.Delete(root, true); }
