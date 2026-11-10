using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal static class CallPreparationChecks
{
    internal static async Task Run()
    {
        int checks = 0;
        int cases = 0;
        string root = Path.GetFullPath(".temp/call-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckClassification();
            await CheckMetadataFrontier();
            await CheckProcessFlowDeferral();
            await CheckIndependentMetadata(observeSibling: false);
            await CheckIndependentMetadata(observeSibling: true);
            foreach (string route in new[] { "module", "object", "executable" })
                foreach (bool dispatch in new[] { false, true })
                    await CheckSelectedTarget(route, dispatch);
            foreach (int level in new[] { 0, 2 })
                foreach (string route in new[] { "module", "object", "executable" })
                {
                    await CheckSource("captured-counter", CounterSource, level, route,
                        new() { ["calls.txt"] = "8", ["total.txt"] = "36", ["trace.txt"] = string.Concat(Enumerable.Repeat("tick|", 8)) });
                    await CheckSource("metadata-after-publish", MetadataSource, level, route,
                        new() { ["calls.txt"] = "1", ["result.txt"] = "present", ["published.txt"] = "present" });
                }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Call preparation: {cases} real VM journeys, {checks} classification/effect contracts passed.");

        async Task CheckClassification()
        {
            FunctionCode[] functions = [
                new("entry", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
                new("pure", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
                new("planned", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Transfer), new(OpCode.Return)], TypeRef.Bool),
                new("opaque", 0, 0, [new(OpCode.Constant, 1), new(OpCode.Constant, 0), new(OpCode.NativeCall, 2, Text: "io.append_text"), new(OpCode.Return)], TypeRef.Null),
                new("nested", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Call, B: 2), new(OpCode.Return)], TypeRef.Null)
            ];
            Module image = new([new StringValue("not-executed"), new PathValue("./guard.txt")], functions,
                Stage: ArtifactStage.Executable, Context: new(root, root)) { ExecutionPlans = [] };
            Interpreter interpreter = new(image, new(WorkingDirectory: root));
            Type stackType = typeof(Interpreter).Assembly.GetType("Msync.Runtime.OperandStack")!;
            MethodInfo add = stackType.GetMethod("Add")!;
            PropertyInfo count = stackType.GetProperty("Count")!;
            MethodInfo guard = typeof(Interpreter).GetMethod("CanExecuteCallDirectly", BindingFlags.Instance | BindingFlags.NonPublic)!;

            bool Probe(Instruction call, Value[] operands, params Instruction[] tail)
            {
                object stack = Activator.CreateInstance(stackType, nonPublic: true)!;
                foreach (Value operand in operands) add.Invoke(stack, [operand]);
                FunctionCode caller = new("guard-probe", 0, 0,
                    new[] { call }.Concat(tail.Length == 0 ? [new(OpCode.Return)] : tail).ToArray(), TypeRef.Any);
                bool result = (bool)guard.Invoke(interpreter, [caller, 0, stack])!;
                Check((int)count.GetValue(stack)! == operands.Length, "Classification consumed the actual call operands.");
                return result;
            }

            Check(Probe(new(OpCode.Call), [new FunctionValue(1)]), "A known final source call was not admitted directly.");
            Check(Probe(new(OpCode.Call, B: 2), [new FunctionValue(2)]), "Static B did not override the callable's original identity.");
            Check(!Probe(new(OpCode.Call, B: 3), [new FunctionValue(1)]), "The original callable hid a planning-capable selected target.");
            Check(Probe(new(OpCode.Call, B: 3, Text: "2:1"), [new FunctionValue(2)]), "Text dispatch did not take precedence over B.");
            Check(!Probe(new(OpCode.Call, B: 2, Text: "1:2"), [new FunctionValue(1)]), "B incorrectly overrode a planning-capable text target.");
            Check(!Probe(new(OpCode.Call, Text: "2:1"), [new FunctionValue(1)]), "An unresolved dispatch identity bypassed preparation.");
            Check(!Probe(new(OpCode.Call, B: -1), [new FunctionValue(1)]), "An open session call bypassed its session policy.");
            Check(!Probe(new(OpCode.Call, B: 2), [new FunctionValue(-1, NativeName: "math.abs")]), "A native callee entered the source-call path.");
            Check(!Probe(new(OpCode.Call), [new StringValue("not-a-source-function")]), "An unknown callable bypassed preparation.");
            Check(!Probe(new(OpCode.Call, A: 1), [new FunctionValue(1)]), "An incomplete argument stack bypassed preparation.");
            Check(!Probe(new(OpCode.Call), []), "An empty operand stack bypassed preparation.");
            Check(!Probe(new(OpCode.ProcessCall), [new FunctionValue(1)]), "A process call entered the source-call path.");
            Check(Probe(new(OpCode.Call, A: 1, B: 2), [new FunctionValue(2), Value.Null]), "The guard selected an argument instead of the callee.");
            Check(Probe(new(OpCode.Call), [new CellValue(new FunctionValue(1))]), "A shared callable location lost its actual value.");
            Check(Probe(new(OpCode.Call), [new FunctionValue(3)]), "An opaque-native source body was mistaken for a planning target.");
            Check(!Probe(new(OpCode.Call), [new FunctionValue(4)]), "A source target with a nested call lost preparation.");
            foreach (Instruction future in new Instruction[]
            {
                new(OpCode.Call), new(OpCode.GetIterator), new(OpCode.SelectPath),
                new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Transfer), new(OpCode.LocalTransfer)
            })
                Check(!Probe(new(OpCode.Call), [new FunctionValue(1)], future, new(OpCode.Return)),
                    "A future " + future.Op + " opportunity was skipped.");
            Check(Probe(new(OpCode.Call), [new FunctionValue(1)], new(OpCode.Return), new(OpCode.Transfer)),
                "Unreachable post-return work blocked direct execution.");
            Check(Probe(new(OpCode.Call), [new FunctionValue(1)], new(OpCode.NativeCall, 2, Text: "io.append_text"), new(OpCode.Return)),
                "An opaque native tail was incorrectly treated as a metadata opportunity.");
            await interpreter.ExecuteAsync();
            Check(!File.Exists(Path.Combine(root, "guard.txt")), "Classification executed a callee or its opaque I/O.");
        }

        async Task CheckSelectedTarget(string route, bool dispatch)
        {
            cases++;
            string work = Path.Combine(root, "selected-target-" + dispatch + "-" + route);
            Directory.CreateDirectory(work);
            Module raw = new([new PathValue("./trace.txt"), new StringValue("once"), new NumberValue(17),
                new NumberValue(99), new FunctionValue(2, [])],
                [new("entry", 0, 0, [new(OpCode.Constant, 4), new(OpCode.Call, B: dispatch ? 3 : 2, Text: dispatch ? "2:1" : null), new(OpCode.Return)], TypeRef.Number),
                 new("selected", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.NativeCall, 2, Text: "io.append_text"),
                    new(OpCode.Pop), new(OpCode.Constant, 2), new(OpCode.Return)], TypeRef.Number),
                 new("identity", 0, 0, [new(OpCode.Constant, 3), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
            Check(!ContextualBackend.NeedsPreparation(raw.Functions[1]), "The selected opaque native fixture unexpectedly needs planning.");
            Module program = Prepare(raw, work, route);
            Check(!File.Exists(Path.Combine(work, "trace.txt")), "Preparation executed selected opaque I/O.");
            Value result = await Interpreter.ExecuteAsync(program, new(WorkingDirectory: work));
            Check(result is NumberValue { Data: 17 }, "The real call executed its original identity instead of the selected target.");
            Check(File.ReadAllText(Path.Combine(work, "trace.txt")) == "once", "The selected source body did not execute exactly once.");
        }

        async Task CheckMetadataFrontier()
        {
            cases++;
            string work = Path.Combine(root, "held-producer-metadata-frontier");
            Directory.CreateDirectory(work);
            TaskCompletionSource<ProcessValue> producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ProcessValue process = new(0, "", "") { Completion = producer.Task };
            CellValue counter = new(new NumberValue(0));
            Value[] constants = [process, new PathValue("./published.txt"), new FunctionValue(1, [counter]),
                new NumberValue(1), new StringValue("missing")];
            Module program = new(constants,
                [new("entry", 0, 0,
                    [new(OpCode.Constant, 0), new(OpCode.GetMember, Text: "stdout"), new(OpCode.Constant, 1),
                     new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Constant, 2), new(OpCode.Constant, 1),
                     new(OpCode.Call, A: 1, B: 2), new(OpCode.Return)], TypeRef.String),
                 new("observe", 1, 2,
                    [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 3), new(OpCode.Add), new(OpCode.StoreLocal, 0),
                     new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.JumpIfFalse, 10),
                     new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 1, Text: "io.read_text"), new(OpCode.Return),
                     new(OpCode.Constant, 4), new(OpCode.Return)],
                    TypeRef.String, CaptureCount: 1, ParameterTypes: [TypeRef.Path])],
                Stage: ArtifactStage.Executable, Context: new(work, work), OptimizationLevel: 0) { ExecutionPlans = [] };
            // The held live process is a runtime fixture, never an artifact payload.
            Task<Value> execution = Interpreter.ExecuteAsync(program, new(WorkingDirectory: work)).AsTask();
            bool prefixVisible = counter.Data is NumberValue { Data: 1 };
            bool executionPending = !execution.IsCompleted;
            bool producerHeld = !producer.Task.IsCompleted;
            bool unpublished = !File.Exists(Path.Combine(work, "published.txt"));
            producer.SetResult(new ProcessValue(0, "present", ""));
            Value result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Check(prefixVisible, "Metadata waiting moved before the callee's independent captured-counter prefix.");
            Check(executionPending && producerHeld, "The metadata observer completed while its selected publication was held.");
            Check(unpublished, "The held producer published bytes before being released.");
            Check(counter.Data is NumberValue { Data: 1 }, "Resuming the metadata frontier replayed the callee prefix.");
            Check(result is StringValue { Data: "present" }, "Preparation froze metadata before the selected producer completed.");
            Check(File.ReadAllText(Path.Combine(work, "published.txt")) == "present", "The selected publication did not complete.");
        }

        async Task CheckProcessFlowDeferral()
        {
            cases++;
            string work = Path.Combine(root, "held-producer-copy-deferral");
            Directory.CreateDirectory(work);
            TaskCompletionSource<ProcessValue> producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ProcessValue process = new(0, "", "") { Completion = producer.Task };
            CellValue counter = new(new NumberValue(0));
            Module program = new([process, new PathValue("./first.txt"), new PathValue("./second.txt"),
                counter, new FunctionValue(1, []), new NumberValue(1)],
                [new("entry", 0, 1,
                    [new(OpCode.Constant, 0), new(OpCode.GetMember, Text: "stdout"), new(OpCode.Constant, 1),
                     new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Constant, 4), new(OpCode.Constant, 0),
                     new(OpCode.Call, A: 1, B: 2), new(OpCode.Pop), new(OpCode.Constant, 3), new(OpCode.InitializeLocal, 0),
                     new(OpCode.LoadLocal, 0), new(OpCode.Constant, 5), new(OpCode.Add), new(OpCode.StoreLocal, 0),
                     new(OpCode.Null), new(OpCode.Return)], TypeRef.Null),
                 new("copy_then_return_process", 1, 1,
                    [new(OpCode.Constant, 1), new(OpCode.Constant, 2), new(OpCode.Transfer), new(OpCode.Pop),
                     new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Process, ParameterTypes: [TypeRef.Process])],
                Stage: ArtifactStage.Executable, Context: new(work, work), OptimizationLevel: 0) { ExecutionPlans = [] };
            Task<Value> execution = Interpreter.ExecuteAsync(program, new(WorkingDirectory: work)).AsTask();
            bool callerAdvanced = counter.Data is NumberValue { Data: 1 };
            bool executionPending = !execution.IsCompleted;
            bool producerHeld = !producer.Task.IsCompleted;
            bool unpublished = !File.Exists(Path.Combine(work, "first.txt")) && !File.Exists(Path.Combine(work, "second.txt"));
            producer.SetResult(new ProcessValue(0, "payload", ""));
            Value result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Check(callerAdvanced, "Copy preparation waited for a live process before the caller could advance past its discarded helper call.");
            Check(executionPending && producerHeld, "A held process flow did not retain pending execution ownership.");
            Check(unpublished, "The held process flow published before its producer completed.");
            Check(result is NullValue && counter.Data is NumberValue { Data: 1 }, "Deferring the helper copy replayed or skipped the caller prefix.");
            Check(File.ReadAllText(Path.Combine(work, "first.txt")) == "payload"
                && File.ReadAllText(Path.Combine(work, "second.txt")) == "payload", "The promoted helper copy did not preserve its producer dependency.");
        }

        async Task CheckIndependentMetadata(bool observeSibling)
        {
            cases++;
            string work = Path.Combine(root, observeSibling ? "ready-sibling-metadata" : "unselected-held-metadata");
            Directory.CreateDirectory(work);
            if (observeSibling) File.WriteAllText(Path.Combine(work, "ready.txt"), "ready");
            TaskCompletionSource<ProcessValue> producer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ProcessValue process = new(0, "", "") { Completion = producer.Task };
            CellValue counter = new(new NumberValue(0));
            Instruction[] body = [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 3), new(OpCode.Add), new(OpCode.StoreLocal, 0)];
            body = body.Concat(observeSibling
                ? new Instruction[]
                {
                    new(OpCode.LoadLocal, 1), new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.JumpIfFalse, 9),
                    new(OpCode.Constant, 7), new(OpCode.Return),
                    new(OpCode.Constant, 4), new(OpCode.Return)
                }
                : new Instruction[]
                {
                    new(OpCode.False), new(OpCode.JumpIfFalse, 11), new(OpCode.LoadLocal, 1),
                    new(OpCode.NativeCall, 1, Text: "fs.exists"), new(OpCode.Pop), new(OpCode.Constant, 4),
                    new(OpCode.Return), new(OpCode.Constant, 4), new(OpCode.Return)
                }).ToArray();
            Module program = new([process, new PathValue("./held.txt"), new FunctionValue(1, [counter]),
                new NumberValue(1), new StringValue(observeSibling ? "missing" : "unselected"), counter,
                new PathValue(observeSibling ? "./ready.txt" : "./held.txt"), new StringValue("ready")],
                [new("entry", 0, 2,
                    [new(OpCode.Constant, 0), new(OpCode.GetMember, Text: "stdout"), new(OpCode.Constant, 1),
                     new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Constant, 2), new(OpCode.Constant, 6),
                     new(OpCode.Call, A: 1, B: 2), new(OpCode.StoreLocal, 1), new(OpCode.Constant, 5),
                     new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Constant, 3),
                     new(OpCode.Add), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Return)], TypeRef.String),
                 new("observe_selected", 1, 2, body, TypeRef.String, CaptureCount: 1, ParameterTypes: [TypeRef.Path])],
                Stage: ArtifactStage.Executable, Context: new(work, work), OptimizationLevel: 0) { ExecutionPlans = [] };
            Task<Value> execution = Interpreter.ExecuteAsync(program, new(WorkingDirectory: work)).AsTask();
            bool callerAdvanced = counter.Data is NumberValue { Data: 2 };
            bool executionPending = !execution.IsCompleted;
            bool producerHeld = !producer.Task.IsCompleted;
            bool unpublished = !File.Exists(Path.Combine(work, "held.txt"));
            producer.SetResult(new ProcessValue(0, "held-payload", ""));
            Value result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            string expected = observeSibling ? "ready" : "unselected";
            Check(callerAdvanced, "Unselected or sibling metadata waited for an unrelated held producer before the caller could advance.");
            Check(executionPending && producerHeld && unpublished, "The independent observer did not leave the unrelated producer pending.");
            Check(counter.Data is NumberValue { Data: 2 }, "Independent metadata replayed a callee or caller prefix.");
            Check(result is StringValue text && text.Data == expected, "Independent metadata selected the wrong branch or sibling content.");
            Check(File.ReadAllText(Path.Combine(work, "held.txt")) == "held-payload", "The unrelated producer did not finish after release.");
            if (observeSibling)
                Check(File.ReadAllText(Path.Combine(work, "ready.txt")) == "ready", "Observing a sibling modified its existing content.");
        }

        async Task CheckSource(string scenario, string source, int level, string route, Dictionary<string, string> expected)
        {
            cases++;
            string work = Path.Combine(root, scenario + "-O" + level + "-" + route);
            Directory.CreateDirectory(work);
            Module raw = Compiler.Compile(source, scenario + ".msync", level);
            Module program = Prepare(raw, work, route);
            Check(expected.Keys.All(name => !File.Exists(Path.Combine(work, name))), scenario + ": preparation performed source effects.");
            await Interpreter.ExecuteAsync(program, new(WorkingDirectory: work));
            foreach (var output in expected)
            {
                string actual = File.ReadAllText(Path.Combine(work, output.Key));
                Check(actual == output.Value,
                    $"{scenario} O{level} {route}: {output.Key} expected {System.Text.Json.JsonSerializer.Serialize(output.Value)}, got {System.Text.Json.JsonSerializer.Serialize(actual)}.");
            }
        }

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static Module Prepare(Module module, string work, string route) => route switch
    {
        "object" => RoundTrip(module),
        "executable" => RoundTrip(ContextualBackend.Prepare(Binding.Bind(module, new(work, work)), new(WorkingDirectory: work))),
        _ => module
    };

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes);
    }

    private const string CounterSource = """
        let calls = 0;
        fn tick() {
            global calls;
            calls = calls + 1;
            $std::io::append_text(./trace.txt, "tick|");
            calls
        };
        let total = 0;
        for index in $std::ranges::iota(0, 8) { total = total + tick(); };
        (calls as String) -> ./calls.txt;
        (total as String) -> ./total.txt;
        """;

    private const string MetadataSource = """
        let calls = 0;
        fn read_selected(path: Path) {
            global calls;
            calls = calls + 1;
            if exists path { $std::io::read_text(path) } else { "missing" }
        };
        "present" -> ./published.txt;
        read_selected(./published.txt) -> ./result.txt;
        (calls as String) -> ./calls.txt;
        """;
}
