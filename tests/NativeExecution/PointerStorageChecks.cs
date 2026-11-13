using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class PointerStorageChecks
{
    internal static async Task<int> RunAsync()
    {
        await Task.WhenAll(
            Check(ReboundNamespace(), 20, "Rebinding a namespace location must replace its pointer value"),
            Check(IndirectPointerStore(), 20, "An indirect pointer store must replace the subsequently dereferenced address"),
            Check(CrossFramePointerStore(), 20, "A callee's pointer write must be visible to its caller"),
            Check(ProjectedPointerReload(), 20, "Pointer reloads must retain the selected physical ABI, not reinterpret it as the logical sum"));
        await ReturnedPendingPointer();
        await HostPointerAdmission();
        await NestedPointerStoreAdmission();
        await PendingStoreAdmission();
        Console.WriteLine("PASS pointer-storage: rebound locations and indirect pointer writes use current values");
        return 14;
    }

    private static NativeProgram ReboundNamespace() => new(
        [new(NativeKind.Number, Number: 10), new(NativeKind.Number, Number: 20)],
        [new("rebound-namespace", [new(1), new(1), new(3), new(2), new(0)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Allocate, 1, 0),
             new(NativeOp.Constant, 4, 0), new(NativeOp.AddressOffset, 2, 0, 0, 0),
             new(NativeOp.Store, A: 2, B: 4, C: 1, Type: 0),
             new(NativeOp.Constant, 4, 1), new(NativeOp.AddressOffset, 2, 1, 0, 0),
             new(NativeOp.Store, A: 2, B: 4, C: 1, Type: 0),
             new(NativeOp.FrameAddress, 3, 1), new(NativeOp.BindLocation, A: 0, B: 3),
             new(NativeOp.AddressOffset, 2, 0, 0, 0), new(NativeOp.Load, 4, 2, Type: 0),
             new(NativeOp.Return, A: 4)], new([], 0, []))],
        [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]),
         new(TypeKind.Never, Pointee: 1), new(TypeKind.Never, Pointee: 0)],
        [new([new("value", 0)])]);

    private static NativeProgram IndirectPointerStore() => new(
        [new(NativeKind.Number, Number: 10), new(NativeKind.Number, Number: 20)],
        [new("indirect-pointer-store", [new(1), new(1), new(2), new(2), new(0), new(3)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Allocate, 1, 0),
             new(NativeOp.Constant, 4, 0), new(NativeOp.AddressOffset, 2, 0, 0, 0),
             new(NativeOp.Store, A: 2, B: 4, C: 1, Type: 0),
             new(NativeOp.Constant, 4, 1), new(NativeOp.AddressOffset, 3, 1, 0, 0),
             new(NativeOp.Store, A: 3, B: 4, C: 1, Type: 0),
             new(NativeOp.FrameAddress, 5, 2), new(NativeOp.Store, A: 5, B: 3, C: 0, Type: 2),
             new(NativeOp.Load, 4, 2, Type: 0), new(NativeOp.Return, A: 4)], new([], 0, []))],
        [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]),
         new(TypeKind.Never, Pointee: 0), new(TypeKind.Never, Pointee: 2)],
        [new([new("value", 0)])]);

    private static NativeProgram CrossFramePointerStore()
    {
        NativeProgram original = IndirectPointerStore();
        NativeFunctionCode main = original.Functions[0];
        return original with
        {
            Constants = [.. original.Constants, new(NativeKind.Null)],
            Types = [.. original.Types, new(TypeKind.Null), new(TypeKind.Function)],
            Functions = [main with
            {
                Slots = [.. main.Slots, new(5), new(4)],
                Instructions = [.. main.Instructions.Take(9),
                    new(NativeOp.MakeFunction, 6, 1, C: 0),
                    new(NativeOp.Call, 7, 6, 1, Inputs: [5, 3]),
                    .. main.Instructions.Skip(10)]
            }, new("write-pointer", [new(3, NativeSlotRole.Parameter), new(2, NativeSlotRole.Parameter), new(4)],
                [new(NativeOp.Store, A: 0, B: 1, C: 0, Type: 2), new(NativeOp.Constant, 2, 2), new(NativeOp.Return, A: 2)],
                new([3, 2], 4, []), ParameterCount: 2)]
        };
    }

    private static NativeProgram ProjectedPointerReload() => new(
        [new(NativeKind.Number, Number: 20)],
        [new("projected-pointer-reload", [new(1), new(0), new(5), new(3), new(4), new(3), new(2), new(0)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Constant, 1, 0),
             new(NativeOp.AddressOffset, 2, 0, 0, 0), new(NativeOp.Store, A: 2, B: 1, C: 1, Type: 0),
             new(NativeOp.ProjectAddress, 3, 0, 0), new(NativeOp.FrameAddress, 4, 3),
             new(NativeOp.Load, 5, 4, Type: 3), new(NativeOp.Load, 6, 5, Type: 2),
             new(NativeOp.Narrow, 7, 6, Type: 0), new(NativeOp.Return, A: 7)], new([], 0, []))],
        [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Null | TypeKind.Number),
         new(TypeKind.Never, Pointee: 2), new(TypeKind.Never, Pointee: 3), new(TypeKind.Never, Pointee: 0)],
        [new([new("value", 0)])])
    { Projections = [new([new(0, 0)])] };

    private static async Task HostPointerAdmission()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 7), new(NativeKind.Null)],
            [new("write-number", [new(2, NativeSlotRole.Parameter), new(0)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Store, A: 0, B: 1, C: 0, Type: 0), new(NativeOp.Return, A: 1)],
                new([2], 0, []), ParameterCount: 1),
             new("write-nullable", [new(3, NativeSlotRole.Parameter), new(1)],
                [new(NativeOp.Constant, 1, 1), new(NativeOp.Store, A: 0, B: 1, C: 0, Type: 1), new(NativeOp.Return, A: 1)],
                new([3], 1, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Number | TypeKind.Null),
             new(TypeKind.Never, Pointee: 0), new(TypeKind.Never, Pointee: 1)], []);
        NativeArtifact.Validate(program);
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes));
        NativeAllocation number = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        NativeValue written = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(number.Field(0))]);
        if (written.AsNumber() != 7 || number.Field(0).Load().AsNumber() != 7)
            throw new Exception("An uninitialized output pointer was read or rejected before assignment");
        NativeAllocation nullable = new(new NativeLayout(NativeType.Union(NativeType.Of(NativeKind.Number), NativeType.Of(NativeKind.Null))));
        if ((await interpreter.InvokeAsync(NativeValue.FromFunction(new(1)), [NativeValue.FromAddress(nullable.Field(0))])).Kind != NativeKind.Null
            || nullable.Field(0).Load().Kind != NativeKind.Null)
            throw new Exception("A nullable output pointer lost its physical sum ABI");
        await Reject(1, NativeValue.FromAddress(number.Field(0)));
        if (number.Field(0).Load().AsNumber() != 7) throw new Exception("Rejected nullable pointer ABI corrupted a Number location");
        NativeAllocation boolean = new(new NativeLayout(NativeType.Of(NativeKind.Bool)));
        boolean.Field(0).Store(NativeValue.False);
        await Reject(0, NativeValue.FromAddress(boolean.Field(0)));
        await Reject(0, NativeValue.FromAddress(boolean.Field(0).OffsetBy(default, NativeType.Of(NativeKind.Number))));
        await Reject(0, NativeValue.FromNumber(7));
        NativeAllocation untyped = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(untyped.Address)]);
        if (untyped.Field(0).Load().AsNumber() != 7) throw new Exception("An untyped but layout-valid output location was rejected");

        async Task Reject(int function, NativeValue argument)
        {
            try { await interpreter.InvokeAsync(NativeValue.FromFunction(new(function)), [argument]); }
            catch (MsyncException error) when (error.Message.Contains("invariant pointer", StringComparison.Ordinal)) { return; }
            throw new Exception("Host pointer ABI mismatch was not rejected at the invocation boundary");
        }
    }

    private static async Task NestedPointerStoreAdmission()
    {
        NativeProgram program = new([new(NativeKind.Null)],
            [new("write-through-nested-pointer", [new(4, NativeSlotRole.Parameter), new(3), new(5)],
                [new(NativeOp.Load, 1, 0, Type: 3), new(NativeOp.Constant, 2, 0),
                 new(NativeOp.Store, A: 1, B: 2, C: 0, Type: 1), new(NativeOp.Return, A: 2)],
                new([4], 5, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Number | TypeKind.Null), new(TypeKind.Never, Pointee: 0),
             new(TypeKind.Never, Pointee: 1), new(TypeKind.Never, Pointee: 3), new(TypeKind.Null)], []);
        NativeArtifact.Validate(program);
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes));
        NativeAllocation number = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        number.Field(0).Store(NativeValue.FromNumber(7));
        NativeAllocation cell = new(new NativeLayout(NativeType.Of(NativeKind.Address)));
        cell.Field(0).Store(NativeValue.FromAddress(number.Field(0)));
        try
        {
            await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(cell.Field(0))]);
            throw new Exception("Nested pointer widened its actual Number storage and accepted Null");
        }
        catch (MsyncException error) when (error.Message.Contains("actual physical location", StringComparison.Ordinal)) { }
        if (number.Field(0).Load().AsNumber() != 7)
            throw new Exception("A rejected nested pointer store changed its destination");
    }

    private static async Task PendingStoreAdmission()
    {
        NativeProgram program = new([new(NativeKind.String, "payload"), new(NativeKind.Path, "destination"), new(NativeKind.Null)],
            [new("store-pending-wrong-physical-kind", [new(4, NativeSlotRole.Parameter), new(3), new(6), new(7), new(8), new(5)],
                [new(NativeOp.Load, 1, 0, Type: 3), new(NativeOp.Constant, 2, 0), new(NativeOp.Constant, 3, 1),
                 new(NativeOp.Transfer, 4, 2, 3), new(NativeOp.Store, A: 1, B: 4, C: 0, Type: 1),
                 new(NativeOp.Constant, 5, 2), new(NativeOp.Return, A: 5)], new([4], 5, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Number | TypeKind.Bool), new(TypeKind.Never, Pointee: 0),
             new(TypeKind.Never, Pointee: 1), new(TypeKind.Never, Pointee: 3), new(TypeKind.Null),
             new(TypeKind.String), new(TypeKind.Path), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(program);
        NativeRuntimeContext context = new() { TransferAsync = static (_, _, _, _) => new(true) };
        await using NativeInterpreter interpreter = new(program, context: context);
        NativeAllocation number = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
        number.Field(0).Store(NativeValue.FromNumber(7));
        NativeAllocation cell = new(new NativeLayout(NativeType.Of(NativeKind.Address)));
        cell.Field(0).Store(NativeValue.FromAddress(number.Field(0)));
        try
        {
            await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(cell.Field(0))]);
            throw new Exception("A pending Bool producer was admitted to actual Number storage");
        }
        catch (MsyncException error) when (error.Message.Contains("actual physical location", StringComparison.Ordinal)) { }
        if (number.Field(0).Load().AsNumber() != 7) throw new Exception("Rejected pending data corrupted the physical Number location");
    }

    private static async Task ReturnedPendingPointer()
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeRuntimeContext context = new()
        { TransferAsync = async (_, _, _, token) => await completion.Task.WaitAsync(token) };
        NativeProgram program = new([new(NativeKind.String, "payload"), new(NativeKind.Path, "destination")],
            [new("return-pending-pointer", [new(0), new(1), new(2), new(3)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
                 new(NativeOp.Transfer, 2, 0, 1), new(NativeOp.FrameAddress, 3, 2), new(NativeOp.Return, A: 3)],
                new([], 3, [])),
             new("load-returned-pointer", [new(3, NativeSlotRole.Parameter), new(2)],
                [new(NativeOp.Load, 1, 0, Type: 2), new(NativeOp.Return, A: 1)], new([3], 2, []), ParameterCount: 1)],
            [new(TypeKind.String), new(TypeKind.Path), new(TypeKind.Bool), new(TypeKind.Never, Pointee: 2)], []);
        NativeArtifact.Validate(program);
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes), context: context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NativeValue pointer = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [], timeout.Token);
        if (completion.Task.IsCompleted || pointer.AsAddress().IsInitialized)
            throw new Exception("A returned pending pointer was demanded before its owner released the transfer");
        completion.SetResult(true);
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(1)), [pointer], timeout.Token);
        if (!result.AsBool()) throw new Exception("Retiring a function discarded a returned pointer's pending memory");
    }

    private static async Task Check(NativeProgram program, double expected, string message)
    {
        NativeArtifact.Validate(program);
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes));
        NativeValue result = await interpreter.ExecuteAsync();
        if (result.Kind != NativeKind.Number || result.AsNumber() != expected)
            throw new Exception($"{message}: expected {expected}, received {result}");
    }
}
