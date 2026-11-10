using Msync.Core;
using Msync.Runtime;

if (args.Contains("--batch-only", StringComparer.Ordinal)) { await BatchChecks.RunAsync(); return; }
if (OperatingSystem.IsWindows()) return;
string root = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_WORK") ?? Path.GetFullPath(Path.Combine(".temp", "transfer-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Directory.CreateDirectory(Path.Combine(root, "remote"));
Directory.CreateDirectory(Path.Combine(root, "remote-home"));

RuntimeContext context = new(new ExecutionOptions(WorkingDirectory: root));
string? remoteAlias = Environment.GetEnvironmentVariable("MSYNC_TRANSFER_REMOTE");
ConnectionValue remote = new(ConnectionKind.Ssh, remoteAlias ?? "unused");
PathValue P(string raw) => new(raw, root);
PathValue R(string raw) => new(Path.Combine(root, "remote", raw), Connection: remote);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Transfer(Value source, Value target) => Check(await TransferEngine.CopyAsync(source, target, context), "copy false");
await File.WriteAllTextAsync(Path.Combine(root, "source"), "hello 可莉\n");
File.SetUnixFileMode(Path.Combine(root, "source"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
await Transfer(P("source"), P("copied"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "copied")) == "hello 可莉\n", "local bytes");
Check(File.GetUnixFileMode(Path.Combine(root, "copied")) == File.GetUnixFileMode(Path.Combine(root, "source")), "local mode");
await Transfer(new StringValue("literal\n"), P("copied"));
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
await Transfer(new RangeValue([P("source"), P("copied")], TypeRef.Path), P("cohort-replace/."));
Check(!File.Exists(Path.Combine(root, "cohort-replace/old")) && File.Exists(Path.Combine(root, "cohort-replace/source")), "finite local cohort replaces selected tree");
await Transfer(new RangeValue([P("tree")], TypeRef.Path), P("tree/selected-cohort/."));
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
using (FileSystemWatcher watcher = new(root, ".msync-*") { EnableRaisingEvents = true })
{
    TaskCompletionSource stageCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    watcher.Created += (_, _) => stageCreated.TrySetResult();
    Task<bool> copying = TransferEngine.CopyAsync(P("cancel-source"), P("cancel-target"), context, cancellation.Token);
    Task first = await Task.WhenAny(stageCreated.Task, copying);
    if (first == copying) await copying;
    Check(first == stageCreated.Task, "large-file cancellation observes actual owned stage before completion");
    cancellation.Cancel();
    try { await copying; throw new Exception("cancelled copy must not publish"); }
    catch (OperationCanceledException) { }
}
Check(await File.ReadAllTextAsync(Path.Combine(root, "cancel-target")) == "previous target", "cancellation retains previous target bytes");
Check(!Directory.EnumerateFileSystemEntries(root, ".msync-*").Any(), "cancellation removes only its owned stage");
File.Delete(Path.Combine(root, "cancel-source"));
Console.WriteLine("Local transfers passed: bytes, modes, literal replacement, dangling links, self, descendant and ancestor overlap, actual mid-copy cancellation.");
if (remoteAlias is null) return;
await Transfer(P("source"), R("out"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/out")) == "hello 可莉\n", "upload bytes");
await Transfer(new StringValue("changed"), R("out"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/out")) == "changed", "remote literal");
await Transfer(P("tree"), R("trees/"));
Check(new FileInfo(Path.Combine(root, "remote/trees/tree/dangling")).LinkTarget == "missing", "remote dangling");
await Transfer(R("trees/tree"), P("downloaded/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "downloaded/tree/file")) == "tree", "download tree");
Check(new FileInfo(Path.Combine(root, "downloaded/tree/dangling")).LinkTarget == "missing", "download dangling");
await Transfer(R("out"), R("remote-copy"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/remote-copy")) == "changed", "remote copy");
await Transfer(new RangeValue(new Value[] { P("source"), P("tree") }, TypeRef.Path), R("cohort/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/cohort/source")) == "hello 可莉\n", "cohort file");
Check(await File.ReadAllTextAsync(Path.Combine(root, "remote/cohort/tree/file")) == "tree", "cohort tree");
await Transfer(new RangeValue(new Value[] { R("cohort/source"), R("cohort/tree") }, TypeRef.Path), P("range-download/"));
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
async IAsyncEnumerable<Value> MustNotEnumerate()
{
    yield return await Task.FromException<Value>(new Exception("Selector transfer must not enumerate its fallback source and authenticate twice."));
}
RangeValue Selected(string pattern) => new(MustNotEnumerate(), TypeRef.Path, new PathSelection(R("selection"), pattern));
const string selectedPattern = "(?s)^(?!skip).+\\.pick$";
Directory.CreateDirectory(Path.Combine(root, "selected"));
await File.WriteAllTextAsync(Path.Combine(root, "selected/keep"), "unrelated");
await Transfer(Selected(selectedPattern), P("selected/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "selected/a.pick")) == "a.pick", "one-channel selector download");
Check(await File.ReadAllTextAsync(Path.Combine(root, "selected/line\n.pick")) == "line\n.pick", "selector preserves newline names");
Check(!File.Exists(Path.Combine(root, "selected/skip.pick")), "selector uses unchanged .NET negative-lookahead regex");
Check(File.Exists(Path.Combine(root, "selected/keep")), "selector merge retains unrelated data");
await Transfer(Selected(selectedPattern), R("selected-target/"));
Check(File.Exists(Path.Combine(root, "remote/selected-target/a.pick")), "same-channel selector same-endpoint copy");
await Transfer(Selected("^no-match$"), P("no-match/"));
Check(!Directory.Exists(Path.Combine(root, "no-match")), "empty selector does not create destination");
if (Environment.GetEnvironmentVariable("MSYNC_TRANSFER_SECOND_REMOTE") is { } secondAlias)
{
    ConnectionValue second = new(ConnectionKind.Ssh, secondAlias);
    await Transfer(Selected("^no-match$"), new PathValue(Path.Combine(root, "remote/no-match"), Connection: second));
    Check(!Directory.Exists(Path.Combine(root, "remote/no-match")), "empty selector does not authenticate unrelated destination");
    await Transfer(Selected(selectedPattern), new PathValue(Path.Combine(root, "remote/relayed/"), Connection: second));
    Check(File.Exists(Path.Combine(root, "remote/relayed/b.pick")), "selector relay uses one channel per actual endpoint");
}
Directory.CreateDirectory(Path.Combine(root, "selected-replace"));
await File.WriteAllTextAsync(Path.Combine(root, "selected-replace/obsolete"), "old");
await Transfer(Selected(selectedPattern), P("selected-replace/."));
Check(!File.Exists(Path.Combine(root, "selected-replace/obsolete")) && File.Exists(Path.Combine(root, "selected-replace/a.pick")), "selector local tree replacement");
Directory.CreateDirectory(Path.Combine(root, "remote/selected-replace"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/selected-replace/obsolete"), "old");
await Transfer(Selected(selectedPattern), R("selected-replace/."));
Check(!File.Exists(Path.Combine(root, "remote/selected-replace/obsolete")) && File.Exists(Path.Combine(root, "remote/selected-replace/a.pick")), "selector remote tree replacement");
Directory.CreateDirectory(Path.Combine(root, "remote/selection/dir.pick"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/selection/dir.pick/child"), "inside");
async ValueTask<Value> InvokeNative(Value function, IReadOnlyList<Value> arguments, CancellationToken token)
{
    string name = ((FunctionValue)function).NativeName!;
    if (name == "identity") return arguments[0];
    if (name == "retarget") return ((PathValue)arguments[0]) with { Raw = Path.Combine(root, "remote/transformed/a.pick") };
    if (name == "to-local") return P("source");
    if (name == "metadata-check")
    {
        Value exists = await NativeRegistry.InvokeAsync("fs.exists", arguments, context, InvokeNative, token);
        Check(exists is BoolValue { Data: true }, "predicate metadata remains observable on the admitted source channel");
        return await NativeRegistry.InvokeAsync("fs.is_regular_file", arguments, context, InvokeNative, token);
    }
    return await NativeRegistry.InvokeAsync(name, arguments, context, InvokeNative, token);
}
async Task<RangeValue> Adapt(string name, params Value[] arguments) =>
    (RangeValue)await NativeRegistry.InvokeAsync("ranges." + name, arguments, context, InvokeNative);
int resolutions = 0;
RangeValue deferred = new(MustNotEnumerate(), TypeRef.Path, Resolve: _ => { ++resolutions; return ValueTask.FromResult(Selected(selectedPattern)); });
RangeValue filtered = await Adapt("filter", deferred, new FunctionValue(-1, NativeName: "metadata-check"));
RangeValue reversed = await Adapt("reverse", filtered);
RangeValue limited = await Adapt("take", reversed, new NumberValue(2));
RangeValue projected = await Adapt("transform", limited, new FunctionValue(-1, NativeName: "identity"));
await Transfer(projected, P("query-selected/"));
Check(resolutions == 1, "deferred source is resolved exactly once");
Check(File.Exists(Path.Combine(root, "query-selected/b.pick")) && File.Exists(Path.Combine(root, "query-selected/line\n.pick")), "deferred callback chain keeps discovery order and selected files");
Check(!Path.Exists(Path.Combine(root, "query-selected/dir.pick")) && !File.Exists(Path.Combine(root, "query-selected/a.pick")), "metadata filter and reverse/take apply before archive bytes");
RangeValue never = new(MustNotEnumerate(), TypeRef.Path, Resolve: _ => ValueTask.FromException<RangeValue>(new Exception("take(0) must not resolve its source")));
await Transfer(await Adapt("take", never, new NumberValue(0)), P("never-demanded/"));
Check(!Directory.Exists(Path.Combine(root, "never-demanded")), "take(0) neither authenticates nor creates destination");
Directory.CreateDirectory(Path.Combine(root, "remote/transformed"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/transformed/a.pick"), "transformed bytes");
await Transfer(await Adapt("transform", Selected("^a\\.pick$"), new FunctionValue(-1, NativeName: "retarget")), P("transformed-download/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "transformed-download/a.pick")) == "transformed bytes", "same-endpoint transform can select another directory");
await Transfer(await Adapt("transform", Selected("^a\\.pick$"), new FunctionValue(-1, NativeName: "to-local")), P("transformed-local/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "transformed-local/source")) == "hello 可莉\n", "endpoint-changing transform dispatches actual resulting paths");
Directory.CreateDirectory(Path.Combine(root, "remote/recursive/child"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/recursive/root.pick"), "root");
await File.WriteAllTextAsync(Path.Combine(root, "remote/recursive/child/deep.pick"), "deep");
RangeValue recursive = (RangeValue)await NativeRegistry.InvokeAsync("fs.recursive_directory_iterator", [R("recursive")], context, InvokeNative);
await Transfer(await Adapt("filter", recursive, new FunctionValue(-1, NativeName: "fs.is_regular_file")), P("recursive-selected/"));
Check(await File.ReadAllTextAsync(Path.Combine(root, "recursive-selected/root.pick")) == "root" && await File.ReadAllTextAsync(Path.Combine(root, "recursive-selected/deep.pick")) == "deep", "recursive iterator preserves source origin through metadata filter in one channel");
Directory.CreateDirectory(Path.Combine(root, "remote/collision/a"));
Directory.CreateDirectory(Path.Combine(root, "remote/collision/b"));
await File.WriteAllTextAsync(Path.Combine(root, "remote/collision/a/same"), "first");
await File.WriteAllTextAsync(Path.Combine(root, "remote/collision/b/same"), "second");
RangeValue collisions = (RangeValue)await NativeRegistry.InvokeAsync("fs.recursive_directory_iterator", [R("collision")], context, InvokeNative);
try
{
    await Transfer(await Adapt("filter", collisions, new FunctionValue(-1, NativeName: "fs.is_regular_file")), P("collision-target/"));
    throw new Exception("recursive cohort duplicate basenames must be diagnosed before writes");
}
catch (IOException error) when (error.Message.Contains("collide", StringComparison.Ordinal)) { }
Check(!Directory.Exists(Path.Combine(root, "collision-target")), "collision preflight leaves destination absent");
Console.WriteLine("Remote transfers passed: real SSH archive upload/download, same-endpoint, cohorts, trailing-newline filenames, failure and retry (28 channel uses).");
