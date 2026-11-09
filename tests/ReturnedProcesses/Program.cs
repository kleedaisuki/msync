using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.FirstOrDefault() == "--no-io-baseline")
{
    await AllocationCheck.Run(args[1]);
    return;
}
if (args.FirstOrDefault() == "--child")
{
    Console.Write("echo:" + await Console.In.ReadToEndAsync());
    return;
}
if (args.FirstOrDefault() == "--done-child")
{
    await File.WriteAllTextAsync(args[1], "joined");
    return;
}
if (args.FirstOrDefault() == "--file-child")
{
    await File.WriteAllTextAsync(args[1], "ready");
    await WaitForFile(args[2], CancellationToken.None);
    await File.WriteAllTextAsync(args[3], "joined");
    return;
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "returned-processes-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string executable = Environment.ProcessPath!.Replace('\\', '/');
int checks = 0;
try
{
    foreach (int level in args.Any(argument => argument is "--session-only" or "--flow-failure-only") ? [] : new[] { 0, 2, 3 })
    {
        string prelude = $$"""
            fn start() {
                $std::process::spawn("{{executable}}", ["--child"])
            };
            """;
        string[] programs =
        [
            prelude + """
                let child = start();
                $std::process::write_stdin(child, "direct");
                $std::process::close_stdin(child);
                $std::process::stdout($std::process::wait(child))
                """,
            prelude + """
                fn box() { scope { child: start() } };
                let boxed = box();
                $std::process::write_stdin(boxed::child, "scope");
                $std::process::close_stdin(boxed::child);
                $std::process::stdout($std::process::wait(boxed::child))
                """,
            prelude + """
                fn capture() { let child = start(); fn { child } };
                let get = capture();
                let child = get();
                $std::process::write_stdin(child, "capture");
                $std::process::close_stdin(child);
                $std::process::stdout($std::process::wait(child))
                """,
            prelude + """
                fn flow() {
                    let child = start();
                    child::stdout -> ./flow.txt;
                    child
                };
                let child = flow();
                $std::process::write_stdin(child, "flow");
                $std::process::close_stdin(child);
                acquire child;
                $std::io::read_text(./flow.txt)
                """,
            prelude + """
                fn flow_existing(child: Process) {
                    child::stdout -> ./existing.txt;
                    child
                };
                let child = flow_existing(start());
                $std::process::write_stdin(child, "existing");
                $std::process::close_stdin(child);
                acquire child;
                $std::io::read_text(./existing.txt)
                """,
            prelude + """
                fn values() {
                    let child = start();
                    $std::ranges::materialize([child])
                };
                let child = $std::ranges::first(values());
                $std::process::write_stdin(child, "materialized");
                $std::process::close_stdin(child);
                $std::process::stdout($std::process::wait(child))
                """,
            prelude + """
                fn values() {
                    let child = start();
                    $std::ranges::take([child], 1)
                };
                let child = $std::ranges::first(values());
                $std::process::write_stdin(child, "view");
                $std::process::close_stdin(child);
                $std::process::stdout($std::process::wait(child))
                """,
            prelude + """
                fn values() {
                    let buffer = $std::storage::Buffer::new([start()]);
                    $std::storage::Buffer::snapshot(buffer)
                };
                let child = $std::ranges::first(values());
                $std::process::write_stdin(child, "snapshot");
                $std::process::close_stdin(child);
                $std::process::stdout($std::process::wait(child))
                """,
            prelude + """
                fn values() {
                    let child? = null;
                    let values = [fn { child }];
                    child = start();
                    values
                };
                let get = $std::ranges::first(values());
                let child? = get();
                if child is null {
                    "missing"
                } else {
                    $std::process::write_stdin(child, "late");
                    $std::process::close_stdin(child);
                    $std::process::stdout($std::process::wait(child))
                }
                """,
            prelude + """
                let exported? = null;
                fn launch() { global exported; exported = start(); };
                launch();
                if exported is null {
                    "missing"
                } else {
                    $std::process::write_stdin(exported, "global");
                    $std::process::close_stdin(exported);
                    $std::process::stdout($std::process::wait(exported))
                }
                """,
            prelude + """
                let state = scope { child?: null };
                fn launch(state: Scope) { state::child = start(); };
                launch(state);
                if state::child is null {
                    "missing"
                } else {
                    $std::process::write_stdin(state::child, "out");
                    $std::process::close_stdin(state::child);
                    $std::process::stdout($std::process::wait(state::child))
                }
                """,
            $$"""
                fn ignored() {
                    $std::process::spawn("{{executable}}", ["--done-child", "./joined.txt"]);
                    null
                };
                ignored();
                $std::io::read_text(./joined.txt)
                """
        ];
        string[] expected = ["echo:direct", "echo:scope", "echo:capture", "echo:flow", "echo:existing",
            "echo:materialized", "echo:view", "echo:snapshot", "echo:late", "echo:global", "echo:out", "joined"];
        for (int index = 0; index < programs.Length; index++)
        {
            if (args.Contains("--late-cell-only", StringComparer.Ordinal) && index != 8) continue;
            if (args.Contains("--external-only", StringComparer.Ordinal) && index is not (9 or 10)) continue;
            Module module = Compiler.Compile(programs[index], "returned-process.msync", level);
            foreach (Module image in Images(module, root))
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                Value value = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root), timeout.Token);
                if (value is not StringValue text || text.Data != expected[index])
                    throw new InvalidOperationException($"O{level} case {index}: expected {expected[index]}, got {value}");
                checks++;
            }
        }
    }
    if (args.Contains("--flow-failure-only", StringComparer.Ordinal)) await CheckSelectedFlowFailure();
    else if (!args.Contains("--late-cell-only", StringComparer.Ordinal) && !args.Contains("--external-only", StringComparer.Ordinal))
    {
        await CheckRetainedValues();
        foreach (int level in new[] { 0, 2, 3 }) await CheckSession(level);
        await CheckUncontrolledTransport();
        if (!args.Contains("--session-only", StringComparer.Ordinal)) await CheckSelectedFlowFailure();
    }
    Console.WriteLine($"PASS {checks} returned Process ownership contracts");
}
finally { Directory.Delete(root, true); }

async Task CheckRetainedValues()
{
    Value first = new ScopeValue(new Dictionary<string, Value> { ["first"] = Value.True });
    Value second = new ScopeValue(new Dictionary<string, Value> { ["second"] = Value.True });
    var source = new RangeValue(new[] { first, second }, TypeRef.Scope);
    RuntimeContext context = new(new(WorkingDirectory: root));
    static ValueTask<Value> NoCallback(Value function, IReadOnlyList<Value> arguments, CancellationToken token)
        => throw new InvalidOperationException("The ownership metadata check must not invoke a callback.");
    var view = (RangeValue)await NativeRegistry.InvokeAsync("ranges.take", [source, new NumberValue(1)], context, NoCallback);
    var snapshot = (RangeValue)await NativeRegistry.InvokeAsync("ranges.materialize", [view], context, NoCallback);
    if (snapshot.RetainedValues is not { Count: 1 } values || !ReferenceEquals(values[0], first))
        throw new InvalidOperationException("Materialized ownership roots retain discarded source membership.");
    checks++;
    var buffer = (RangeValue)await NativeRegistry.InvokeAsync("storage.buffer_new", [source], context, NoCallback);
    await NativeRegistry.InvokeAsync("storage.buffer_pop", [buffer], context, NoCallback);
    var saved = (RangeValue)await NativeRegistry.InvokeAsync("storage.buffer_snapshot", [buffer], context, NoCallback);
    if (saved.RetainedValues is not { Count: 1 } savedValues || !ReferenceEquals(savedValues[0], first)
        || ReferenceEquals(savedValues, buffer.RetainedValues))
        throw new InvalidOperationException("Buffer snapshot ownership roots are not its independent output membership.");
    checks++;
}

async Task CheckSession(int level)
{
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    CompilerSession compiler = new(optimization: level);
    await using ExecutionSession execution = new(new(WorkingDirectory: root));
    async Task<Value> Submit(string source)
    {
        Module module = compiler.Compile(source);
        Value result = await execution.ExecuteAsync(module, cancellationToken: timeout.Token);
        compiler.AcceptSubmission(execution.GetLocalValue);
        return result;
    }
    await Submit($$"""let child = $std::process::spawn("{{executable}}", ["--child"]);""");
    if (await Submit("child") is not ProcessValue { Completion.IsCompleted: false } child)
        throw new InvalidOperationException("Inspecting a session Process joined it or replaced its live value.");
    checks++;
    await Submit("$std::process::write_stdin(child, \"session\"); $std::process::close_stdin(child);");
    if (await Submit("$std::process::stdout($std::process::wait(child))") is not StringValue { Data: "echo:session" })
        throw new InvalidOperationException("A live Process did not survive four independent submissions.");
    checks++;

    await Submit($$"""let flowing = $std::process::spawn("{{executable}}", ["--child"]); flowing::stdout -> ./session-flow-{{level}}.txt;""");
    await Submit("$std::process::write_stdin(flowing, \"shared-flow\"); $std::process::close_stdin(flowing);");
    await Submit("acquire flowing;");
    if (await Submit($$"""$std::io::read_text(./session-flow-{{level}}.txt)""") is not StringValue { Data: "echo:shared-flow" })
        throw new InvalidOperationException("A process-dependent transfer lost its session owner.");
    checks++;

    string ready = Path.Combine(root, $"ready-{level}").Replace('\\', '/');
    string release = Path.Combine(root, $"release-{level}").Replace('\\', '/');
    string finished = Path.Combine(root, $"finished-{level}").Replace('\\', '/');
    Task<Value> ignored = Submit($$"""{ let hidden = $std::process::spawn("{{executable}}", ["--file-child", "{{ready}}", "{{release}}", "{{finished}}"]); };""");
    await WaitForFile(ready, timeout.Token);
    if (ignored.IsCompleted) throw new InvalidOperationException("An unexported temporary escaped its entry invocation.");
    await File.WriteAllTextAsync(release, "release", timeout.Token);
    await ignored;
    if (!File.Exists(finished)) throw new InvalidOperationException("The entry did not join its nonescaping Process.");
    checks++;

    string deferredReady = Path.Combine(root, $"deferred-ready-{level}").Replace('\\', '/');
    string deferredRelease = Path.Combine(root, $"deferred-release-{level}").Replace('\\', '/');
    string deferredFinished = Path.Combine(root, $"deferred-finished-{level}").Replace('\\', '/');
    await Submit($$"""
        fn produce() { [$std::process::spawn("{{executable}}",
            ["--file-child", "{{deferredReady}}", "{{deferredRelease}}", "{{deferredFinished}}"])] };
        let deferred = produce();
        """);
    Task<Value> consume = Submit("fn consume() { let process = $std::ranges::first(deferred); null }; consume();");
    await WaitForFile(deferredReady, timeout.Token);
    if (consume.IsCompleted) throw new InvalidOperationException("An old Range callback did not give its Process to the active caller.");
    await File.WriteAllTextAsync(deferredRelease, "release", timeout.Token);
    await consume;
    if (!File.Exists(deferredFinished)) throw new InvalidOperationException("The active caller did not join its nonescaping Range-produced Process.");
    checks++;

    string privateReady = Path.Combine(root, $"private-ready-{level}").Replace('\\', '/');
    string privateRelease = Path.Combine(root, $"private-release-{level}").Replace('\\', '/');
    string privateFinished = Path.Combine(root, $"private-finished-{level}").Replace('\\', '/');
    await Submit($$"""
        let outside = scope { child?: null };
        fn private_spawn(state: Scope) {
            state = scope { child?: null };
            state::child = $std::process::spawn("{{executable}}",
                ["--file-child", "{{privateReady}}", "{{privateRelease}}", "{{privateFinished}}"]);
        };
        """);
    Task<Value> privateCall = Submit("private_spawn(outside);");
    await WaitForFile(privateReady, timeout.Token);
    if (privateCall.IsCompleted) throw new InvalidOperationException("A reassigned private parameter escaped as the original argument.");
    await File.WriteAllTextAsync(privateRelease, "release", timeout.Token);
    await privateCall;
    if (await Submit("outside") is not ScopeValue outside || outside["child"] is not NullValue || !File.Exists(privateFinished))
        throw new InvalidOperationException("Original argument identity was lost while classifying an outgoing Process.");
    checks++;

    Module failed = compiler.Compile($$"""let surviving = $std::process::spawn("{{executable}}", ["--child"]); $std::io::read_text(./missing-after-spawn);""");
    try
    {
        await execution.ExecuteAsync(failed, cancellationToken: timeout.Token);
        throw new InvalidOperationException("Expected the selected missing-file error.");
    }
    catch (MsyncException) { compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue); }
    await Submit("$std::process::close_stdin(surviving); $std::process::wait(surviving);");
    checks++;

    await Submit($$"""let unfinished = $std::process::spawn("{{executable}}", ["--child"]);""");
    var unfinished = (ProcessValue)await Submit("unfinished");
    await execution.DisposeAsync();
    if (unfinished.Control?.IsRunning != false || unfinished.Completion?.IsCompleted != true)
        throw new InvalidOperationException("Session disposal left its owned live child running.");
    checks++;
}

static async Task WaitForFile(string path, CancellationToken token)
{
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using FileSystemWatcher watcher = new(Path.GetDirectoryName(path)!, Path.GetFileName(path));
    watcher.Created += (_, _) => ready.TrySetResult();
    watcher.EnableRaisingEvents = true;
    if (!File.Exists(path)) await ready.Task.WaitAsync(token);
}

async Task CheckUncontrolledTransport()
{
    TaskCompletionSource<ProcessValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int aborted = 0;
    var transport = new ProcessValue(0, "", "")
    {
        Completion = completion.Task,
        AbortOwnedTransport = () => { aborted++; completion.TrySetResult(new ProcessValue(0, "", "")); }
    };
    var interpreter = new Interpreter(Compiler.Compile("null"));
    var processes = (HashSet<ProcessValue>)typeof(Interpreter).GetField("sessionProcesses",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(interpreter)!;
    processes.Add(transport);
    var close = typeof(Interpreter).GetMethod("CloseSessionAsync",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    await (ValueTask)close.Invoke(interpreter, null)!;
    await (ValueTask)close.Invoke(interpreter, null)!;
    if (aborted != 1 || !completion.Task.IsCompletedSuccessfully)
        throw new InvalidOperationException("The session did not abort its uncontrolled owned transport exactly once.");
    checks++;
}

async Task CheckSelectedFlowFailure()
{
    await File.WriteAllTextAsync(Path.Combine(root, "blocked"), "not a directory");
    using StringWriter diagnostic = new();
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
    CompilerSession compiler = new(optimization: 2);
    await using ExecutionSession execution = new(new(WorkingDirectory: root, Error: diagnostic));
    async Task<Value> Submit(string source)
    {
        Value value = await execution.ExecuteAsync(compiler.Compile(source), cancellationToken: timeout.Token);
        compiler.AcceptSubmission(execution.GetLocalValue);
        return value;
    }
    await Submit($$"""let child = $std::process::spawn("{{executable}}", ["--child"]); child::stdout -> ./blocked/output;""");
    await Submit("$std::process::close_stdin(child);");
    try
    {
        await Submit("acquire child;");
        throw new InvalidOperationException("A selected failed process flow lost its required-success result.");
    }
    catch (MsyncException error) when (error.Message.Contains("file transfers failed", StringComparison.Ordinal))
    {
        compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
        checks++;
    }
    if (!diagnostic.ToString().Contains("blocked", StringComparison.Ordinal))
        throw new InvalidOperationException("The failed transfer lost its actual destination diagnostic.");
    checks++;
    if (await Submit("2") is not NumberValue { Data: 2 })
        throw new InvalidOperationException("The selected failed flow was not retired after its diagnostic.");
    checks++;
}

static IEnumerable<Module> Images(Module module, string root)
{
    yield return module;
    using MemoryStream relocatable = new();
    Artifact.Write(module, relocatable);
    relocatable.Position = 0;
    yield return Artifact.Read(relocatable);
    using MemoryStream executable = new();
    Artifact.Write(Binding.Bind(module, new(root, root)), executable);
    executable.Position = 0;
    yield return Artifact.Read(executable);
}
