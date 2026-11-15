using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Main(string[] args)
    {
        if (args.Contains("--storage-copy"))
        {
            Console.WriteLine($"PASS storage copy {StorageCopyChecks.Run()}");
            return;
        }
        AllBuiltinStorage();
        ExactConstants();
        ScalarTextConstants();
        PathDefinitionOrigins();
        LayoutMetadata();
        TypeMetadataOperations();
        MutableLocationSnapshot();
        checks += MemoryBarrierChecks.Run();
        checks += ProvenanceChecks.Run();
        checks += ValueIdentityChecks.Run();
        checks += InterproceduralChecks.Run();
        checks += TypeBridgeChecks.Run();
        EscapesAndLoops();
        AcquireAndFailure();
        Console.WriteLine($"PASS {checks} native optimization observations");
    }

    private static NativeCodeType CodeType(TypeKind kind) => new(kind);
    private static NativeValue[] Samples() =>
    [
        NativeValue.Null, NativeValue.True, NativeValue.FromNumber(-0d),
        NativeValue.FromString("可莉𐐀/é"),
        NativeValue.FromPath(new("a\\b", "/origin", new(ConnectionKind.Ssh, "configured-alias"), "/home", "captured")),
        NativeValue.FromConnection(new(ConnectionKind.Wsl, "distribution")),
        NativeValue.FromFunction(new(31)),
        NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(3)], TypeRef.Number)),
        NativeValue.FromProcess(new(7, "out", "err")),
        NativeValue.FromType(new(TypeKind.Range, TypeRef.Path)),
        NativeValue.FromAddress(new NativeAllocation(new NativeLayout(), 1).Address)
    ];

    private static void AllBuiltinStorage()
    {
        foreach (NativeValue input in Samples())
        {
            NativeCodeType item = new(input.Type.Kind, Layouts: input.Kind == NativeKind.Address ? [1] : null);
            NativeProgram original = new([], [new("identity", [new(0, NativeSlotRole.Parameter), new(1), new(2), new(0)],
                [new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
                 new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0), new(NativeOp.Load, 3, 2, Type: 0),
                 new(NativeOp.Return, A: 3)], new([0], 0, []), ParameterCount: 1)],
                [item, new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
                [new([new("value", 0)]), new([])]);
            NativeArtifact.Validate(original);
            NativeProgram optimized = NativeOptimizer.Optimize(original);
            NativeArtifact.Validate(optimized);
            Check(Machine.Run(original, [input]) == input, input.Kind + ": oracle input");
            Check(Machine.Run(optimized, [input]) == input, input.Kind + ": native descriptor changed");
            Check(optimized.Functions[0].Instructions.All(instruction => instruction.Op is not (NativeOp.Allocate or NativeOp.Load or NativeOp.Store or NativeOp.BindLocation)),
                input.Kind + ": unobserved logical storage not eliminated");
            Check(optimized.Allocations.Count == 0, input.Kind + ": removed allocation retained a runtime placement record");
            NativeProgram twice = NativeOptimizer.Optimize(optimized);
            Check(twice.Functions[0].Instructions.SequenceEqual(optimized.Functions[0].Instructions), input.Kind + ": not idempotent");
            using MemoryStream stream = new(); NativeArtifact.Write(optimized, stream); stream.Position = 0;
            Check(Machine.Run(NativeArtifact.Read(stream), [input]) == input, input.Kind + ": detached artifact changed");
        }
    }

    private static void ExactConstants()
    {
        double[] boundaries = [0d, -0d, double.Epsilon, -double.Epsilon, double.MaxValue,
            double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            BitConverter.UInt64BitsToDouble(0x7ff8000000001234), BitConverter.UInt64BitsToDouble(0xfff8000000005678)];
        foreach (double first in boundaries)
            foreach (double second in boundaries)
                foreach (NativeBinary operation in Enum.GetValues<NativeBinary>().Where(operation => operation <= NativeBinary.GreaterEqual))
                    Arithmetic(first, second, operation);
        Random random = new(9031);
        for (int sample = 0; sample < 512; sample++)
            Arithmetic(BitConverter.Int64BitsToDouble(random.NextInt64()), BitConverter.Int64BitsToDouble(random.NextInt64()),
                (NativeBinary)random.Next((int)NativeBinary.GreaterEqual + 1));

        NativeProgram zeros = ArithmeticProgram(0d, -0d, NativeBinary.Add);
        NativeProgram folded = NativeOptimizer.Optimize(zeros);
        Check(folded.Constants.Any(value => value.Kind == NativeKind.Number && BitConverter.DoubleToUInt64Bits(value.Number) == 0), "+0 lost");
        Check(folded.Constants.Any(value => value.Kind == NativeKind.Number && BitConverter.DoubleToUInt64Bits(value.Number) == (1UL << 63)), "-0 interning lost");
    }

    private static void ScalarTextConstants()
    {
        string[] samples = ["", "a", "aa", "é", "漢", "\ue000", "𐀀", "𐀀a", "𐀁", "\U0010ffff"];
        foreach (string first in samples)
            foreach (string second in samples)
                foreach (NativeBinary operation in new[] { NativeBinary.Equal, NativeBinary.NotEqual,
                    NativeBinary.Less, NativeBinary.LessEqual, NativeBinary.Greater, NativeBinary.GreaterEqual })
                    ScalarTextConstant(first, second, operation);
    }

    private static void ScalarTextConstant(string first, string second, NativeBinary operation)
    {
        int[] left = first.EnumerateRunes().Select(rune => rune.Value).ToArray();
        int[] right = second.EnumerateRunes().Select(rune => rune.Value).ToArray();
        int order = left.AsSpan().SequenceCompareTo(right);
        bool expected = operation switch
        {
            NativeBinary.Equal => order == 0, NativeBinary.NotEqual => order != 0,
            NativeBinary.Less => order < 0, NativeBinary.LessEqual => order <= 0,
            NativeBinary.Greater => order > 0, NativeBinary.GreaterEqual => order >= 0,
            _ => throw new InvalidOperationException()
        };
        NativeProgram program = new([new(NativeKind.String, first), new(NativeKind.String, second)],
            [new("scalar-text", [new(0), new(0), new(1)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
                 new(NativeOp.Binary, 2, 0, 1, Immediate: (long)operation), new(NativeOp.Return, A: 2)],
                new([], 1, []))], [new(TypeKind.String), new(TypeKind.Bool)], []);
        NativeProgram optimized = NativeOptimizer.Optimize(program);
        NativeArtifact.Validate(optimized);
        Check(!optimized.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Binary),
            "Known scalar-text comparison did not fold");
        Check(Machine.Run(optimized, []).AsBool() == expected, "Text folding changed Unicode scalar order");
    }

    private static void TypeMetadataOperations()
    {
        NativeProgram dynamic = new([], [new("evaluated-type", [new(0, NativeSlotRole.Parameter), new(1), new(1)],
            [new(NativeOp.Unary, 1, 0, Immediate: (long)NativeUnary.ValueType),
             new(NativeOp.MakeType, 2, 1, Immediate: (long)TypeKind.Range), new(NativeOp.Return, A: 2)],
             new([0], 1, []), ParameterCount: 1)], [new(TypeKind.Number | TypeKind.Bool), new(TypeKind.Type)], []);
        NativeProgram optimized = NativeOptimizer.Optimize(dynamic);
        Check(optimized.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Unary
            && instruction.Immediate == (long)NativeUnary.ValueType), "ValueType incorrectly folded from a declared union instead of evaluated payload.");
        Check(optimized.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.MakeType),
            "Dynamic element descriptor construction was incorrectly constant folded.");
        NativeProgram constant = new([new(NativeKind.Number, Number: 7)], [new("constant-type", [new(0), new(1), new(1)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Unary, 1, 0, Immediate: (long)NativeUnary.ValueType),
             new(NativeOp.MakeType, 2, 1, Immediate: (long)TypeKind.Storage), new(NativeOp.Return, A: 2)],
             new([], 1, []))], [new(TypeKind.Number), new(TypeKind.Type)], []);
        Check(Machine.Run(NativeOptimizer.Optimize(constant), []).AsType() == new TypeRef(TypeKind.Storage, TypeRef.Number),
            "Known actual payload Type and nested historical metadata failed to fold exactly.");
        Check(!NativeInstructionFacts.MemoryBarrier(new(NativeOp.MakeType)), "Pure Type descriptor construction became a memory barrier.");
        NativeProgram local = new([], [new("local-type", [new(0), new(1)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Unary, 1, 0, Immediate: (long)NativeUnary.ValueType),
             new(NativeOp.Return, A: 1)], new([], 1, []))], [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Type)], [new([])]);
        Check(NativeAllocationSafety.FrameSafe(local, 0).Contains(0),
            "Returning a Type descriptor incorrectly makes its observed allocation escape.");
    }

    private static void LayoutMetadata()
    {
        NativeProgram original = new([new(NativeKind.Number, Number: 7)],
            [new("non-address-layout", [new(0), new(1)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Unary, 1, 0, 0, Immediate: (long)NativeUnary.HasLayout),
                 new(NativeOp.Return, A: 1)], new([], 1, []))], [new(TypeKind.Number), new(TypeKind.Bool)], [new([])]);
        NativeProgram optimized = NativeOptimizer.Optimize(original);
        Check(!Machine.Run(optimized, []).AsBool(), "A non-Address constant acquired a namespace layout tag.");
        Check(optimized.Functions[0].Instructions.All(instruction => instruction.Op != NativeOp.Unary),
            "Known non-Address HasLayout did not fold to false.");
    }

    private static void PathDefinitionOrigins()
    {
        NativeProgram original = new(
            [new(NativeKind.Path, "same") { OriginId = 1, OriginOffset = 7 },
             new(NativeKind.Path, "same") { OriginId = 2, OriginOffset = 7 }],
            [new("path-origins", [new(1, NativeSlotRole.Parameter), new(0), new(0)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Store, A: 0, B: 1, Type: 0),
                 new(NativeOp.Constant, 2, 1), new(NativeOp.Store, A: 0, B: 2, Type: 0),
                 new(NativeOp.Return, A: 2)], new([1], 0, []), ParameterCount: 1)],
            [new(TypeKind.Path), new(TypeKind.Never, Pointee: 0)], []);
        NativeProgram optimized = NativeOptimizer.Optimize(original);
        Check(optimized.Functions[0].Instructions.Count(instruction => instruction.Op == NativeOp.Store) == 2,
            "Distinct Path definition contexts were treated as representation-equal stores.");
        Check(optimized.Constants.Select(value => value.OriginId).Contains(1)
            && optimized.Constants.Select(value => value.OriginId).Contains(2), "Path definition origin interning lost provenance.");
    }

    private static void Arithmetic(double first, double second, NativeBinary operation)
    {
        NativeProgram original = ArithmeticProgram(first, second, operation);
        NativeProgram optimized = NativeOptimizer.Optimize(original);
        NativeArtifact.Validate(optimized);
        NativeValue expected = Machine.Run(original, []), actual = Machine.Run(optimized, []);
        Check(actual == expected, $"{operation}: {BitConverter.DoubleToUInt64Bits(first):x16}/{BitConverter.DoubleToUInt64Bits(second):x16}");
    }

    private static NativeProgram ArithmeticProgram(double first, double second, NativeBinary operation)
    {
        int result = operation >= NativeBinary.Equal ? 1 : 0;
        return new([new(NativeKind.Number, Number: first), new(NativeKind.Number, Number: second)],
            [new("arithmetic", [new(0), new(0), new(result)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
                 new(NativeOp.Binary, 2, 0, 1, Immediate: (int)operation), new(NativeOp.Return, A: 2)], new([], result, []))],
            [CodeType(TypeKind.Number), CodeType(TypeKind.Bool)], []);
    }

    private static void MutableLocationSnapshot()
    {
        NativeProgram original = new([new(NativeKind.Number, Number: 9)],
            [new("snapshot", [new(0, NativeSlotRole.Parameter), new(1), new(2), new(0, NativeSlotRole.Local), new(0), new(0), new(0)],
                [new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
                 new(NativeOp.BindLocation, A: 3, B: 2), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0),
                 new(NativeOp.Copy, 4, 3), new(NativeOp.Constant, 5, 0), new(NativeOp.Store, A: 2, B: 5, Type: 0),
                 new(NativeOp.Binary, 6, 4, 3, Immediate: (int)NativeBinary.Add), new(NativeOp.Return, A: 6)],
                 new([0], 0, []), ParameterCount: 1)],
            [CodeType(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
            [new([new("number", 0)])]);
        NativeProgram optimized = NativeOptimizer.Optimize(original);
        for (int value = -16; value < 17; value++)
        {
            NativeValue[] input = [NativeValue.FromNumber(value)];
            Check(Machine.Run(original, input).AsNumber() == value + 9, "snapshot oracle");
            Check(Machine.Run(optimized, input).AsNumber() == value + 9, "mutable location substituted for old snapshot");
        }
    }

    private static void EscapesAndLoops()
    {
        NativeCodeType[] types = [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0), CodeType(TypeKind.Function), CodeType(TypeKind.Null)];
        NativeProgram nested = new([], [new("nested", [new(0), new(0), new(1)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
             new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0), new(NativeOp.Return, A: 1)], new([], 0, []))], types,
            [new([new("child", 0)])]);
        NativeProgram nestedOptimized = NativeOptimizer.Optimize(nested);
        Check(nestedOptimized.Allocations.Count == 2 && nestedOptimized.Allocations.All(plan => plan.Placement == NativeAllocationPlacement.LogicalHeap), "nested escape lost");
        Check(NativeAllocationSafety.FrameSafe(nested, 0).Count == 0, "reader trusts nested escaping allocation");

        NativeProgram capture = new([], [new("capture", [new(0, NativeSlotRole.Local), new(1), new(0), new(2)],
            [new(NativeOp.Allocate, 2, 0), new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Store, A: 1, B: 2, C: 1, Type: 0),
             new(NativeOp.MakeFunction, 3, 1, Inputs: [1]), new(NativeOp.Return, A: 3)], new([], 2, [])),
            new("body", [new(0, NativeSlotRole.Capture), new(3)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Return, A: 1)], new([], 3, [new(0, NativeCaptureMode.Location)]), CaptureCount: 1)],
            types, [new([new("child", 0)])]);
        capture = capture with { Constants = [new(NativeKind.Null)] };
        NativeProgram captured = NativeOptimizer.Optimize(capture);
        Check(captured.Functions[0].Slots[0].Escapes, "returned location capture did not escape");
        Check(captured.Allocations.Single().Placement == NativeAllocationPlacement.LogicalHeap, "allocation behind returned capture stack-placed");

        NativeProgram cyclic = new([new(NativeKind.Bool, Boolean: true), new(NativeKind.Null)],
            [new("cycle", [new(0), new(1), new(2)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Allocate, 0, 0),
                 new(NativeOp.Branch, A: 1, B: 1, C: 3), new(NativeOp.Constant, 2, 1), new(NativeOp.Return, A: 2)], new([], 2, []))],
            [new(TypeKind.Scope, Layouts: [0]), CodeType(TypeKind.Bool), CodeType(TypeKind.Null)], [new([])]);
        Check(!NativeAllocationSafety.FrameSafe(cyclic, 0).Contains(1), "loop allocation reused without lifetime proof");
    }

    private static void AcquireAndFailure()
    {
        NativeProgram original = new([new(NativeKind.Number, Number: 4)], [new("acquire", [new(0), new(0)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Acquire, 1, 0), new(NativeOp.Return, A: 1)], new([], 0, []))],
            [CodeType(TypeKind.Number)], []);
        NativeProgram optimized = NativeOptimizer.Optimize(original);
        Check(optimized.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Constant), "Acquire input removed");
        Check(Machine.Run(optimized, []).AsNumber() == 4, "Acquire value changed");

        NativeProgram narrowing = new([new(NativeKind.Null)], [new("narrow", [new(0), new(1)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Narrow, 1, 0, Type: 1), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.Null | TypeKind.Number), CodeType(TypeKind.Number)], []);
        NativeProgram retained = NativeOptimizer.Optimize(narrowing);
        Check(retained.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Narrow), "failing dead refinement erased");
    }
}
