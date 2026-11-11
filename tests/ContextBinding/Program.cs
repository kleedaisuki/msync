using Msync.Optimization;
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
    Module pure = BlockBuilder.Form(new([], [new("main", 0, 0, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool)], Stage: ArtifactStage.Executable, Context: context));
    Check(ReferenceEquals(ExecutionLowering.Lower(pure), pure), "No-op contextual lowering replaced the module");
    Module scalar = BlockBuilder.Form(new([new StringValue("value")], [new("main", 0, 1,
        [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.String)],
        Stage: ArtifactStage.Executable, Context: context));
    Check(ReferenceEquals(ExecutionLowering.Lower(scalar), scalar), "Ordinary scalar code allocated a new execution image");

    Module number = Optimizer.OptimizeInstructions(new([new NumberValue(4)],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number)]));
    Module boundNumber = Binding.BindInstructions(number, context);
    Check(ReferenceEquals(boundNumber.Functions, number.Functions), "Relocation repeated context-free optimization");
    Check(await Interpreter.ExecuteAsync(RoundTrip(boundNumber)) is NumberValue { Data: 4 },
        "Reused portable optimization changed the bound result");

    Module spelling = Optimizer.OptimizeInstructions(new([new StringValue("./here")],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Convert, Text: "Path"),
            new(OpCode.Return)], TypeRef.Path)]));
    spelling = spelling with { Functions = [spelling.Functions[0],
        new("unrelated", 0, 0, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool)] };
    Module boundSpelling = Binding.BindInstructions(spelling, context);
    Check(ReferenceEquals(boundSpelling.Functions[1], spelling.Functions[1]),
        "Contextual folding touched an unrelated portable function");
    Check(spelling.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.Convert)
        && boundSpelling.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.Convert),
        "String-to-Path conversion missed the newly available context");
    Check(await Interpreter.ExecuteAsync(RoundTrip(boundSpelling)) is PathValue { Raw: "./here" } boundPath
        && boundPath.WorkingDirectory == root, "The folded Path lost its binding origin");

    Module calls = new([new FunctionValue(1), new NumberValue(73)],
        [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Call, B: 2), new(OpCode.Return)], TypeRef.Number),
         new("scalar", 0, 0, [new(OpCode.Constant, 1), new(OpCode.Return)], TypeRef.Number)],
         Stage: ArtifactStage.Executable, Context: context);
    Module preparedCalls = ContextualBackend.PrepareInstructions(calls);
    Check(ReferenceEquals(preparedCalls.Functions, calls.Functions)
        && ReferenceEquals(preparedCalls.Constants, calls.Constants),
        "An entry with only closed scalar calls did contextual evaluation");
    Check(await Interpreter.ExecuteAsync(RoundTrip(preparedCalls)) is NumberValue { Data: 73 },
        "The selected scalar call did not execute normally");

    Module Loop(bool copies, bool suffix)
    {
        Instruction[] tail = suffix
            ? [new(OpCode.Constant, 3), new(OpCode.Constant, 4), new(OpCode.Transfer), new(OpCode.Pop)]
            : [];
        Instruction[] leaf = copies
            ? [new(OpCode.Constant, 3), new(OpCode.Constant, 4), new(OpCode.Transfer), new(OpCode.Pop),
                new(OpCode.LoadLocal, 0), new(OpCode.Return)]
            : [new(OpCode.LoadLocal, 0), new(OpCode.Return)];
        return new([new RangeValue([new NumberValue(7)], TypeRef.Number), new FunctionValue(1), new FunctionValue(2),
            new StringValue("planned"), new PathValue("./loop-copy")],
            [new("main", 0, 2,
                [new(OpCode.Constant, 0), new(OpCode.GetIterator), new(OpCode.InitializeLocal, 0),
                    new(OpCode.IteratorNext, 0, 10), new(OpCode.InitializeLocal, 1),
                    new(OpCode.Constant, 1), new(OpCode.LoadLocal, 1), new(OpCode.Call, 1, 2), new(OpCode.Pop),
                    new(OpCode.Jump, 3), .. tail, new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
            new("wrapper", 1, 1, [new(OpCode.Constant, 2), new(OpCode.LoadLocal, 0),
                new(OpCode.Call, 1, Text: "2:2"), new(OpCode.Return)], TypeRef.Number),
            new("leaf", 1, 1, leaf, TypeRef.Number)], Stage: ArtifactStage.Executable, Context: context);
    }
    Module scalarLoop = ContextualBackend.PrepareInstructions(Loop(false, false));
    Check(scalarLoop.Blocks is null && scalarLoop.ExecutionPlans is { Count: 0 }
        && scalarLoop.Functions[0].Instructions.Any(instruction => instruction.Op == OpCode.IteratorNext),
        "A copy-free call graph triggered contextual loop evaluation");
    foreach (Module image in new[] { Loop(true, false), Loop(false, true) })
    {
        Module prepared = ContextualBackend.PrepareInstructions(image);
        Check(prepared.Blocks is null && prepared.ExecutionPlans is { Count: 1 }
            && prepared.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.IteratorNext),
            "Contextual preparation missed a nested or subsequent copy");
        await Interpreter.ExecuteAsync(RoundTrip(prepared));
        string path = Path.Combine(root, "loop-copy");
        Check(await File.ReadAllTextAsync(path) == "planned", "Contextual call graph changed selected transfer bytes");
        File.Delete(path);
    }

    Check(NativeSignatures.Get("fs.relative").Effect == NativeEffect.Read
        && NativeSignatures.Get("fs.lexically_relative").Effect == NativeEffect.Pure,
        "Canonical and lexical relative paths have different observable dependencies");
    if (OperatingSystem.IsLinux())
    {
        Directory.CreateDirectory(Path.Combine(root, "targets"));
        await File.WriteAllTextAsync(Path.Combine(root, "targets", "new"), "target");
        File.CreateSymbolicLink(Path.Combine(root, "source-link"), "targets/new");
        Module relative = new([new PathValue("./source-link"), new PathValue("./published-link"), new PathValue("./")],
            [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1),
                new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Constant, 1), new(OpCode.Constant, 2),
                new(OpCode.NativeCall, 2, Text: "fs.relative"), new(OpCode.Return)], TypeRef.Path)],
            Stage: ArtifactStage.Executable, Context: context);
        Module prepared = ContextualBackend.PrepareInstructions(relative);
        Check(prepared.ExecutionPlans is { Count: 1 }
            && prepared.Functions[0].Instructions.Any(instruction => instruction.Text == "fs.relative"),
            "Canonical relative path was speculated across a link publication");
        Check(await Interpreter.ExecuteAsync(RoundTrip(prepared)) is PathValue { Raw: "targets/new" },
            "Canonical relative path did not observe the selected publication");
    }

    Module branch = new([new PathValue("./layout-switch"), new StringValue("off"),
        new PathValue("./layout-trace"), new StringValue("one"), new StringValue("on"), new StringValue("two")],
        [new("main", 0, 0, [
            new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "fs.exists"),
            new(OpCode.JumpIfFalse, 4), new(OpCode.Jump, 6),
            new(OpCode.Constant, 1), new(OpCode.Return),
            new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.NativeCall, 2, Text: "io.append_text"),
            new(OpCode.Pop), new(OpCode.Constant, 2), new(OpCode.Constant, 5), new(OpCode.NativeCall, 2, Text: "io.append_text"),
            new(OpCode.Pop), new(OpCode.Constant, 4), new(OpCode.Return)
        ], TypeRef.String)]);
    Module relocatable = Optimizer.Optimize(branch, 2);
    Module relocated = Binding.Relocate(relocatable, context);
    Check(ReferenceEquals(relocated.Functions, relocatable.Functions), "Path relocation rewrote executable instructions");
    Module arranged = RoundTrip(Binding.Bind(relocatable, context));
    Check(relocatable.Functions[0].Instructions.Count(op => op.Op == OpCode.Jump) == 1
        && arranged.Functions[0].Instructions.All(op => op.Op != OpCode.Jump)
        && arranged.Blocks!.Count < relocatable.Blocks!.Count,
        "Executable layout did not eliminate the jump and its block");
    Check(arranged.Blocks!.Count(block => arranged.Functions[block.Function].Instructions.Skip(block.Start)
        .Take(block.End - block.Start).Count(instruction => instruction.Op == OpCode.NativeCall) == 2
        && block.HasEffects) == 1, "Linear effects unnecessarily split the inspection CFG");
    string marker = Path.Combine(root, "layout-trace");
    Value cold = await Interpreter.ExecuteAsync(arranged);
    Check(cold is StringValue { Data: "off" } && !File.Exists(marker), "Layout executed an unselected effect");
    await File.WriteAllTextAsync(Path.Combine(root, "layout-switch"), "");
    Value hot = await Interpreter.ExecuteAsync(arranged);
    Check(hot is StringValue { Data: "on" } && await File.ReadAllTextAsync(marker) == "onetwo",
        "Layout changed the selected effect or return value");
    Console.WriteLine($"Executable trace layout: {relocatable.Blocks!.Count} -> {arranged.Blocks!.Count} blocks, one jump removed.");

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
