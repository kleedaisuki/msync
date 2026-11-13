using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;
using Msync.Runtime;
using static RuntimeFixture;

internal static class BorrowedFrameChecks
{
    internal static async Task RunAsync()
    {
        // Borrow/detach was an old stack-frame mechanism. The contract is shared locations across return.
        await Number("""
            let current = 10;
            fn mutate(argument: Number) {
                current = current + argument;
                let returned = argument + 7;
                fn() { returned }
            };
            let below = 100;
            let read = mutate(2 + 3);
            below + read() + current
            """, 127);
        await Number("""
            let location = scope { value: 10 };
            fn write(alias) { alias::value = 42; alias::value };
            write(location) + location::value
            """, 84);
        NativeProgram local = new([new(NativeKind.Number, Number: 7)], [new("private", [new(0), new(1)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Constant, 1, 0), new(NativeOp.Return, A: 1)], new([], 1, []))],
            [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Number)], [new([])])
            { Allocations = [new(0, 0, NativeAllocationPlacement.Frame)] };
        Check(NativeAllocationSafety.FrameSafe(local, 0).Contains(0), "Private acyclic allocation lost placement proof");
        RuntimeProfile profile = new();
        Check((await Run(Reload(local), new() { Profile = profile })).AsNumber() == 7, "Frame placement changed result");
        Check(profile.Drain().Single(sample => sample.Name == "vm.function").Metrics["frame_placed_allocations"] == 1,
            "Native frame placement was not observed");
        NativeProgram escaping = local with { Functions = [local.Functions[0] with
            { Signature = new([], 0, []), Instructions = [new(NativeOp.Allocate, 0, 0), new(NativeOp.Return, A: 0)] }] };
        Check(Reload(escaping).Allocations[0].Placement == NativeAllocationPlacement.LogicalHeap,
            "Untrusted escaping frame hint was not downgraded");
        Console.WriteLine("PASS native shared-location returns and frame/heap placement controls");
    }
}
