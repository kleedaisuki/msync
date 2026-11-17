using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

if (args.Contains("--benchmark", StringComparer.Ordinal)) { PerformanceCases.Run(); return; }

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
NativeProgram Codec(NativeProgram program)
{
    using MemoryStream bytes = new();
    NativeArtifact.Write(program, bytes); bytes.Position = 0;
    return NativeArtifact.Read(bytes);
}
async Task<NativeValue> Run(NativeProgram program, string cwd)
{
    await using NativeInterpreter interpreter = new(program, new(WorkingDirectory: cwd));
    return await interpreter.ExecuteAsync();
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "context-binding-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    BindingContext binding = new(root, root, Path.Combine(root, "script.msync"));
    NativeProgram scalar = Compiler.Compile("fn value() { 73 }; value()", "scalar.msync", 0);
    NativeProgram scalarBound = NativePreparation.Prepare(scalar, binding);
    Check(ReferenceEquals(scalarBound.Functions, scalar.Functions) && ReferenceEquals(scalarBound.Constants, scalar.Constants),
        "Pure scalar binding reran portable optimization or contextual evaluation");
    Check((await Run(Codec(scalarBound), Environment.CurrentDirectory)).AsNumber() == 73, "Bound scalar result changed");
    Check(ReferenceEquals(NativePreparation.Prepare(scalarBound, new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory)), scalarBound),
        "Executable code was rebound or replanned");

    NativeProgram spelling = Compiler.Compile("fn unrelated(){ true }; let spelling=\"./here\"; spelling as Path", "spelling.msync", 0);
    NativeProgram spellingBound = NativePreparation.Prepare(spelling, binding);
    Check(ReferenceEquals(spelling.Functions, spellingBound.Functions), "Path context capture rewrote unrelated portable code");
    NativePath path = (await Run(Codec(spellingBound), Environment.CurrentDirectory)).AsPath();
    Check(path.Raw == "./here" && path.WorkingDirectory == root, "String-to-Path lost its bound origin");
    Check(NativeSignatures.Get("fs.relative").Effect == NativeEffect.Read && NativeSignatures.Get("fs.lexically_relative").Effect == NativeEffect.Pure,
        "Canonical and lexical path dependencies were conflated");

    foreach (bool copies in new[] { false, true })
    {
        string effect = copies ? "\"planned\" -> ./loop-copy; " : "";
        string source = $"fn leaf(n:Number) {{ {effect}n }}; fn wrapper(n:Number) {{leaf(n)}}; for n in [7] {{wrapper(n);}};";
        NativeProgram loop = NativePreparation.Prepare(Compiler.Compile(source, "loop.msync", 0), binding);
        Check(loop.Functions[loop.EntryPoint].Instructions.Any(instruction => instruction.Op == NativeOp.OpenIterator),
            "Preparation evaluated an unknown loop body");
        Check(!File.Exists(Path.Combine(root, "loop-copy")), "Binding executed loop transfer bytes");
        await Run(Codec(loop), Environment.CurrentDirectory);
        Check(File.Exists(Path.Combine(root, "loop-copy")) == copies, "Residual nested loop call selected the wrong effect");
        if (copies)
        {
            Check(await File.ReadAllTextAsync(Path.Combine(root, "loop-copy")) == "planned", "Nested loop transfer changed bytes");
            File.Delete(Path.Combine(root, "loop-copy"));
        }
    }

    if (OperatingSystem.IsLinux())
    {
        Directory.CreateDirectory(Path.Combine(root, "targets"));
        await File.WriteAllTextAsync(Path.Combine(root, "targets", "new"), "target");
        File.CreateSymbolicLink(Path.Combine(root, "source-link"), "targets/new");
        NativeProgram relative = NativePreparation.Prepare(Compiler.Compile("./source-link -> ./published-link; $std::fs::relative(./published-link, ./)", "relative.msync", 0), binding);
        Check(!File.Exists(Path.Combine(root, "published-link")), "Preparation published transfer bytes");
        Check((await Run(Codec(relative), Environment.CurrentDirectory)).AsPath().Raw == "targets/new", "Relative lookup missed the selected link publication");
    }

    foreach (int level in new[] { 0, 1, 2, 3 })
    {
        string source = "if $std::fs::exists(./selected) { ($local @ ./source) -> ./destination } else { false }";
        NativeProgram portable = Compiler.Compile(source, "selected.msync", level);
        // This control retains runtime selection instead of freezing the current filesystem.
        NativeProgram residual = Codec(NativeBackendCompiler.Compile(
            portable with { Stage = ArtifactStage.Executable, Context = binding }, new(null, binding)));
        File.Delete(Path.Combine(root, "selected"));
        File.Delete(Path.Combine(root, "destination"));
        Check(!(await Run(residual, Environment.CurrentDirectory)).AsBool() && !File.Exists(Path.Combine(root, "destination")), $"O{level}: unselected transfer executed");
        await File.WriteAllTextAsync(Path.Combine(root, "selected"), "selected");
        await File.WriteAllTextAsync(Path.Combine(root, "source"), $"actual bytes O{level}");
        Check((await Run(residual, Environment.CurrentDirectory)).AsBool(), $"O{level}: selected transfer failed");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "destination")) == $"actual bytes O{level}", $"O{level}: transfer ignored frozen context");
        File.Delete(Path.Combine(root, "source"));
        Check(!(await Run(residual, root)).AsBool(), $"O{level}: now-missing source was not reported");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "destination")) == $"actual bytes O{level}", $"O{level}: failed transfer destroyed completed bytes");
        NativeProgram frozen = NativePreparation.Prepare(portable, binding);
        Check(frozen.Context == binding && ReferenceEquals(NativePreparation.Prepare(frozen), frozen), $"O{level}: executable binding contract changed");
    }
    Console.WriteLine($"PASS {checks} native context, residual effects, source-free artifacts and frozen path contracts");
}
finally { Directory.Delete(root, true); }
