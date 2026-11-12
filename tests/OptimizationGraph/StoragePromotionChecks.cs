using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class StoragePromotionChecks
{
    internal static async Task<int> Run(string directory)
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var conditional = Program(
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
            new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 7),
            new(OpCode.LoadCell, 0, 1), new(OpCode.Pop),
            new(OpCode.LoadStorage, 0), new(OpCode.InitializeLocal, 1),
            new(OpCode.Constant, 1), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Return));
        foreach (var candidate in Artifacts(conditional, directory))
        {
            Check(candidate.Functions[0].Instructions.Count(instruction => instruction.Op == OpCode.LoadCell) == 1,
                $"O{candidate.OptimizationLevel}/{candidate.Stage}: a live raw alias lost its conditional promotion.");
            Check(await Execute(candidate, directory, "promote\n") is NumberValue { Data: 40 },
                $"O{candidate.OptimizationLevel}/{candidate.Stage}: the selected alias did not observe the write.");
            Check(await Execute(candidate, directory, "") is NumberValue { Data: 30 },
                $"O{candidate.OptimizationLevel}/{candidate.Stage}: the unselected arm was spuriously aliased.");
        }

        var acrossStore = Program(new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, 1),
            new(OpCode.Pop), new(OpCode.Constant, 1), new(OpCode.StoreLocal, 0), new(OpCode.LoadStorage, 0),
            new(OpCode.InitializeLocal, 1), new(OpCode.Constant, 2), new(OpCode.StoreLocal, 0),
            new(OpCode.LoadLocal, 1), new(OpCode.Return));
        foreach (var candidate in Artifacts(acrossStore, directory))
            Check(await Execute(candidate, directory, "") is NumberValue { Data: 50 },
                $"O{candidate.OptimizationLevel}/{candidate.Stage}: StoreLocal reset a cell's storage identity.");

        var laterCapture = Program(new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
            new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 1),
            new(OpCode.LoadCell, 0, 1), new(OpCode.Pop), new(OpCode.LoadLocal, 1), new(OpCode.StoreLocal, 0),
            new(OpCode.LoadCell, 0, 1), new(OpCode.InitializeLocal, 2), new(OpCode.Constant, 1),
            new(OpCode.StoreLocal, 2), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.Return));
        foreach (var candidate in Artifacts(laterCapture, directory))
            Check(await Execute(candidate, directory, "") is NumberValue { Data: 70 },
                $"O{candidate.OptimizationLevel}/{candidate.Stage}: a later capture did not preserve the original outer Cell.");

        var dead = Program(new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, 1),
            new(OpCode.Pop), new(OpCode.LoadStorage, 0), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Return));
        var reset = Program(new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0, 1),
            new(OpCode.Pop), new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 0), new(OpCode.LoadStorage, 0), new(OpCode.Return));
        foreach (var (source, expected) in new[] { (dead, 30d), (reset, 40d) })
            foreach (var candidate in Artifacts(source, directory, includeUnoptimized: false))
            {
                Check(candidate.Functions[0].Instructions.All(instruction => instruction.Op != OpCode.LoadCell),
                    $"O{candidate.OptimizationLevel}/{candidate.Stage}: an unobservable promotion was retained.");
                Check(await Execute(candidate, directory, "") is NumberValue result && result.Data == expected,
                    $"O{candidate.OptimizationLevel}/{candidate.Stage}: deleting an unobservable promotion changed the value.");
            }
        return assertions;
    }

    private static Module Program(params Instruction[] code) =>
        new([new NumberValue(30), new NumberValue(40), new NumberValue(50)],
            [new FunctionCode("main", 0, 3, code, TypeRef.Number) { CodeSignature = new([], 0, []) }],
            OptimizationLevel: 0) { CodeTypes = [new(TypeKind.Number)] };

    private static IEnumerable<Module> Artifacts(Module source, string directory, bool includeUnoptimized = true)
    {
        for (int level = includeUnoptimized ? 0 : 1; level <= 3; level++)
        {
            var portable = Optimizer.OptimizeInstructions(source, level);
            foreach (var module in new[] { portable, Binding.Bind(portable, new(directory, directory)) })
            {
                using MemoryStream bytes = new();
                Artifact.Write(module, bytes);
                bytes.Position = 0;
                yield return Artifact.Read(bytes);
            }
        }
    }

    private static async Task<Value> Execute(Module module, string directory, string input)
    {
        using StringReader reader = new(input);
        return await Interpreter.ExecuteAsync(module, new ExecutionOptions(WorkingDirectory: directory, Output: TextWriter.Null, Error: TextWriter.Null)
        { Input = reader });
    }
}
