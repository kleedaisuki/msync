using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class BorrowedFrameChecks
{
    internal static async Task RunAsync()
    {
        string root = Environment.CurrentDirectory;
        Module closure = ClosureResult();
        foreach (Module image in new[] { closure, Binding.Bind(closure, new(root, root)) })
        {
            using MemoryStream bytes = new();
            Artifact.Write(image, bytes);
            bytes.Position = 0;
            Module decoded = Artifact.Read(bytes, formBlocks: false);
            RuntimeProfile profile = new();
            var result = await Interpreter.ExecuteAsync(decoded, new() { Profile = profile });
            Require(result is NumberValue { Data: 127 },
                "Borrowed frame changed a captured write, a returned Cell, or the value below the call");
            var frames = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
            var mutate = frames.Single(frame => frame.Attributes["function.name"] == "mutate");
            var read = frames.Single(frame => frame.Attributes["function.name"] == "read");
            Require(mutate.Metrics["borrowed_frames"] == 1 && mutate.Metrics["detached_frames"] == 1,
                "A borrowed frame must detach once across multiple local writes");
            Require(read.Metrics["borrowed_frames"] == 1 && read.Metrics["detached_frames"] == 0,
                "A read-only returned closure should borrow its captured slot");
        }

        RuntimeProfile sessionProfile = new();
        await using var session = new ExecutionSession(new() { Profile = sessionProfile });
        Require(await session.ExecuteAsync(closure) is NumberValue { Data: 127 },
            "Session frame storage changed result");
        Require(sessionProfile.Drain().Where(sample => sample.Name == "vm.function")
            .All(frame => frame.Metrics["borrowed_frames"] == 0), "A session borrowed from its persistent frame");

        Module raw = NestedCellArgument();
        Require(await Interpreter.ExecuteAsync(raw) is NumberValue { Data: 84 },
            "Source argument normalization must dereference exactly once and detachment must retain the inner Cell");
    }

    private static Module ClosureResult() => new(
        [new NumberValue(10), new NumberValue(100), new NumberValue(2), new NumberValue(3), new NumberValue(7)],
        [
            new("main", 0, 1, [
                new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.Constant, 1),
                new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 1, 1),
                new(OpCode.Constant, 2), new(OpCode.Constant, 3), new(OpCode.Add), new(OpCode.Call, 1, 2),
                new(OpCode.Call, 0, 3), new(OpCode.Add), new(OpCode.LoadLocal, 0), new(OpCode.Add), new(OpCode.Return)
            ], TypeRef.Number),
            new("mutate", 1, 2, [
                new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.StoreLocal, 0),
                new(OpCode.LoadLocal, 1), new(OpCode.Constant, 4), new(OpCode.Add), new(OpCode.InitializeLocal, 1),
                new(OpCode.LoadCell, 1), new(OpCode.MakeClosure, 2, 1), new(OpCode.Return)
            ], TypeRef.Function, CaptureCount: 1, ParameterTypes: [TypeRef.Number]),
            new("read", 0, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Number, CaptureCount: 1)
        ], OptimizationLevel: 0);

    private static Module NestedCellArgument() => new(
        [new NumberValue(10), new NumberValue(1), new NumberValue(42)],
        [
            new("main", 0, 2, [
                new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0),
                new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 1),
                new(OpCode.LoadCell, 1), new(OpCode.Pop),
                new(OpCode.LoadCell, 0), new(OpCode.StoreLocal, 1),
                new(OpCode.MakeClosure, 1), new(OpCode.LoadCell, 1), new(OpCode.Call, 1, 2),
                new(OpCode.LoadLocal, 0), new(OpCode.Add), new(OpCode.Return)
            ], TypeRef.Number),
            new("write", 1, 1, [
                new(OpCode.Constant, 2), new(OpCode.StoreLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Return)
            ], TypeRef.Number, ParameterTypes: [TypeRef.Number])
        ], OptimizationLevel: 0);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
