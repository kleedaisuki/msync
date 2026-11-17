using Msync.Core;
using Msync.Core.Native;

static class Fixtures
{
    internal static NativeProgram Literal(NativeConstant value, TypeKind? storage = null)
    {
        TypeKind kind = storage ?? value.Kind switch
        {
            NativeKind.Null => TypeKind.Null, NativeKind.Bool => TypeKind.Bool,
            NativeKind.Number => TypeKind.Number, NativeKind.String => TypeKind.String,
            NativeKind.Path => TypeKind.Path, NativeKind.Connection => TypeKind.Connection,
            NativeKind.Type => TypeKind.Type, _ => TypeKind.Any
        };
        return new([value], [new("main", [new(0)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(kind)], []);
    }

    internal static NativeProgram Plan()
    {
        NativePath source = new("a\\b", "/captured", new NativeConnection(ConnectionKind.Ssh, "configured-alias"),
            "/home/klee", "captured-name");
        NativeExecutionPlan plan = new(0, 71, 912,
        [
            new(0, NativePlanValue.Scalar(new(NativeKind.String, "Klee\n\ud800")), new("first.txt", "/destination"),
                PlannedTransferKind.WriteText, [], [new("local", "/destination/first.txt")], []),
            new(1, NativePlanValue.Snapshot([NativePlanValue.FromPath(source)], TypeRef.Path),
                new("cohort", "/destination", new NativeConnection(ConnectionKind.Local)),
                PlannedTransferKind.GenericCopy, [new("ssh:configured-alias", "a\\b")],
                [new("local", "/destination/cohort", true)], [0], new("absent-source.msync", 10, 7))
        ], [new([0], "local"), new([1], "local")], 3, [1, 0]);
        return new([], [new("prepared", [new(0)],
            [new(NativeOp.ExecutePlan, A: 0), new(NativeOp.PlanResult, 0, 0), new(NativeOp.Return, A: 0)],
            new([], 0, []))], [new(TypeKind.Bool)], [])
        { Stage = ArtifactStage.Executable, Context = new("/destination", "/home/klee", "missing.msync"), ExecutionPlans = [plan] };
    }

    internal static NativeProgram WrongLayout() => new([],
        [new("wrong-layout", [new(2), new(4), new(0)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.AddressOffset, 1, 0, 1, 0),
                new(NativeOp.Constant, 2, 0), new(NativeOp.Return, A: 2)], new([], 0, []))],
        [new(TypeKind.Null), new(TypeKind.String), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Number),
            new(TypeKind.Never, Pointee: 1)],
        [new([new("number", 3)]), new([new("text", 1)])])
    { Constants = [new(NativeKind.Null)] };

    internal static NativeProgram ProjectedStore() => new([new(NativeKind.Number, Number: 1), new(NativeKind.Null)],
        [new("projected-store", [new(2, NativeSlotRole.Parameter), new(4), new(0), new(5)],
            [new(NativeOp.ProjectAddress, 1, 0, 0), new(NativeOp.Constant, 2, 0),
                new(NativeOp.Store, A: 1, B: 2, C: 0, Type: 3), new(NativeOp.Constant, 3, 1), new(NativeOp.Return, A: 3)],
            new([2], 5, []), ParameterCount: 1)],
        [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Scope, Layouts: [0, 1]),
            new(TypeKind.Number | TypeKind.Bool), new(TypeKind.Never, Pointee: 3), new(TypeKind.Null)],
        [new([new("value", 0)]), new([new("value", 1)])])
    { Projections = [new([new(0, 0), new(1, 0)])] };

    internal static NativeProgram ScopeCarrierStore(TypeKind source = TypeKind.Scope, bool nullable = true) =>
        new([new(NativeKind.Null)],
            [new("scope-carrier-store", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(5), new(6)],
                [new(NativeOp.ProjectAddress, 2, 0, 0), new(NativeOp.Store, A: 2, B: 1, C: 0, Type: 2),
                 new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)], new([0, 1], 6, []), ParameterCount: 2)],
            [new(TypeKind.Scope, Layouts: [0, 1]), new(source, Layouts: (source & TypeKind.Scope) != 0 ? [0, 1] : null),
             new(TypeKind.Any), new(TypeKind.Scope | (nullable ? TypeKind.Null : TypeKind.Never), Layouts: [0]),
             new(TypeKind.Scope | (nullable ? TypeKind.Null : TypeKind.Never), Layouts: [1]),
             new(TypeKind.Never, Pointee: 2), new(TypeKind.Null)],
            [new([new("next", 3)]), new([new("next", 4)])])
        { Projections = [new([new(0, 0), new(1, 0)])] };

    internal static NativeProgram LogicalRangeDescriptors() => new(
        [new(NativeKind.Number, Number: 1), new(NativeKind.Type, Type: new(TypeKind.Storage, TypeRef.Number))],
        [new("range-descriptors", [new(0), new(1), new(2), new(3), new(3), new(2), new(2), new(4), new(2)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
             new(NativeOp.MakeRange, 2, C: 1, Type: 2, Inputs: [0]),
             new(NativeOp.MakeFunction, 3, 1, C: 0), new(NativeOp.MakeFunction, 4, 2, C: 0),
             new(NativeOp.MakeRange, 5, C: 1, Type: 2, Inputs: []),
             new(NativeOp.MakeRange, 6, C: 1, Type: 2, Inputs: [0]), new(NativeOp.Allocate, 7, 0),
             new(NativeOp.RangeFromMemory, 8, 7, 0, 0, Inputs: [1]), new(NativeOp.Return, A: 8)], new([], 2, [])),
         new("range-producer", [new(0), new(2)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.MakeRange, 1, Type: 2, Inputs: [0]), new(NativeOp.Return, A: 1)], new([], 2, [])),
         new("item-producer", [new(0)], [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []))],
        [new(TypeKind.Number), new(TypeKind.Type), new(TypeKind.Range, Element: 0), new(TypeKind.Function), new(TypeKind.Scope, Layouts: [0])],
        [new([new("value", 0)])]);

    internal static NativeProgram Memory() => new([new(NativeKind.Number, Number: 2)],
        [new("memory", [new(0), new(1), new(1), new(2), new(3)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Allocate, 1, 0, C: 0),
                new(NativeOp.IndexAddress, 2, 1, 0, 0),
                new(NativeOp.CopyMemory, A: 1, B: 2, C: 0, Immediate: 0),
                new(NativeOp.RangeFromMemory, 3, 1, 0, 0),
                new(NativeOp.ClearMemory, A: 1, C: 0, Immediate: 0),
                new(NativeOp.MakeFunction, 4, -1, Symbol: "io.println"),
                new(NativeOp.Return, A: 3)], new([], 2, []))],
        [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Range, Element: 0), new(TypeKind.Function)],
        [new([new("element", 0)])]);

    internal static NativeProgram Hint(bool escape)
    {
        NativeInstruction[] code = escape
            ? [new(NativeOp.Allocate, 0, 0), new(NativeOp.Return, A: 0)]
            : [new(NativeOp.Allocate, 0, 0), new(NativeOp.Constant, 1, 0), new(NativeOp.Return, A: 1)];
        return new([new(NativeKind.Null)], [new("placement", [new(1), new(0)], code,
            new([], escape ? 1 : 0, []))], [new(TypeKind.Null), new(TypeKind.Scope, Layouts: [0])], [new([])])
        { Allocations = [new(0, 0, escape ? NativeAllocationPlacement.LogicalHeap : NativeAllocationPlacement.Frame)] };
    }
}
