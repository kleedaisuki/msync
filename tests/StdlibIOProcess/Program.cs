using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

if (args.FirstOrDefault() == "--standard-streams")
{
    await StandardStreamChecks.RunAsync();
    return;
}

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
    NativeRuntimeContext context = new(new(WorkingDirectory: root));
    NativeHostIO.Attach(context);
    ValueTask<NativeValue> Native(string name, params NativeValue[] values) => NativeBuiltins.InvokeAsync(name, values, context,
        (_, _, _) => throw new InvalidOperationException("Unexpected source callback"));
    NativePath file = new("unicode file.txt", root);
    string text = "Klee\r\n星⭐\nlast\0";
    await Native("io.write_text", NativeValue.FromPath(file), NativeValue.FromString(text));
    Require((await Native("io.read_text", NativeValue.FromPath(file))).AsString() == text, "Text byte content changed");
    await Native("io.append_text", NativeValue.FromPath(file), NativeValue.FromString("tail"));
    Require(await File.ReadAllTextAsync(file.LocalPath(root)) == text + "tail", "Append overwrote content");
    NativeRange lines = (await Native("io.read_lines", NativeValue.FromPath(file))).AsRange();
    List<string> firstLines = [];
    await foreach (NativeValue value in lines.Items) firstLines.Add(value.AsString());
    Require(firstLines.SequenceEqual(["Klee", "星⭐", "last\0tail"]), "Line reader altered separators or Unicode");
    await File.WriteAllTextAsync(file.LocalPath(root), "new\n");
    List<string> secondLines = [];
    await foreach (NativeValue value in lines.Items) secondLines.Add(value.AsString());
    Require(secondLines.SequenceEqual(["new"]), "Repeated enumeration did not reopen the file");

    NativeRange missing = (await Native("io.read_lines", NativeValue.FromPath(new("missing", root)))).AsRange();
    NativeRange none = (await Native("ranges.take", NativeValue.FromRange(missing), NativeValue.FromNumber(0))).AsRange();
    await foreach (NativeValue _ in none.Items) throw new InvalidOperationException("take(0) produced a line");
    checks++;
    byte[] bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    NativePath binary = new("bytes.dat", root);
    await Native("io.write_bytes", NativeValue.FromPath(binary), NativeValue.FromRange(NativeRange.Snapshot(bytes.Select(value => NativeValue.FromNumber(value)), TypeRef.Number)));
    Require((await File.ReadAllBytesAsync(binary.LocalPath(root))).SequenceEqual(bytes), "Binary write changed byte values");
    NativeRange input = (await Native("io.read_bytes", NativeValue.FromPath(binary))).AsRange();
    List<byte> restored = [];
    await foreach (NativeValue value in input.Items) restored.Add((byte)value.AsNumber());
    Require(restored.SequenceEqual(bytes), "Binary read changed byte values");
    await Native("io.append_bytes", NativeValue.FromPath(binary), NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(255), NativeValue.FromNumber(0)], TypeRef.Number)));
    Require((await Native("io.file_size", NativeValue.FromPath(binary))).AsNumber() == 258, "File size did not report appended bytes");
    byte[] beforeFailure = await File.ReadAllBytesAsync(binary.LocalPath(root));
    try
    {
        await Native("io.write_bytes", NativeValue.FromPath(binary), NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(10), NativeValue.FromNumber(256)], TypeRef.Number)));
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
        NativeProgram module = Compiler.Compile(source, "process-library.ms", level);
        foreach (NativeProgram image in Images(module, root))
        {
            await Execute(image, new(WorkingDirectory: root));
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
        NativeProgram module = Compiler.Compile(stop, "process-stop.ms", 2);
        foreach (NativeProgram image in Images(module, root))
            Require((await Execute(image, new(WorkingDirectory: root))).AsBool(), "Live process stop did not terminate owned child");
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
            await Execute(Compiler.Compile(waiting, "process-cancel.ms", 2), options, cancellation.Token);
            throw new InvalidOperationException("Cancelled process execution returned normally");
        }
        catch (OperationCanceledException) { Require(started, "Cancellation never admitted the real child"); }
    }
    if (Environment.GetEnvironmentVariable("MSYNC_IO_REMOTE") is { Length: > 0 } endpoint)
    {
        NativeConnection connection = new(ConnectionKind.Ssh, endpoint);
        NativePath remoteText = new(Path.Combine(root, "remote text.txt"), Connection: connection);
        await Native("io.write_text", NativeValue.FromPath(remoteText), NativeValue.FromString("remote 星\nsecond\0"));
        Require((await Native("io.read_text", NativeValue.FromPath(remoteText))).AsString() == "remote 星\nsecond\0", "Remote UTF-8 content changed");
        await Native("io.append_text", NativeValue.FromPath(remoteText), NativeValue.FromString("tail"));
        Require((await Native("io.read_text", NativeValue.FromPath(remoteText))).AsString() == "remote 星\nsecond\0tail", "Remote append replaced content");
        NativePath remoteBinary = new(Path.Combine(root, "remote bytes.bin"), Connection: connection);
        await Native("io.write_bytes", NativeValue.FromPath(remoteBinary), NativeValue.FromRange(NativeRange.Snapshot(bytes.Select(value => NativeValue.FromNumber(value)), TypeRef.Number)));
        NativeRange remoteBytes = (await Native("io.read_bytes", NativeValue.FromPath(remoteBinary))).AsRange();
        List<byte> decoded = [];
        await foreach (NativeValue value in remoteBytes.Items) decoded.Add((byte)value.AsNumber());
        Require(decoded.SequenceEqual(bytes), "Remote binary read/write changed bytes");
        string remoteSource = $$"""
            let endpoint = ssh://{{endpoint}};
            let process = $std::process::spawn(endpoint @ {{Quote(executable)}}, ["--child-identity"]);
            let identity = $std::process::pid(process);
            if identity <= 0 { "invalid-pid" -> ./remote-result; };
            $std::process::write_stdin(process, "remote 星");
            $std::process::close_stdin(process);
            let completed = $std::process::wait(process);
            let result = object { pid: identity, text: $std::process::stdout(completed) };
            result::pid > 0 and result::text == $std::string::concat(result::pid as String, ":remote 星")
            """;
        foreach (int level in new[] { 0, 2, 3 })
        {
            NativeProgram module = Compiler.Compile(remoteSource, "remote-process-library.ms", level);
            foreach (NativeProgram image in Images(module, root))
            {
                Require((await Execute(image, new(WorkingDirectory: root))).AsBool(),
                    "Single-channel remote Process reported proxy identity or changed input");
            }
        }
        foreach (string operation in new[] { "kill", "terminate" })
        {
            string remoteStop = $$"""
                let endpoint = ssh://{{endpoint}};
                let process = $std::process::spawn(endpoint @ {{Quote(executable)}}, ["--child-wait"]);
                let live = $std::process::running(process);
                let requested = $std::process::{{operation}}(process);
                let completed = $std::process::wait(process);
                live and requested and not $std::process::success(completed)
                """;
            Require((await Execute(Compiler.Compile(remoteStop, "remote-stop.ms", 2), new(WorkingDirectory: root))).AsBool(),
                "Remote single-channel stop did not terminate actual child");
        }
    }
    Console.WriteLine($"PASS: {checks} I/O and live-process contracts");
}
finally { Directory.Delete(root, true); }

static async ValueTask<NativeValue> Execute(NativeProgram program, ExecutionOptions options, CancellationToken token = default)
{
    await using NativeInterpreter interpreter = new(program, options);
    return await interpreter.ExecuteAsync(token);
}

static IEnumerable<NativeProgram> Images(NativeProgram program, string root)
{
    yield return program;
    yield return Roundtrip(program);
    yield return Roundtrip(NativePreparation.Prepare(program, new BindingContext(root, root)));
}

static NativeProgram Roundtrip(NativeProgram program)
{
    using MemoryStream artifact = new();
    NativeArtifact.Write(program, artifact);
    artifact.Position = 0;
    return NativeArtifact.Read(artifact);
}
