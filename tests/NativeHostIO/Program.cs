using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

if (args.FirstOrDefault() == "--child-echo")
{
    string childInput = await Console.In.ReadToEndAsync();
    Console.Write($"{Environment.ProcessId}:{childInput}");
    return;
}
if (args.FirstOrDefault() == "--child-wait")
{
    await Task.Delay(Timeout.Infinite);
    return;
}

string root = args.FirstOrDefault() == "--remote-case" ? Path.GetFullPath(args[2])
    : Path.Combine(FindRoot(), ".temp", "native-host-io", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
NativeRuntimeContext context = new(new(WorkingDirectory: root, Output: new StringWriter(), Error: new StringWriter()));
NativeHostIO.Attach(context);
if (args.FirstOrDefault() == "--remote-case")
{
    await Remote(args[1]);
    Console.WriteLine($"PASS remote {args[1]}");
    return;
}

NativeValue binary = NativeValue.FromRange(NativeRange.Snapshot(
    Enumerable.Range(0, 256).Select(value => NativeValue.FromNumber(value)), TypeRef.Number));
await Call("io.write_bytes", PathValue("bytes.bin"), binary);
Check(File.ReadAllBytes(Path.Combine(root, "bytes.bin")).SequenceEqual(Enumerable.Range(0, 256).Select(value => (byte)value)), "binary write");
NativeValue bytesRead = await Call("io.read_bytes", PathValue("bytes.bin"));
List<byte> roundtrip = [];
await foreach (NativeValue item in bytesRead.AsRange().Items) roundtrip.Add((byte)item.AsNumber());
Check(roundtrip.SequenceEqual(Enumerable.Range(0, 256).Select(value => (byte)value)), "binary read");
string text = "星🙂\nline\0tail";
await Call("io.write_text", PathValue("text.txt"), NativeValue.FromString(text));
Check((await Call("io.read_text", PathValue("text.txt"))).AsString() == text, "text roundtrip");
await Call("io.append_text", PathValue("text.txt"), NativeValue.FromString("追加"));
Check(File.ReadAllText(Path.Combine(root, "text.txt")) == text + "追加", "text append");

string source = Path.Combine(root, "tree");
Directory.CreateDirectory(Path.Combine(source, "nested"));
File.WriteAllBytes(Path.Combine(source, "one.bin"), [0, 128, 255]);
File.WriteAllText(Path.Combine(source, "nested/two.txt"), "nested");
DateTime fixedTime = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
File.SetLastWriteTimeUtc(Path.Combine(source, "one.bin"), fixedTime);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(source, "one.bin"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
await NativeHostIO.TransferAsync(PathValue("tree"), context.AsPath(NativeValue.FromString("copies/")), context);
string copied = Path.Combine(root, "copies/tree/one.bin");
Check(File.ReadAllBytes(copied).SequenceEqual(new byte[] { 0, 128, 255 }), "directory bytes");
Check(File.GetLastWriteTimeUtc(copied) == fixedTime, "mtime");
if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(copied) == File.GetUnixFileMode(Path.Combine(source, "one.bin")), "mode");

NativeRange files = NativeRange.Snapshot([PathValue("bytes.bin"), PathValue("text.txt")], TypeRef.Path);
await NativeHostIO.TransferAsync(NativeValue.FromRange(files), context.AsPath(NativeValue.FromString("cohort/")), context);
Check(Directory.GetFiles(Path.Combine(root, "cohort")).Length == 2, "cohort membership");
Check(File.ReadAllBytes(Path.Combine(root, "cohort/bytes.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "bytes.bin"))), "cohort bytes");
NativePlannedTransfer recordedFile = Plan(PlannedTransferKind.CopyFile, context.AsPath(PathValue("bytes.bin")),
    context.AsPath(PathValue("recorded.bin")));
await NativeHostIO.ExecutePlannedAsync(recordedFile, PathValue("bytes.bin"), context);
Check(File.ReadAllBytes(Path.Combine(root, "recorded.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "bytes.bin"))), "recorded file");
File.WriteAllText(Path.Combine(root, "changing"), "original");
NativePlannedTransfer fixedFile = Plan(PlannedTransferKind.CopyFile, context.AsPath(PathValue("changing")),
    context.AsPath(PathValue("fixed.bin")));
await NativeHostIO.ExecutePlannedAsync(fixedFile, PathValue("changing"), context);
File.Delete(Path.Combine(root, "changing"));
Directory.CreateDirectory(Path.Combine(root, "changing"));
File.WriteAllText(Path.Combine(root, "changing/child"), "not a file");
try
{
    await NativeHostIO.ExecutePlannedAsync(fixedFile, PathValue("changing"), context);
    throw new Exception("recorded CopyFile was reinterpreted as directory copy");
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
Check(File.ReadAllText(Path.Combine(root, "fixed.bin")) == "original", "failed recorded copy preserved publication");
NativePlannedTransfer ensureDirectory = new(0, NativePlanValue.Scalar(new(NativeKind.Null)),
    context.AsPath(PathValue("ensured")), PlannedTransferKind.EnsureDirectory, [], [], [], Mode: TransferDestinationMode.Merge);
await NativeHostIO.ExecutePlannedAsync(ensureDirectory, NativeValue.Null, context);
File.SetLastWriteTimeUtc(source, fixedTime);
await NativeHostIO.ExecutePlannedAsync(Plan(PlannedTransferKind.ApplyDirectoryMetadata,
    context.AsPath(PathValue("tree")), context.AsPath(PathValue("ensured")), TransferDestinationMode.Merge), PathValue("tree"), context);
Check(Directory.GetLastWriteTimeUtc(Path.Combine(root, "ensured")) == fixedTime, "recorded directory metadata");
Directory.CreateDirectory(Path.Combine(root, "other"));
File.WriteAllText(Path.Combine(root, "other/bytes.bin"), "collision");
try
{
    await NativeHostIO.TransferAsync(NativeValue.FromRange(NativeRange.Snapshot([PathValue("bytes.bin"), PathValue("other/bytes.bin")], TypeRef.Path)),
        context.AsPath(NativeValue.FromString("must-not-exist/")), context);
    throw new Exception("collision unexpectedly succeeded");
}
catch (MsyncException error) when (error.Message.Contains("collide", StringComparison.Ordinal)) { }
Check(!Directory.Exists(Path.Combine(root, "must-not-exist")), "collision preflight has no writes");
NativeRange deferredSelection = NativeHostIO.SelectPath(
    new("/unused", Connection: new(ConnectionKind.Ssh, "must-not-authenticate.invalid")), ".*", context);
Check(deferredSelection.Selection?.Directory.Connection?.Address == "must-not-authenticate.invalid", "selection creation is metadata only");
await NativeHostIO.TransferAsync(NativeValue.FromRange(NativeRange.Snapshot([], TypeRef.Path)),
    new("/unused", Connection: new(ConnectionKind.Ssh, "must-not-authenticate.invalid")), context);
NativeRuntimeContext stdio = new(new ExecutionOptions(WorkingDirectory: root) { Input = new StringReader("a\nb\nc\n") });
NativeRange inputLines = (await CallWith(stdio, "io.stdin_lines")).AsRange();
await using (IAsyncEnumerator<NativeValue> lines = inputLines.Items.GetAsyncEnumerator())
{
    Check(await lines.MoveNextAsync() && lines.Current.AsString() == "a", "deferred input first line");
}
Check((await CallWith(stdio, "io.read_line")).AsString() == "b", "shared input cursor");
Check((await CallWith(stdio, "io.read_stdin")).AsString() == "c\n", "shared input remainder");
NativeValue remoteLexical = await Call("fs.filename", NativeValue.FromPath(new("a\\b.txt", Connection: new(ConnectionKind.Ssh, "metadata-only"))));
Check(remoteLexical.AsPath().Raw == "a\\b.txt", "POSIX backslash provenance");

NativeRuntimeContext captured = context.CaptureDirectory();
Directory.CreateDirectory(Path.Combine(root, "later"));
context.ChangeWorkingDirectory(PathValue("later"));
await captured.TransferAsync!(NativeValue.FromString("captured"), captured.AsPath(NativeValue.FromString("anchor.txt")), captured, default);
Check(File.ReadAllText(Path.Combine(root, "anchor.txt")) == "captured" && !File.Exists(Path.Combine(root, "later/anchor.txt")), "captured cwd hook");

NativeProcess? observed = null;
context.ProcessCreated = process => observed = process;
NativeProcess child = await NativeHostIO.StartProcessAsync(Executable(), ChildArgs("--child-echo"), context, true, default);
Check(ReferenceEquals(child, observed), "selected process ownership callback");
await CallWith(context, "process.write_stdin", NativeValue.FromProcess(child), NativeValue.FromString("input 星🙂"));
await CallWith(context, "process.close_stdin", NativeValue.FromProcess(child));
NativeProcess completed = (await CallWith(context, "process.wait", NativeValue.FromProcess(child))).AsProcess();
Check(completed.ExitCode == 0 && completed.StandardOutput == $"{child.ProcessId}:input 星🙂", "local controlled process");
Console.WriteLine("PASS native host IO: binary/text, tree metadata, finite cohorts, collision, cwd capture, selected process and input");

async Task Remote(string name)
{
    NativeConnection connection = new(ConnectionKind.Ssh, "Perf_Fixture");
    NativePathValue RemotePath(string path) => new(Path.Combine(root, path), Connection: connection);
    File.WriteAllBytes(Path.Combine(root, "input.bin"), [0, 10, 128, 255]);
    switch (name)
    {
        case "upload":
            await NativeHostIO.TransferAsync(PathValue("input.bin"), RemotePath("remote 星.bin"), context);
            Check(File.ReadAllBytes(Path.Combine(root, "remote 星.bin")).SequenceEqual(new byte[] { 0, 10, 128, 255 }), name);
            break;
        case "download":
            await NativeHostIO.TransferAsync(NativeValue.FromPath(RemotePath("input.bin")), context.AsPath(NativeValue.FromString("download.bin")), context);
            Check(File.ReadAllBytes(Path.Combine(root, "download.bin")).SequenceEqual(new byte[] { 0, 10, 128, 255 }), name);
            break;
        case "cohort":
            File.WriteAllText(Path.Combine(root, "second.txt"), "second");
            await NativeHostIO.TransferAsync(NativeValue.FromRange(NativeRange.Snapshot([
                NativeValue.FromPath(RemotePath("input.bin")), NativeValue.FromPath(RemotePath("second.txt"))], TypeRef.Path)),
                RemotePath("selected/"), context);
            Check(Directory.GetFiles(Path.Combine(root, "selected")).Length == 2, name);
            break;
        case "mixed-cohort":
            File.WriteAllText(Path.Combine(root, "second.txt"), "second");
            File.WriteAllText(Path.Combine(root, "third.txt"), "third");
            await NativeHostIO.TransferAsync(NativeValue.FromRange(NativeRange.Snapshot([
                PathValue("input.bin"), NativeValue.FromPath(RemotePath("second.txt")),
                NativeValue.FromPath(RemotePath("third.txt"))], TypeRef.Path)),
                context.AsPath(NativeValue.FromString("selected/")), context);
            Check(Directory.GetFiles(Path.Combine(root, "selected")).Length == 3, name);
            break;
        case "selector":
            Directory.CreateDirectory(Path.Combine(root, "selection"));
            File.WriteAllBytes(Path.Combine(root, "selection/one.bin"), [1, 2]);
            File.WriteAllBytes(Path.Combine(root, "selection/two.bin"), [3, 4, 5]);
            File.WriteAllText(Path.Combine(root, "selection/ignore.txt"), "no");
            NativeRange range = new(Unexpected(), TypeRef.Path,
                selection: new(RemotePath("selection"), @".*\.bin", Apply: FilterSelected));
            await NativeHostIO.TransferAsync(NativeValue.FromRange(range), context.AsPath(NativeValue.FromString("selected/")), context);
            Check(Directory.GetFiles(Path.Combine(root, "selected")).Length == 1
                && File.ReadAllBytes(Path.Combine(root, "selected/two.bin")).SequenceEqual(new byte[] { 3, 4, 5 }), name);
            break;
        case "process":
            NativeProcess process = await NativeHostIO.StartProcessAsync(Executable() with { Connection = connection }, ChildArgs("--child-echo"), context, true, default);
            await Call("process.write_stdin", NativeValue.FromProcess(process), NativeValue.FromString("remote 星🙂"));
            await Call("process.close_stdin", NativeValue.FromProcess(process));
            NativeProcess result = (await Call("process.wait", NativeValue.FromProcess(process))).AsProcess();
            Check(result.ExitCode == 0 && result.StandardOutput == $"{process.ProcessId}:remote 星🙂", name);
            break;
        case "bytes":
            await Call("io.write_bytes", NativeValue.FromPath(RemotePath("roundtrip.bin")), NativeValue.FromRange(NativeRange.Snapshot([
                NativeValue.FromNumber(0), NativeValue.FromNumber(255), NativeValue.FromNumber(10)], TypeRef.Number)));
            List<byte> actual = [];
            await foreach (NativeValue item in (await Call("io.read_bytes", NativeValue.FromPath(RemotePath("roundtrip.bin")))).AsRange().Items)
                actual.Add((byte)item.AsNumber());
            Check(actual.SequenceEqual(new byte[] { 0, 255, 10 }), name);
            break;
        case "planned-upload":
            NativePlannedTransfer plannedUpload = Plan(PlannedTransferKind.CopyFile, context.AsPath(PathValue("input.bin")), RemotePath("recorded.bin"));
            await NativeHostIO.ExecutePlannedAsync(plannedUpload, PathValue("input.bin"), context);
            Check(File.ReadAllBytes(Path.Combine(root, "recorded.bin")).SequenceEqual(new byte[] { 0, 10, 128, 255 }), name);
            break;
        case "planned-download":
            NativePlannedTransfer plannedDownload = Plan(PlannedTransferKind.CopyFile, RemotePath("input.bin"), context.AsPath(PathValue("recorded.bin")));
            await NativeHostIO.ExecutePlannedAsync(plannedDownload, NativeValue.FromPath(RemotePath("input.bin")), context);
            Check(File.ReadAllBytes(Path.Combine(root, "recorded.bin")).SequenceEqual(new byte[] { 0, 10, 128, 255 }), name);
            break;
        case "planned-merge":
            Directory.CreateDirectory(Path.Combine(root, "source-dir"));
            Directory.CreateDirectory(Path.Combine(root, "target-dir"));
            File.WriteAllText(Path.Combine(root, "source-dir/new"), "new");
            File.WriteAllText(Path.Combine(root, "target-dir/retained"), "retained");
            NativePlannedTransfer plannedMerge = Plan(PlannedTransferKind.CopyDirectory, RemotePath("source-dir"), RemotePath("target-dir"), TransferDestinationMode.Merge);
            await NativeHostIO.ExecutePlannedAsync(plannedMerge, NativeValue.FromPath(RemotePath("source-dir")), context);
            Check(File.ReadAllText(Path.Combine(root, "target-dir/new")) == "new" && File.ReadAllText(Path.Combine(root, "target-dir/retained")) == "retained", name);
            break;
        case "planned-changed-kind":
            NativePlannedTransfer fixedRemoteFile = Plan(PlannedTransferKind.CopyFile, RemotePath("input.bin"), context.AsPath(PathValue("recorded.bin")));
            await NativeHostIO.ExecutePlannedAsync(fixedRemoteFile, NativeValue.FromPath(RemotePath("input.bin")), context);
            File.Delete(Path.Combine(root, "input.bin"));
            Directory.CreateDirectory(Path.Combine(root, "input.bin"));
            try
            {
                await NativeHostIO.ExecutePlannedAsync(fixedRemoteFile, NativeValue.FromPath(RemotePath("input.bin")), context);
                throw new Exception("remote recorded kind was reinterpreted");
            }
            catch (IOException error) when (error.Message.Contains("CopyFile", StringComparison.Ordinal)) { }
            Check(File.ReadAllBytes(Path.Combine(root, "recorded.bin")).SequenceEqual(new byte[] { 0, 10, 128, 255 }), name);
            break;
        case "planned-directory-metadata":
            Directory.CreateDirectory(Path.Combine(root, "source-dir"));
            Directory.CreateDirectory(Path.Combine(root, "target-dir"));
            DateTime stamp = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(Path.Combine(root, "source-dir"), stamp);
            await NativeHostIO.ExecutePlannedAsync(Plan(PlannedTransferKind.ApplyDirectoryMetadata, RemotePath("source-dir"), RemotePath("target-dir"), TransferDestinationMode.Merge),
                NativeValue.FromPath(RemotePath("source-dir")), context);
            Check(Directory.GetLastWriteTimeUtc(Path.Combine(root, "target-dir")) == stamp, name);
            break;
        case "error":
            try
            {
                await NativeHostIO.RunCommandAsync(connection, "printf 'specific native cause' >&2; exit 37", [], context);
                throw new Exception("remote failure was lost");
            }
            catch (IOException error) when (error.Message.Contains("specific native cause", StringComparison.Ordinal) && error.Message.Contains("37", StringComparison.Ordinal)) { }
            break;
        default: throw new ArgumentException(name);
    }
}

async IAsyncEnumerable<NativeValue> FilterSelected(IAsyncEnumerable<NativeValue> source, [EnumeratorCancellation] CancellationToken token)
{
    await foreach (NativeValue path in source.WithCancellation(token))
        if ((await Call("io.file_size", path)).AsNumber() > 2) yield return path;
}
static async IAsyncEnumerable<NativeValue> Unexpected()
{
    yield return await Task.FromException<NativeValue>(new Exception("remote selection enumerated outside its native channel"));
}
NativeValue PathValue(string raw) => NativeValue.FromPath(new(raw, root));
ValueTask<NativeValue> Call(string name, params NativeValue[] values) => CallWith(context, name, values);
static ValueTask<NativeValue> CallWith(NativeRuntimeContext context, string name, params NativeValue[] values) =>
    NativeBuiltins.InvokeAsync(name, values, context, static (_, _, _) => throw new Exception("unexpected source callback"));
static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
static NativePlannedTransfer Plan(PlannedTransferKind kind, NativePathValue input, NativePathValue output,
    TransferDestinationMode mode = TransferDestinationMode.Exact) =>
    new(0, NativePlanValue.FromPath(input), output, kind, [], [], [], Mode: mode);
static NativePathValue Executable() => new(Environment.ProcessPath ?? throw new Exception("missing process path"));
static NativeValue[] ChildArgs(string mode) => Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet"
    ? [NativeValue.FromString(Assembly.GetExecutingAssembly().Location), NativeValue.FromString(mode)]
    : [NativeValue.FromString(mode)];
static string FindRoot()
{
    DirectoryInfo? current = new(Environment.CurrentDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "msync.slnx"))) current = current.Parent;
    return current?.FullName ?? throw new Exception("run from repository");
}
