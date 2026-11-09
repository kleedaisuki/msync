using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.Contains("--windows-rooted", StringComparer.Ordinal)) { WindowsRootChecks.Run(); return; }
if (args.Contains("--editor-session", StringComparer.Ordinal)) { await EditorSessionChecks.Run(); return; }
if (args.Contains("--process-members", StringComparer.Ordinal)) { await EditorSessionChecks.RunProcessMembers(); return; }

int assertions = 0;
void Equal<T>(T expected, T actual, string message)
{
    assertions++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected '{expected}', received '{actual}'");
}

ConnectionValue ssh = new(ConnectionKind.Ssh, "unused");
ConnectionValue wsl = new(ConnectionKind.Wsl);
PathValue Join(PathValue basis, string item, bool windows = true) => PathSemantics.JoinValue(basis, new StringValue(item), windows);
PathValue home = Join(new("~", "captured-cwd", HomeDirectory: "captured-home"), ".codex");
Equal("~/.codex", home.Raw, "Symbolic join selected the compiling host's separator");
Equal("~\\.codex", home.NativeText, "Windows local projection lost its native separator");
foreach (ConnectionValue connection in new[] { ssh, wsl })
{
    PathValue qualified = PathSemantics.Qualify(home, connection);
    PathValue before = Join(PathSemantics.Qualify(new("~"), connection), ".codex");
    Equal(before, qualified, "Qualifying before and after a binding differs");
    Equal("~/.codex", qualified.NativeText, "Remote qualification leaked Windows spelling");
    Equal<string?>(null, qualified.LocalSpelling, "Remote qualification retained local projection");
    Equal<string?>(null, qualified.WorkingDirectory, "Remote qualification retained local cwd");
    Equal<string?>(null, qualified.HomeDirectory, "Remote qualification retained local home");
}
PathValue relative = Join(Join(new("."), "dir"), "file");
Equal("./dir/file", relative.Raw, "Relative symbolic path differs");
Equal(".\\dir\\file", relative.NativeText, "Relative Windows projection differs");
Equal("./dir/file", PathSemantics.Qualify(relative, ssh).NativeText, "Relative qualification leaked local separators");
PathValue trailing = Join(new("left\\"), "leaf");
Equal("left\\/leaf", trailing.Raw, "Generated join consumed a literal POSIX backslash");
Equal("left\\leaf", trailing.NativeText, "Windows join duplicated its trailing separator");
Equal("left\\/leaf", PathSemantics.Qualify(trailing, ssh).Raw, "Remote qualification rewrote literal filename data");
Equal("one\\two", PathSemantics.FileName(new("folder/one\\two", Connection: ssh)), "Remote basename used the host's backslash grammar");
Equal("leaf\\", PathSemantics.FileName(new("folder/leaf\\", Connection: ssh)), "Remote basename consumed a trailing filename backslash");
PathValue right = Join(new("a"), "b");
PathValue composed = PathSemantics.JoinValue(new("root"), right, true);
Equal("root/a/b", composed.Raw, "Joining a projected right operand lost symbolic spelling");
Equal("root\\a\\b", composed.NativeText, "Joining a projected right operand lost native spelling");
Equal<string?>(null, relative.WithRaw("next").LocalSpelling, "Derived path retained a stale local spelling");
PathValue posix = Join(new("left\\"), "leaf", false);
Equal("left\\/leaf", posix.NativeText, "POSIX native projection consumed backslash data");
Equal<string?>(null, posix.LocalSpelling, "Ordinary POSIX join allocated a local spelling");

Module Constant(Value value) => new([value], [new("main", 0, 0,
    [new(OpCode.Constant, 0), new(OpCode.Return)], value.Type)]);
using (MemoryStream stream = new())
{
    Artifact.Write(Constant(new ScopeValue(new Dictionary<string, Value> { ["file"] = relative })), stream);
    Equal((int)Artifact.FormatVersion, (int)BitConverter.ToUInt16(stream.ToArray(), 8), "New path projection did not use the current artifact format");
    stream.Position = 0;
    ScopeValue loaded = (ScopeValue)Artifact.Read(stream).Constants[0];
    Equal(relative, (PathValue)loaded.Members["file"], "Current format lost nested path projection");
}
using (MemoryStream stream = new())
{
    Module closure = new([new FunctionValue(1, [relative])],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Function),
         new("captured", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Path, CaptureCount: 1)]);
    Artifact.Write(closure, stream); stream.Position = 0;
    FunctionValue loaded = (FunctionValue)Artifact.Read(stream).Constants[0];
    Equal(relative, (PathValue)loaded.Captures![0], "Current format lost captured path projection");
}
using (Stream fixture = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Msync.PathProjection.Tests.Fixtures.path-v1.mso")!)
{
    try { Artifact.Read(fixture); throw new InvalidOperationException("Obsolete artifact format was accepted"); }
    catch (MsyncException error)
    {
        Equal(true, error.Message.Contains("format 1", StringComparison.Ordinal), "Rejected artifact lost its format diagnostic");
        Equal(true, error.Message.Contains("Recompile", StringComparison.Ordinal), "Rejected artifact omitted migration guidance");
    }
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "path-projection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await File.WriteAllTextAsync(Path.Combine(root, "actual-input"), "projected bytes");
    PathValue input = new("symbolic-input", root, LocalSpelling: "actual-input");
    RuntimeContext context = new(new(WorkingDirectory: root));
    Equal(Path.Combine(root, "actual-input"), input.LocalPath(root), "Local I/O ignored captured projection");
    Equal("symbolic-input", PathSemantics.Qualify(input, ssh).Raw, "Remote qualification used physical local spelling");
    await TransferEngine.CopyAsync(input, new PathValue("output", root), context);
    Equal("projected bytes", await File.ReadAllTextAsync(Path.Combine(root, "output")), "Physical transfer ignored local spelling");
    async ValueTask<Value> Native(string name, params Value[] args) => await NativeRegistry.InvokeAsync(name, args, context,
        (_, _, _) => throw new InvalidOperationException("Unexpected callback"));
    Equal("actual-input", ((StringValue)await Native("fs.native", input)).Data, "fs.native differs from Path conversion");
    Equal("symbolic-input", ((StringValue)await Native("fs.generic_string", input)).Data, "Generic lexical inspection lost raw spelling");
    PathValue parent = (PathValue)await Native("fs.parent_path", input.WithRaw("folder/input", "actual/folder/input"));
    Equal("folder", parent.Raw, "Lexical parent changed symbolic spelling");
    if (!OperatingSystem.IsWindows()) Equal<string?>(null, parent.LocalSpelling, "Derived POSIX parent retained stale projection");

    string source = "let file = ~/.codex; let remote = ssh://unused; let text: String = file @ remote; text";
    Module portable = Compiler.Compile(source, "projection-test", 0);
    foreach (Module module in new[] { portable, Binding.Bind(portable, new(root, root)) })
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream); stream.Position = 0;
        Equal("~/.codex", ((StringValue)await Interpreter.ExecuteAsync(Artifact.Read(stream))).Data, "Source/object/executable endpoint qualification differs");
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"PASS: {assertions} path projection and current artifact assertions");
