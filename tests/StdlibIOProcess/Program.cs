using Msync.Optimization;
using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.FirstOrDefault() is "--child-echo" or "--child-identity")
{
    string content = await Console.In.ReadToEndAsync();
    Console.Write(args[0] == "--child-identity" ? Environment.ProcessId + ":" + content : "echo:" + content);
    return;
}
if (args.FirstOrDefault() == "--child-wait")
{
    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    await Console.In.ReadToEndAsync();
    return;
}

int checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "stdlib-io-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    RuntimeContext context = new(new(WorkingDirectory: root));
    ValueTask<Value> Native(string name, params Value[] values) => NativeRegistry.InvokeAsync(name, values, context,
        (_, _, _) => throw new InvalidOperationException("Unexpected source callback"));
    PathValue file = new("unicode file.txt", root);
    string text = "Klee\r\n星⭐\nlast\0";
    await Native("io.write_text", file, new StringValue(text));
    Require(await Native("io.read_text", file) is StringValue { Data: var read } && read == text, "Text byte content changed");
    await Native("io.append_text", file, new StringValue("tail"));
    Require(await File.ReadAllTextAsync(file.LocalPath(root)) == text + "tail", "Append overwrote content");
    RangeValue lines = (RangeValue)await Native("io.read_lines", file);
    List<string> firstLines = [];
    await foreach (Value value in lines.Items) firstLines.Add(((StringValue)value).Data);
    Require(firstLines.SequenceEqual(["Klee", "星⭐", "last\0tail"]), "Line reader altered separators or Unicode");
    await File.WriteAllTextAsync(file.LocalPath(root), "new\n");
    List<string> secondLines = [];
    await foreach (Value value in lines.Items) secondLines.Add(((StringValue)value).Data);
    Require(secondLines.SequenceEqual(["new"]), "Repeated enumeration did not reopen the file");

    RangeValue missing = (RangeValue)await Native("io.read_lines", new PathValue("missing", root));
    RangeValue none = (RangeValue)await Native("ranges.take", missing, new NumberValue(0));
    await foreach (Value _ in none.Items) throw new InvalidOperationException("take(0) produced a line");
    checks++;
    byte[] bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    PathValue binary = new("bytes.dat", root);
    await Native("io.write_bytes", binary, new RangeValue(bytes.Select(value => (Value)new NumberValue(value)), TypeRef.Number));
    Require((await File.ReadAllBytesAsync(binary.LocalPath(root))).SequenceEqual(bytes), "Binary write changed byte values");
    RangeValue input = (RangeValue)await Native("io.read_bytes", binary);
    List<byte> restored = [];
    await foreach (Value value in input.Items) restored.Add((byte)((NumberValue)value).Data);
    Require(restored.SequenceEqual(bytes), "Binary read changed byte values");
    await Native("io.append_bytes", binary, new RangeValue([new NumberValue(255), new NumberValue(0)], TypeRef.Number));
    Require(await Native("io.file_size", binary) is NumberValue { Data: 258 }, "File size did not report appended bytes");
    byte[] beforeFailure = await File.ReadAllBytesAsync(binary.LocalPath(root));
    try
    {
        await Native("io.write_bytes", binary, new RangeValue([new NumberValue(10), new NumberValue(256)], TypeRef.Number));
        throw new InvalidOperationException("Invalid byte was accepted");
    }
    catch (MsyncException error) { Require(error.Message.Contains("0..255", StringComparison.Ordinal), "Byte error lacks domain"); }
    Require((await File.ReadAllBytesAsync(binary.LocalPath(root))).SequenceEqual(beforeFailure), "Failed write replaced valid content");
    Require(!Directory.EnumerateFiles(root, ".msync-*").Any(), "Owned write stage leaked");

    string executable = Environment.ProcessPath!;
    string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    string source = $$"""
        let process = $std::process::spawn({{Quote(executable)}}, ["--child-echo"]);
        let identity = $std::process::pid(process);
        if identity <= 0 { "invalid-pid" -> ./result; };
        $std::process::write_stdin(process, "hello 星");
        $std::process::close_stdin(process);
        let completed = $std::process::wait(process);
        $std::process::stdout(completed) -> ./result;
        """;
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module module = Compiler.Compile(source, "process-library.ms", level);
        using MemoryStream encoded = new();
        Artifact.Write(module, encoded); encoded.Position = 0;
        foreach (Module image in new[] { module, Artifact.Read(encoded), Binding.Bind(module, new(root, root)) })
        {
            await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
            Require(await File.ReadAllTextAsync(Path.Combine(root, "result")) == "echo:hello 星", "Owned live process pipe failed");
        }
    }
    foreach (string operation in new[] { "kill", "terminate" })
    {
        string stop = $$"""
            let process = $std::process::spawn({{Quote(executable)}}, ["--child-wait"]);
            let live = $std::process::running(process);
            let requested = $std::process::{{operation}}(process);
            if not requested { $std::process::kill(process); };
            let completed = $std::process::wait(process);
            live and not $std::process::success(completed)
            """;
        Module module = Compiler.Compile(stop, "process-stop.ms", 2);
        foreach (Module image in new[] { module, Binding.Bind(module, new(root, root)) })
            Require(await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root)) is BoolValue { Data: true }, "Live process stop did not terminate owned child");
    }
    using (CancellationTokenSource cancellation = new())
    {
        bool started = false;
        string waiting = $"let process = $std::process::spawn({Quote(executable)}, [\"--child-wait\"]); $std::process::wait(process);";
        ExecutionOptions options = new(WorkingDirectory: root, Observe: observation =>
        {
            if (observation.Phase != "process" || observation.Outcome != "started") return;
            started = true;
            cancellation.Cancel();
        });
        try
        {
            await Interpreter.ExecuteAsync(Compiler.Compile(waiting, "process-cancel.ms", 2), options, cancellation.Token);
            throw new InvalidOperationException("Cancelled process execution returned normally");
        }
        catch (OperationCanceledException) { Require(started, "Cancellation never admitted the real child"); }
    }
    if (Environment.GetEnvironmentVariable("MSYNC_IO_REMOTE") is { Length: > 0 } endpoint)
    {
        ConnectionValue connection = new(ConnectionKind.Ssh, endpoint);
        PathValue remoteText = new(Path.Combine(root, "remote text.txt"), Connection: connection);
        await Native("io.write_text", remoteText, new StringValue("remote 星\nsecond\0"));
        Require(await Native("io.read_text", remoteText) is StringValue { Data: "remote 星\nsecond\0" }, "Remote UTF-8 content changed");
        await Native("io.append_text", remoteText, new StringValue("tail"));
        Require(await Native("io.read_text", remoteText) is StringValue { Data: "remote 星\nsecond\0tail" }, "Remote append replaced content");
        PathValue remoteBinary = new(Path.Combine(root, "remote bytes.bin"), Connection: connection);
        await Native("io.write_bytes", remoteBinary, new RangeValue(bytes.Select(value => (Value)new NumberValue(value)), TypeRef.Number));
        RangeValue remoteBytes = (RangeValue)await Native("io.read_bytes", remoteBinary);
        List<byte> decoded = [];
        await foreach (Value value in remoteBytes.Items) decoded.Add((byte)((NumberValue)value).Data);
        Require(decoded.SequenceEqual(bytes), "Remote binary read/write changed bytes");
        string remoteSource = $$"""
            let endpoint = ssh://{{endpoint}};
            let process = $std::process::spawn(endpoint::{{Quote(executable)}}, ["--child-identity"]);
            let identity = $std::process::pid(process);
            if identity <= 0 { "invalid-pid" -> ./remote-result; };
            $std::process::write_stdin(process, "remote 星");
            $std::process::close_stdin(process);
            let completed = $std::process::wait(process);
            scope { pid: identity, text: $std::process::stdout(completed) }
            """;
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = Compiler.Compile(remoteSource, "remote-process-library.ms", level);
            using MemoryStream encoded = new(); Artifact.Write(module, encoded); encoded.Position = 0;
            foreach (Module image in new[] { module, Artifact.Read(encoded), Binding.Bind(module, new(root, root)) })
            {
                ScopeValue result = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
                double pid = ((NumberValue)result.Members["pid"]).Data;
                Require(result.Members["text"] is StringValue { Data: var output } && output == pid + ":remote 星",
                    $"Single-channel remote Process reported proxy identity or changed input: pid={pid}, output={result.Members["text"]}");
            }
        }
        foreach (string operation in new[] { "kill", "terminate" })
        {
            string remoteStop = $$"""
                let endpoint = ssh://{{endpoint}};
                let process = $std::process::spawn(endpoint::{{Quote(executable)}}, ["--child-wait"]);
                let live = $std::process::running(process);
                let requested = $std::process::{{operation}}(process);
                let completed = $std::process::wait(process);
                live and requested and not $std::process::success(completed)
                """;
            Require(await Interpreter.ExecuteAsync(Compiler.Compile(remoteStop, "remote-stop.ms", 2), new(WorkingDirectory: root)) is BoolValue { Data: true },
                "Remote single-channel stop did not terminate actual child");
        }
    }
    Console.WriteLine($"PASS: {checks} I/O and live-process contracts");
}
finally { Directory.Delete(root, true); }
