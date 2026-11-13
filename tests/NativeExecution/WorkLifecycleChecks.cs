using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class WorkLifecycleChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeProgram program = new([new(NativeKind.String, Text: "payload"), new(NativeKind.Path, Text: "destination")],
            [new("work", [new(0), new(1), new(2)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1), new(NativeOp.Transfer, 2, 0, 1), new(NativeOp.Return)],
                new([], 3, []))],
            [new(TypeKind.String), new(TypeKind.Path), new(TypeKind.Bool), new(TypeKind.Null)], []);
        TaskCompletionSource<NativeProcess> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeRuntimeContext context = new();
        context.TransferAsync = (_, _, _, _) =>
        {
            context.ProcessCreated!(new(0, "", "") { Completion = release.Task });
            return ValueTask.FromException<bool>(new FormatException("selected transfer host failure"));
        };
        await using NativeInterpreter interpreter = new(program, context: context);
        Task<NativeValue> pending = interpreter.ExecuteAsync().AsTask();
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(100)));
        bool joined = !pending.IsCompleted;
        release.SetResult(new(0, "joined", ""));
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("Selected host failure was ignored");
        }
        catch (FormatException error) when (error.Message == "selected transfer host failure") { }
        if (!joined) throw new Exception("A failed future skipped the remaining owned Process join");
        Console.WriteLine("PASS work-lifetime: selected future failure does not bypass remaining owned Process cleanup");
        return 1;
    }
}
