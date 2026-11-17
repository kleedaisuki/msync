using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using static NativeFixture;

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

NativeConnection ssh = new(ConnectionKind.Ssh, "unused");
NativeConnection wsl = new(ConnectionKind.Wsl);
NativePath Join(NativePath basis, string item, bool windows = true) => PathSemantics.JoinValue(basis, item, windows);
NativePath home = Join(new("~", "captured-cwd", HomeDirectory: "captured-home"), ".codex");
Equal("~/.codex", home.Raw, "Symbolic join selected the compiling host's separator");
Equal("~\\.codex", home.NativeText, "Windows local projection lost its native separator");
foreach (NativeConnection connection in new[] { ssh, wsl })
{
    NativePath qualified = PathSemantics.Qualify(home, connection);
    NativePath before = Join(PathSemantics.Qualify(new("~"), connection), ".codex");
    Equal(before, qualified, "Qualifying before and after a binding differs");
    Equal("~/.codex", qualified.NativeText, "Remote qualification leaked Windows spelling");
    Equal<string?>(null, qualified.LocalSpelling, "Remote qualification retained local projection");
    Equal<string?>(null, qualified.WorkingDirectory, "Remote qualification retained local cwd");
    Equal<string?>(null, qualified.HomeDirectory, "Remote qualification retained local home");
}
NativePath relative = Join(Join(new("."), "dir"), "file");
Equal("./dir/file", relative.Raw, "Relative symbolic path differs");
Equal(".\\dir\\file", relative.NativeText, "Relative Windows projection differs");
Equal("./dir/file", PathSemantics.Qualify(relative, ssh).NativeText, "Relative qualification leaked local separators");
NativePath trailing = Join(new("left\\"), "leaf");
Equal("left\\/leaf", trailing.Raw, "Generated join consumed a literal POSIX backslash");
Equal("left\\leaf", trailing.NativeText, "Windows join duplicated its trailing separator");
Equal("left\\/leaf", PathSemantics.Qualify(trailing, ssh).Raw, "Remote qualification rewrote literal filename data");
Equal("one\\two", PathSemantics.FileName(new("folder/one\\two", Connection: ssh)), "Remote basename used the host's backslash grammar");
Equal("leaf\\", PathSemantics.FileName(new("folder/leaf\\", Connection: ssh)), "Remote basename consumed a trailing filename backslash");
NativePath right = Join(new("a"), "b");
NativePath composed = PathSemantics.JoinValue(new("root"), right, true);
Equal("root/a/b", composed.Raw, "Joining a projected right operand lost symbolic spelling");
Equal("root\\a\\b", composed.NativeText, "Joining a projected right operand lost native spelling");
Equal<string?>(null, relative.WithRaw("next").LocalSpelling, "Derived path retained a stale local spelling");
NativePath posix = Join(new("left\\"), "leaf", false);
Equal("left\\/leaf", posix.NativeText, "POSIX native projection consumed backslash data");
Equal<string?>(null, posix.LocalSpelling, "Ordinary POSIX join allocated a local spelling");

NativeProgram nested = new([Constant(relative)],
    [new("nested-path", [new(0), new(1), new(2)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.Allocate, 1, 0),
         new(NativeOp.AddressOffset, 2, 1, 0, 0), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0),
         new(NativeOp.Return, A: 1)], new([], 1, []))],
    [new(TypeKind.Path), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)], [new([new("file", 0)])]);
using (MemoryStream stream = new())
{
    NativeArtifact.Write(nested, stream);
    Equal((int)NativeArtifact.FormatVersion, (int)BitConverter.ToUInt16(stream.ToArray(), 8), "New path projection did not use the current artifact format");
    stream.Position = 0;
    NativeValue loaded = await Run(NativeArtifact.Read(stream));
    Equal(relative, loaded.AsAddress().Field(0).Load().AsPath(), "Current format lost nested path projection");
}
NativeProgram closure = new([Constant(relative)],
    [new("capture-path", [new(0), new(1)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.MakeFunction, 1, 1, Inputs: [0]), new(NativeOp.Return, A: 1)], new([], 1, [])),
     new("captured", [new(0, NativeSlotRole.Capture)], [new(NativeOp.Return, A: 0)],
        new([], 0, [new(0, NativeCaptureMode.Value)]), CaptureCount: 1)],
    [new(TypeKind.Path), new(TypeKind.Function)], []);
Equal(relative, (await Run(Codec(closure))).AsFunction().Environment![0].Read().AsPath(), "Current format lost captured path projection");
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "path-projection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await File.WriteAllTextAsync(Path.Combine(root, "actual-input"), "projected bytes");
    NativePath input = new("symbolic-input", root, LocalSpelling: "actual-input");
    NativeRuntimeContext context = new(new(WorkingDirectory: root));
    Equal(Path.Combine(root, "actual-input"), input.LocalPath(root), "Local I/O ignored captured projection");
    Equal("symbolic-input", PathSemantics.Qualify(input, ssh).Raw, "Remote qualification used physical local spelling");
    if (!await NativeHostIO.TransferAsync(input, new("output", root), context)) throw new InvalidOperationException("Projected transfer failed");
    Equal("projected bytes", await File.ReadAllTextAsync(Path.Combine(root, "output")), "Physical transfer ignored local spelling");
    async ValueTask<NativeValue> Native(string name, params NativeValue[] values) => await NativeBuiltins.InvokeAsync(name, values, context,
        (_, _, _) => throw new InvalidOperationException("Unexpected callback"));
    Equal("actual-input", (await Native("fs.native", input)).AsString(), "fs.native differs from Path conversion");
    Equal("symbolic-input", (await Native("fs.generic_string", input)).AsString(), "Generic lexical inspection lost raw spelling");
    NativePath parent = (await Native("fs.parent_path", input.WithRaw("folder/input", "actual/folder/input"))).AsPath();
    Equal("folder", parent.Raw, "Lexical parent changed symbolic spelling");
    if (!OperatingSystem.IsWindows()) Equal<string?>(null, parent.LocalSpelling, "Derived POSIX parent retained stale projection");
    assertions += await BorrowedPathChecks.Run(root);
    string source = "let file = ~/.codex; let remote = ssh://unused; let text: String = remote @ file; text";
    NativeProgram portable = Compiler.Compile(source, "projection-test", 0);
    foreach (NativeProgram program in new[] { portable, NativePreparation.Prepare(portable, new BindingContext(root, root)) })
        Equal("~/.codex", (await Run(Codec(program), new(WorkingDirectory: root))).AsString(), "Source/object/executable endpoint qualification differs");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"PASS: {assertions} path projection and current artifact assertions");
