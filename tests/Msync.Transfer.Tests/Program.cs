using Msync.Core;
using Msync.Runtime;
using Msync.Core.Hpir;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

if (args.FirstOrDefault() == "--cohort") { await FiniteCohortChecks.RunAsync(args[1]); return; }

if (args.Contains("--batch-only", StringComparer.Ordinal)) { await BatchChecks.RunAsync(); return; }
if (args.Contains("--relay-batch-only", StringComparer.Ordinal)) { await RelayBatchChecks.RunAsync(); return; }
if (args.Contains("--node-kind-only", StringComparer.Ordinal)) { await NodeKindChecks.RunAsync(); return; }
if (OperatingSystem.IsWindows()) return;
string root = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_WORK") ?? Path.GetFullPath(Path.Combine(".temp", "transfer-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Directory.CreateDirectory(Path.Combine(root, "remote"));
Directory.CreateDirectory(Path.Combine(root, "remote-home"));

NativeRuntimeContext context = new(new ExecutionOptions(WorkingDirectory: root));
string? remoteAlias = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_REMOTE");
NativeConnection remote = new(ConnectionKind.Ssh, remoteAlias ?? "unused");
NativePathValue P(string raw) => new(raw, root);
NativePathValue R(string raw) => new(Path.Combine(root, "remote", raw), Connection: remote);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Transfer(NativePathValue source, NativePathValue target) =>
    Check(await NativeHostIO.TransferPathAsync(source, target, context, default), "copy false");
async Task TransferText(string text, NativePathValue target) =>
    Check(await NativeHostIO.TransferTextAsync(text, target, context, default), "literal copy false");
Task TransferCohort(NativePathValue[] paths, NativePathValue target) =>
    NativeHostIO.TransferPathBatchAsync(paths, target, context, default).AsTask();
await File.WriteAllTextAsync(Path.Combine(root, "source"), "hello 可莉\n");
File.SetUnixFileMode(Path.Combine(root, "source"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
await Transfer(P("source"), P("copied"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "copied")) == "hello 可莉\n", "local bytes");
Check(File.GetUnixFileMode(Path.Combine(root, "copied")) == File.GetUnixFileMode(Path.Combine(root, "source")), "local mode");
await TransferText("literal\n", P("copied"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "copied")) == "literal\n", "literal bytes");
Check(File.GetUnixFileMode(Path.Combine(root, "copied")) == File.GetUnixFileMode(Path.Combine(root, "source")), "literal preserves mode");
Directory.CreateDirectory(Path.Combine(root, "tree"));
await File.WriteAllTextAsync(Path.Combine(root, "tree", "file"), "tree");
File.CreateSymbolicLink(Path.Combine(root, "tree", "link"), "file");
File.CreateSymbolicLink(Path.Combine(root, "tree", "dangling"), "missing");
await Transfer(P("tree"), P("local-target/"));
Check(new FileInfo(Path.Combine(root, "local-target/tree/dangling")).LinkTarget == "missing", "local dangling");
await Transfer(P("tree"), P("tree/."));
Check(await File.ReadAllTextAsync(Path.Combine(root, "tree/file")) == "tree", "self tree replacement retains contents");
await Transfer(P("tree"), P("tree/descendant/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "tree/descendant/tree/file")) == "tree", "descendant copy uses finite input snapshot");
Directory.CreateDirectory(Path.Combine(root, "ancestor/child"));
await File.WriteAllTextAsync(Path.Combine(root, "ancestor/child/file"), "survives");
await Transfer(P("ancestor/child"), P("ancestor/."));
Check(await File.ReadAllTextAsync(Path.Combine(root, "ancestor/file")) == "survives", "ancestor replacement retains source before removal");
Directory.CreateDirectory(Path.Combine(root, "cohort-replace"));
await File.WriteAllTextAsync(Path.Combine(root, "cohort-replace/old"), "obsolete");
await TransferCohort([P("source"), P("copied")], P("cohort-replace/."));
Check(!File.Exists(Path.Combine(root, "cohort-replace/old")) && File.Exists(Path.Combine(root, "cohort-replace/source")), "finite local cohort replaces selected tree");
await TransferCohort([P("tree")], P("tree/selected-cohort/."));
Check(File.Exists(Path.Combine(root, "tree/selected-cohort/tree/file")), "overlapping finite cohort retains input bytes");
Check(!Directory.Exists(Path.Combine(root, "tree/selected-cohort/tree/selected-cohort")), "overlapping cohort does not discover its new output or owned scratch");
File.CreateSymbolicLink(Path.Combine(root, "source-alias"), "source");
await Transfer(P("source"), P("source-alias"));
Check(new FileInfo(Path.Combine(root, "source-alias")).LinkTarget is null, "same-target source alias is replaced as an entry rather than mistaken for a hardlink no-op");
Directory.CreateDirectory(Path.Combine(root, "tree/alias-child"));
Directory.CreateSymbolicLink(Path.Combine(root, "directory-alias"), "tree/alias-child");
await Transfer(P("tree"), P("directory-alias/nested/"));
Check(File.Exists(Path.Combine(root, "tree/alias-child/nested/tree/file")), "physical overlap follows native parent-directory symlinks");
Check(!Directory.Exists(Path.Combine(root, "tree/alias-child/nested/tree/alias-child/nested")), "symlink-parent descendant copy retains original input only");
await File.WriteAllTextAsync(Path.Combine(root, "cancel-target"), "previous target");
using (FileStream large = File.Create(Path.Combine(root, "cancel-source"))) large.SetLength(256L * 1024 * 1024);
using (CancellationTokenSource cancellation = new())
using (ManualResetEventSlim finished = new())
{
    TaskCompletionSource stageCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Buffered copying and watcher callbacks can share a one-thread pool. Observe
    // the real stage independently so callback starvation cannot fake a fast copy.
    Thread observer = new(() =>
    {
        while (!finished.IsSet)
        {
            if (Directory.EnumerateFileSystemEntries(root, ".msync-*").Any())
            {
                stageCreated.TrySetResult();
                cancellation.Cancel();
                return;
            }
            Thread.Sleep(1);
        }
    });
    observer.Start();
    try
    {
        Task<bool> copying = NativeHostIO.TransferPathAsync(P("cancel-source"), P("cancel-target"), context, cancellation.Token).AsTask();
        Task first = await Task.WhenAny(stageCreated.Task, copying);
        if (first == copying) await copying;
        Check(first == stageCreated.Task, "large-file cancellation observes actual owned stage before completion");
        try { await copying; throw new Exception("cancelled copy must not publish"); }
        catch (OperationCanceledException) { }
    }
    finally { finished.Set(); observer.Join(); }
}
Check(await File.ReadAllTextAsync(Path.Combine(root, "cancel-target")) == "previous target", "cancellation retains previous target bytes");
Check(!Directory.EnumerateFileSystemEntries(root, ".msync-*").Any(), "cancellation removes only its owned stage");
File.Delete(Path.Combine(root, "cancel-source"));
Console.WriteLine("Local transfers passed: bytes, modes, literal replacement, dangling links, self, descendant and ancestor overlap, actual mid-copy cancellation.");
if (remoteAlias is null) return;
await Transfer(P("source"), R("out"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/out")) == "hello 可莉\n", "upload bytes");
await TransferText("changed", R("out"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/out")) == "changed", "remote literal");
await Transfer(P("tree"), R("trees/"));
Check(new FileInfo(Path.Combine(root, "remote/trees/tree/dangling")).LinkTarget == "missing", "remote dangling");
await Transfer(R("trees/tree"), P("downloaded/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "downloaded/tree/file")) == "tree", "download tree");
Check(new FileInfo(Path.Combine(root, "downloaded/tree/dangling")).LinkTarget == "missing", "download dangling");
await Transfer(R("out"), R("remote-copy"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/remote-copy")) == "changed", "remote copy");
await TransferCohort([P("source"), P("tree")], R("cohort/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/cohort/source")) == "hello 可莉\n", "cohort file");
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/cohort/tree/file")) == "tree", "cohort tree");
await TransferCohort([R("cohort/source"), R("cohort/tree")], P("range-download/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "range-download/tree/file")) == "tree", "cohort download");
await Transfer(P("tree"), R("replacement/."));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/replacement/file")) == "tree", "remote upload tree replacement");
await Transfer(R("replacement"), R("replacement/."));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/replacement/file")) == "tree", "remote self replacement preserves tree");
await Transfer(R("replacement"), R("replacement/new-descendant/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/replacement/new-descendant/replacement/file")) == "tree", "remote descendant copy retains finite input");
Check(!Directory.Exists(Path.Combine(root, "remote/replacement/new-descendant/replacement/new-descendant")), "remote descendant copy does not discover its new output");
Directory.CreateDirectory(Path.Combine(root, "remote/ancestor/child"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/ancestor/child/file"), "remote survives");
await Transfer(R("ancestor/child"), R("ancestor/."));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/ancestor/file")) == "remote survives", "remote ancestor replacement captures source before removal");
string unusual = "line\nslash\\ 可莉'%0A%25\n";
await File.WriteAllTextAsync(Path.Combine(root, unusual), "unusual filename");
await Transfer(P(unusual), R(unusual));
await Transfer(R(unusual), P("unusual-download"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "unusual-download")) == "unusual filename", "trailing newline path round trip");
await File.WriteAllTextAsync(Path.Combine(root, "remote/blocked"), "preserve");
try { await Transfer(P("source"), R("blocked/child")); throw new Exception("blocked parent must fail"); }
catch (IOException) { }
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/blocked")) == "preserve", "failed parent preserves old bytes");
File.Delete(Path.Combine(root, "remote/blocked"));
await Transfer(P("source"), R("blocked/child"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/blocked/child")) == "hello 可莉\n", "ordinary remote retry");
Directory.CreateDirectory(Path.Combine(root, "remote/selection"));
foreach (string name in new[] { "a.pick", "b.pick", "skip.pick", "line\n.pick" })
    await File.WriteAllTextAsync(Path.Combine(root, "remote/selection", name), name);
async Task TransferSelected(string pattern, NativePathValue target) =>
    Check(await NativeHostIO.TransferSelectedPathsAsync(R("selection"), pattern, target, context, default), "selector returned false");
const string selectedPattern = "(?s)^(?!skip).+\\.pick$";
Directory.CreateDirectory(Path.Combine(root, "selected"));
await File.WriteAllTextAsync(Path.Combine(root, "selected/keep"), "unrelated");
await TransferSelected(selectedPattern, P("selected/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "selected/a.pick")) == "a.pick", "one-channel selector download");
Check(await File.ReadAllTextAsync(Path.Combine(root, "selected/line\n.pick")) == "line\n.pick", "selector preserves newline names");
Check(!File.Exists(Path.Combine(root, "selected/skip.pick")), "selector uses unchanged .NET negative-lookahead regex");
Check(File.Exists(Path.Combine(root, "selected/keep")), "selector merge retains unrelated data");
await TransferSelected(selectedPattern, R("selected-target/"));
Check(File.Exists(Path.Combine(root, "remote/selected-target/a.pick")), "same-channel selector same-endpoint copy");
await TransferSelected("^no-match$", P("no-match/"));
Check(!Directory.Exists(Path.Combine(root, "no-match")), "empty selector does not create destination");
if (Environment.GetEnvironmentVariable("MSYNC_TRANSFER_SECOND_REMOTE") is { } secondAlias)
{
    NativeConnection second = new(ConnectionKind.Ssh, secondAlias);
    await TransferSelected("^no-match$", new(Path.Combine(root, "remote/no-match"), Connection: second));
    Check(!Directory.Exists(Path.Combine(root, "remote/no-match")), "empty selector does not authenticate unrelated destination");
    await TransferSelected(selectedPattern, new(Path.Combine(root, "remote/relayed/"), Connection: second));
    Check(File.Exists(Path.Combine(root, "remote/relayed/b.pick")), "selector relay uses one channel per actual endpoint");
}
Directory.CreateDirectory(Path.Combine(root, "selected-replace"));
await File.WriteAllTextAsync(Path.Combine(root, "selected-replace/obsolete"), "old");
await TransferSelected(selectedPattern, P("selected-replace/."));
Check(!File.Exists(Path.Combine(root, "selected-replace/obsolete")) && File.Exists(Path.Combine(root, "selected-replace/a.pick")), "selector local tree replacement");
Directory.CreateDirectory(Path.Combine(root, "remote/selected-replace"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/selected-replace/obsolete"), "old");
await TransferSelected(selectedPattern, R("selected-replace/."));
Check(!File.Exists(Path.Combine(root, "remote/selected-replace/obsolete")) && File.Exists(Path.Combine(root, "remote/selected-replace/a.pick")), "selector remote tree replacement");
async Task<NativePathValue[]> RegularFiles(NativePathValue directory)
{
    List<NativePathValue> selected = [];
    await foreach (var item in NativeBuiltins.OpenDirectory(directory, true, context, default))
        if (await NativeBuiltins.ReadFilePredicate(HpirFileMetadata.IsRegularFile, item, context, default) != 0)
            selected.Add(item);
    return selected.ToArray();
}
Directory.CreateDirectory(Path.Combine(root, "remote/recursive/child"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/recursive/root.pick"), "root");
await File.WriteAllTextAsync(Path.Combine(root, "remote/recursive/child/deep.pick"), "deep");
await TransferCohort(await RegularFiles(R("recursive")), P("recursive-selected/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "recursive-selected/root.pick")) == "root" && await File.ReadAllTextAsync(Path.Combine(root, "recursive-selected/deep.pick")) == "deep", "recursive iterator preserves source origin through the typed file predicate");
Directory.CreateDirectory(Path.Combine(root, "remote/collision/a"));
Directory.CreateDirectory(Path.Combine(root, "remote/collision/b"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/collision/a/same"), "first");
await File.WriteAllTextAsync(Path.Combine(root, "remote/collision/b/same"), "second");
try
{
    await TransferCohort(await RegularFiles(R("collision")), P("collision-target/"));
    throw new Exception("recursive cohort duplicate basenames must be diagnosed before writes");
}
catch (MsyncException error) when (error.Message.Contains("collide", StringComparison.Ordinal)) { }
Check(!Directory.Exists(Path.Combine(root, "collision-target")), "collision preflight leaves destination absent");
Console.WriteLine("Remote transfers passed: real SSH archive upload/download, same-endpoint, cohorts, trailing-newline filenames, failure and retry.");
