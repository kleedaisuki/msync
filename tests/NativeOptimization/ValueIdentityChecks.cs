using Msync.Core;
using Msync.Core.Native;

internal static class ValueIdentityChecks
{
    private static readonly NativeCodeType[] Types = [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Number),
        new(TypeKind.Never, Pointee: 1), new(TypeKind.Bool)];

    internal static int Run()
    {
        NativeFunctionCode colored = new("colored", [new(1, NativeSlotRole.Capture), new(0), new(2),
                new(1, NativeSlotRole.Local), new(2), new(1, NativeSlotRole.Local), new(2)],
            [new(NativeOp.FrameAddress, 2, 0), new(NativeOp.Allocate, 1, 0),
                new(NativeOp.AddressOffset, 2, 1, 0, 0), new(NativeOp.BindLocation, A: 3, B: 2),
                new(NativeOp.FrameAddress, 2, 3), new(NativeOp.Copy, 4, 2), new(NativeOp.FrameAddress, 2, 5),
                new(NativeOp.BindLocation, A: 5, B: 4), new(NativeOp.FrameAddress, 6, 5), new(NativeOp.Nop),
                new(NativeOp.Return, A: 1)], new([], 0, [new(1, NativeCaptureMode.Location)]), CaptureCount: 1);
        NativeAddressAnalysis facts = Facts(colored);
        Require(facts.At(1, 2).Unknown && facts[2].Unknown, "The old capture pointer remains unknown in its value and lifetime summary");
        Require(!facts.At(3, 2).Unknown && facts.At(3, 2).Targets.SetEquals([new(1, -1, 0)]),
            "A colored pointer's new definition must not inherit the capture's unknown origin");
        Require(facts.At(5, 2).Targets.SetEquals([new(1, -1, 0)]), "FrameAddress after Bind must name the bound heap field only");
        Require(facts.At(9, 2).Targets.SetEquals([new(-1, 5)]), "A saved pre-Bind pointer must keep the original frame cell");
        Require(facts.At(9, 6).Targets.SetEquals([new(1, -1, 0)]), "A new post-Bind pointer must use the new binding");
        Require(facts.ProvenLayoutsAt(2, 1)?.SetEquals([0]) == true, "Current allocation layout proof must survive slot coloring");
        NativeFunctionCode returned = colored with
        {
            Instructions = [.. colored.Instructions.Take(9), new(NativeOp.Copy, 2, 6), new(NativeOp.Nop), new(NativeOp.Return, A: 2)],
            Signature = colored.Signature with { Result = 2 }
        };
        NativeProgram returnedProgram = Program(returned);
        NativeArtifact.Validate(returnedProgram);
        NativeEscapeAnalysis returnedEscape = new(returnedProgram, 0);
        Require(returnedEscape.EscapingLocations.Count == 0 && returnedEscape.EscapingAllocations.Contains(1),
            "Returning a new colored heap pointer must not escape an earlier frame pointer stored in that slot");
        NativeFunctionCode diamond = new("diamond", [new(3, NativeSlotRole.Parameter), new(0), new(0), new(2)],
            [new(NativeOp.Allocate, 1, 0), new(NativeOp.Allocate, 2, 0), new(NativeOp.Branch, A: 0, B: 3, C: 5),
                new(NativeOp.AddressOffset, 3, 1, 0, 0), new(NativeOp.Jump, A: 7),
                new(NativeOp.AddressOffset, 3, 2, 0, 0), new(NativeOp.Jump, A: 7), new(NativeOp.Nop),
                new(NativeOp.Return, A: 1)], new([3], 0, []), ParameterCount: 1);
        Require(Facts(diamond).At(7, 3).Targets.SetEquals([new(0, -1, 0), new(1, -1, 0)]),
            "Both diamond predecessors must reach the join");
        NativeFunctionCode loop = diamond with { Instructions = [new(NativeOp.Allocate, 1, 0), new(NativeOp.Allocate, 2, 0),
            new(NativeOp.AddressOffset, 3, 1, 0, 0), new(NativeOp.Nop), new(NativeOp.AddressOffset, 3, 2, 0, 0),
            new(NativeOp.Branch, A: 0, B: 3, C: 6), new(NativeOp.Nop), new(NativeOp.Return, A: 1)] };
        Require(Facts(loop).At(3, 3).Targets.SetEquals([new(0, -1, 0), new(1, -1, 0)]),
            "A loop header must include both entry and backedge values");
        NativeFunctionCode mixed = new("mixed", [new(2, NativeSlotRole.Parameter), new(3, NativeSlotRole.Parameter), new(0), new(2)],
            [new(NativeOp.Allocate, 2, 0), new(NativeOp.Branch, A: 1, B: 2, C: 4), new(NativeOp.Copy, 3, 0),
                new(NativeOp.Jump, A: 6), new(NativeOp.AddressOffset, 3, 2, 0, 0), new(NativeOp.Jump, A: 6),
                new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([2, 3], 0, []), ParameterCount: 2);
        NativeAddressAnalysis joined = Facts(mixed);
        Require(joined.At(6, 3).Unknown && joined.At(6, 3).Targets.Contains(new(0, -1, 0)),
            "An unknown predecessor must not become bottom when joined with a known value");
        NativeFunctionCode rebound = new("rebound", [new(3, NativeSlotRole.Parameter), new(0), new(0), new(2),
                new(1, NativeSlotRole.Local), new(2), new(2)],
            [new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 5, 1, 0, 0), new(NativeOp.FrameAddress, 6, 4),
                new(NativeOp.Nop), new(NativeOp.FrameAddress, 3, 4), new(NativeOp.BindLocation, A: 4, B: 5),
                new(NativeOp.Branch, A: 0, B: 3, C: 7), new(NativeOp.Nop), new(NativeOp.Return, A: 1)],
            new([3], 0, []), ParameterCount: 1);
        NativeAddressAnalysis boundLoop = Facts(rebound);
        Require(boundLoop.At(5, 3).Targets.SetEquals([new(-1, 4), new(0, -1, 0)]),
            "A loop-carried binding must preserve the first iteration's original cell alternative");
        Require(boundLoop.At(7, 6).Targets.SetEquals([new(-1, 4)]), "Loop rebindings must not retarget an earlier pointer value");
        return 12;
    }

    private static NativeAddressAnalysis Facts(NativeFunctionCode function)
    {
        NativeProgram program = Program(function);
        NativeArtifact.Validate(program);
        return new(program, function);
    }

    private static NativeProgram Program(NativeFunctionCode function) => new([], [function], Types, [new([new("number", 1)])]);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
