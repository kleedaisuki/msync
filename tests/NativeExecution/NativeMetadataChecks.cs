using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class NativeMetadataChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        NativeProgram metadata = new([], [new("type-metadata", [new(0), new(0), new(0)],
            [new(NativeOp.MakeType, 0, Immediate: (long)TypeKind.Number),
             new(NativeOp.MakeType, 1, 0, Immediate: (long)TypeKind.Range),
             new(NativeOp.MakeType, 2, 1, Immediate: (long)(TypeKind.Range | TypeKind.Null)),
             new(NativeOp.Return, A: 2)], new([], 0, []))], [new(TypeKind.Type)], []);
        using MemoryStream bytes = new();
        NativeArtifact.Write(metadata, bytes);
        bytes.Position = 0;
        await using (NativeInterpreter interpreter = new(NativeArtifact.Read(bytes)))
        {
            TypeRef expected = new(TypeKind.Range | TypeKind.Null, new(TypeKind.Range, TypeRef.Number));
            if ((await interpreter.ExecuteAsync()).AsType() != expected)
                throw new Exception("Nested MakeType flattened its runtime element descriptor");
            checks++;
        }
        NativeProgram valueType = new([], [new("value-type", [new(0, NativeSlotRole.Parameter), new(1)],
            [new(NativeOp.Unary, 1, 0, Immediate: (int)NativeUnary.ValueType), new(NativeOp.Return, A: 1)],
            new([0], 1, []), ParameterCount: 1)], [new(TypeKind.Any), new(TypeKind.Type)], []);
        NativeValue[] values = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(3), NativeValue.FromString("text"),
            NativeValue.FromPath(new("./bound", "/origin")), NativeValue.FromConnection(new(ConnectionKind.Local)),
            NativeValue.FromFunction(new(-1, NativeName: "math.abs")),
            NativeValue.FromRange(NativeRange.Snapshot([], new(TypeKind.Range, TypeRef.Number))),
            NativeValue.FromProcess(new(7, "", "")), NativeValue.FromType(TypeRef.Number)];
        await using (NativeInterpreter interpreter = new(valueType))
            foreach (NativeValue value in values)
            {
                if ((await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value])).AsType() != value.Type)
                    throw new Exception($"ValueType used a declared union rather than the actual {value.Kind} descriptor");
                checks++;
            }
        foreach (int captureMode in new[] { 0, -1 })
        {
            NativeProgram function = new([new(NativeKind.Null)], [new("reference", [new(0)],
                [new(NativeOp.MakeFunction, 0, 1, C: captureMode), new(NativeOp.Return, A: 0)], new([], 0, [])),
                new("body", [new(1)], [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 1, []))], [new(TypeKind.Function), new(TypeKind.Null)], []);
            using MemoryStream functionBytes = new();
            NativeArtifact.Write(function, functionBytes);
            functionBytes.Position = 0;
            await using NativeInterpreter interpreter = new(NativeArtifact.Read(functionBytes));
            NativeFunction first = (await interpreter.ExecuteAsync()).AsFunction();
            NativeFunction second = (await interpreter.ExecuteAsync()).AsFunction();
            if ((first.Environment is null) != (captureMode == 0)
                || ReferenceEquals(first.Environment, second.Environment) != (captureMode == 0))
                throw new Exception("Plain function reference and fresh owned closure environments were conflated");
            checks++;
        }
        checks += await PendingMetadataAsync();
        Console.WriteLine("PASS native-metadata: actual ValueType, nested MakeType and plain versus fresh function environments");
        return checks;
    }

    private static async Task<int> PendingMetadataAsync()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "native-execution", "pending-metadata");
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "ready"));
        TaskCompletionSource<NativeProcess> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(directory) { Filter = "ready", EnableRaisingEvents = true };
        watcher.Created += (_, _) => published.TrySetResult();
        watcher.Renamed += (_, _) => published.TrySetResult();
        watcher.Changed += (_, _) => published.TrySetResult();
        NativeRuntimeContext context = new(new(WorkingDirectory: directory));
        context.ProcessCallAsync = (_, _, _, _) =>
        {
            NativeProcess process = new(0, "", "") { Completion = completed.Task };
            context.ProcessCreated!(process);
            return new(NativeValue.FromProcess(process));
        };
        NativeProgram program = new([new(NativeKind.Path, Text: "unused"), new(NativeKind.Path, Text: "./ready"), new(NativeKind.String, Text: "go")],
            [new("pending-metadata", [new(0), new(1), new(2), new(3), new(4), new(5), new(0), new(6), new(7)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.ProcessCall, 1, 0, Inputs: []),
                 new(NativeOp.CallNative, 2, Symbol: "process.exit_code", Inputs: [1]), new(NativeOp.Copy, 3, 2),
                 new(NativeOp.Unary, 4, 3, Immediate: (int)NativeUnary.ValueType),
                 new(NativeOp.Unary, 5, 3, 0, Immediate: (int)NativeUnary.HasLayout), new(NativeOp.Branch, A: 5, B: 11, C: 7),
                 new(NativeOp.Constant, 6, 1), new(NativeOp.Constant, 7, 2),
                 new(NativeOp.CallNative, 8, Symbol: "io.write_text", Inputs: [6, 7]), new(NativeOp.Return, A: 4),
                 new(NativeOp.Unreachable, Symbol: "Pending Number was mistaken for an Address")], new([], 4, []))],
            [new(TypeKind.Path), new(TypeKind.Process), new(TypeKind.Number), new(TypeKind.Any), new(TypeKind.Type),
             new(TypeKind.Bool), new(TypeKind.String), new(TypeKind.Null)], [new([])]);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using NativeInterpreter interpreter = new(program, context: context);
        Task<NativeValue> pending = interpreter.ExecuteAsync(timeout.Token).AsTask();
        try { await published.Task.WaitAsync(timeout.Token); }
        finally { completed.TrySetResult(new(7, "", "")); }
        if ((await pending).AsType() != TypeRef.Number || await File.ReadAllTextAsync(Path.Combine(directory, "ready")) != "go")
            throw new Exception("Copying a typed future to Any erased its non-demanding producer metadata");
        return 1;
    }
}
