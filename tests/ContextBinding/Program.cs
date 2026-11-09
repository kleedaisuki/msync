using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

if (args.Contains("--benchmark", StringComparer.Ordinal))
{
    PerformanceCases.Run();
    return;
}

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
Module RoundTrip(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream);
    stream.Position = 0;
    return Artifact.Read(stream);
}
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "context-binding-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    BindingContext context = new(root, root, Path.Combine(root, "script.msync"));
    Module pure = Optimizer.FormBlocks(new([], [new("main", 0, 0, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool)], Stage: ArtifactStage.Executable, Context: context));
    Check(ReferenceEquals(ExecutionLowering.Lower(pure), pure), "No-op contextual lowering replaced the module");
    Module scalar = Optimizer.FormBlocks(new([new StringValue("value")], [new("main", 0, 1,
        [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String)],
        Stage: ArtifactStage.Executable, Context: context));
    Check(ReferenceEquals(ExecutionLowering.Lower(scalar), scalar), "Ordinary scalar code allocated a new execution image");

    foreach (int level in new[] { 0, 1, 2, 3 })
    {
        Module raw = new([new PathValue("./selected"), new PathValue("./source"), new PathValue("./destination")],
        [new("main", 0, 0,
        [
            new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.JumpIfFalse, 10),
            new(OpCode.LoadBuiltin, Text: "local"), new(OpCode.Constant, 1), new(OpCode.Qualify),
            new(OpCode.Constant, 2), new(OpCode.Transfer), new(OpCode.Return), new(OpCode.Nop),
            new(OpCode.False), new(OpCode.Return)
        ], TypeRef.Bool)], OptimizationLevel: level);
        Artifact.Validate(raw);
        Module executable = RoundTrip(Binding.Bind(RoundTrip(Optimizer.Optimize(raw, level)), context));
        Check(executable.Context == context, $"O{level}: executable lost captured context");
        Check(executable.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.LocalTransfer), $"O{level}: local native transfer was not selected");
        Check(!executable.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.LoadBuiltin && instruction.Text == "local"), $"O{level}: fixed endpoint root remained symbolic");
        Check(ReferenceEquals(Binding.Bind(executable, new(Environment.CurrentDirectory, Environment.CurrentDirectory)), executable), $"O{level}: executable image was rebound");
        File.Delete(Path.Combine(root, "selected"));
        File.Delete(Path.Combine(root, "destination"));
        Value unselected = await Interpreter.ExecuteAsync(executable, new(WorkingDirectory: Environment.CurrentDirectory));
        Check(unselected is BoolValue { Data: false } && !File.Exists(Path.Combine(root, "destination")), $"O{level}: unselected transfer executed");
        await File.WriteAllTextAsync(Path.Combine(root, "selected"), "selected");
        await File.WriteAllTextAsync(Path.Combine(root, "source"), $"actual bytes O{level}");
        Value selected = await Interpreter.ExecuteAsync(executable, new(WorkingDirectory: Environment.CurrentDirectory));
        Check(selected is BoolValue { Data: true }, $"O{level}: selected transfer did not succeed");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "destination")) == $"actual bytes O{level}", $"O{level}: transfer ignored frozen path context");
        File.Delete(Path.Combine(root, "source"));
        Value missing = await Interpreter.ExecuteAsync(executable, new(WorkingDirectory: root));
        Check(missing is BoolValue { Data: false }, $"O{level}: runtime did not report a now-missing source");
        Check(await File.ReadAllTextAsync(Path.Combine(root, "destination")) == $"actual bytes O{level}", $"O{level}: failed transfer destroyed completed bytes");
    }
    Console.WriteLine($"Context binding: {assertions} assertions passed.");
}
finally { Directory.Delete(root, true); }
