using Msync.Core;
using Msync.Core.Native;

internal static class InterproceduralChecks
{
    private static readonly NativeCodeType[] Types = [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Scope, Layouts: [0]),
        new(TypeKind.Scope, Layouts: [1]), new(TypeKind.Never, Pointee: 0), new(TypeKind.Function), new(TypeKind.Range, Element: 0),
        new(TypeKind.Range, Element: 2), new(TypeKind.Scope), new(TypeKind.Null), new(TypeKind.Never, Pointee: 8),
        new(TypeKind.Range, Element: 5), new(TypeKind.Range, Element: 7), new(TypeKind.Range, Element: 4),
        new(TypeKind.Never, Pointee: 2), new(TypeKind.Range), new(TypeKind.Scope, Layouts: [2]), new(TypeKind.Never, Pointee: 4), new(TypeKind.Scope, Layouts: [3])];
    private static int checks;
    private static bool reverseDefinitions;
    internal static int Run()
    {
        RecordChain(); AliasSubstitution(); CaptureSnapshots(); ForeignContainer(); RecursiveProducer(); LiteralAndOrdinaryCalls(); IteratorEdges(); MixedNative(); FreshnessAndPublication(); CorrelatedFields(); RecursiveApplications(); BulkMemoryEffects(); RecursiveFields();
        reverseDefinitions = true;
        RecordChain(); AliasSubstitution(); CaptureSnapshots();
        reverseDefinitions = false;
        SharedSummaryDag();
        return checks;
    }
    private static NativeProgram Program(NativeFunctionCode main, params NativeFunctionCode[] functions) => new([new(NativeKind.Null)],
        [main, .. functions], Types, [new([new("n", 0)]), new([new("b", 1)]), new([new("pointer", 4)]), new([new("child", 8)])])
        { Projections = [new([new(0, 0)])] };
    private static NativeAddressAnalysis Facts(NativeProgram program)
    {
        NativeArtifact.Validate(program);
        NativeAddressAnalysis.ProgramFacts owner = new(program);
        if (reverseDefinitions)
            foreach (int index in Enumerable.Range(0, program.Functions.Count).Reverse()) owner.Function(index);
        return owner.Function(0);
    }
    private static NativeFunctionCode Factory() => new("factory", [new(0, NativeSlotRole.Parameter), new(2), new(4)],
        [new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
            new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0), new(NativeOp.Return, A: 1)], new([0], 2, []), ParameterCount: 1);
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }

    private static void RecordChain()
    {
        NativeFunctionCode main = new("record", [new(0, NativeSlotRole.Parameter), new(5), new(6), new(7), new(7), new(2), new(4), new(9)],
            [new(NativeOp.MakeFunction, 1, 1, C: 0, Inputs: []), new(NativeOp.MakeRange, 2, Type: 6, Inputs: [0]),
                new(NativeOp.CallNative, 3, Symbol: "ranges.transform", Inputs: [2, 1]),
                new(NativeOp.CallNative, 4, Symbol: "ranges.materialize", Inputs: [3]), new(NativeOp.OpenIterator, A: 4, B: 0),
                new(NativeOp.NextIterator, 5, 0, 8), new(NativeOp.ProjectAddress, 6, 5, 0), new(NativeOp.Jump, A: 5),
                new(NativeOp.CloseIterator, A: 0), new(NativeOp.Constant, 7, 0), new(NativeOp.Return, A: 7)],
            new([0], 9, []), ParameterCount: 1, IteratorCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main, Factory()));
        Check(!facts.At(6, 5).MayAliasCurrentFrame && facts.At(6, 5).Unknown, "A returned row has incomplete exact identity, not a caller-frame alias");
        Check(facts.ProvenLayoutsAt(6, 5)?.SetEquals([0]) == true, "transform/materialize/iterator must preserve the actual callback allocation layout");
        Check(facts.At(7, 6).Targets.Count == 0 && !facts.At(7, 6).MayAliasCurrentFrame, "Foreign producer PCs must never become caller-local allocation PCs");
    }

    private static void AliasSubstitution()
    {
        NativeFunctionCode identity = new("identity", [new(4, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], new([4], 4, []), ParameterCount: 1);
        NativeFunctionCode main = new("aliases", [new(0, NativeSlotRole.Parameter), new(4), new(5), new(4), new(2), new(4), new(4)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.MakeFunction, 2, 1, C: 0, Inputs: []),
                new(NativeOp.Call, 3, 2, 1, Inputs: [1]), new(NativeOp.Allocate, 4, 0), new(NativeOp.AddressOffset, 5, 4, 0, 0),
                new(NativeOp.Call, 6, 2, 1, Inputs: [5]), new(NativeOp.Nop), new(NativeOp.Return, A: 6)], new([0], 4, []), ParameterCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main, identity));
        Check(facts.At(6, 3).MayAliasCurrentFrame && facts.At(6, 3).Targets.Contains(new(-1, 0)), "id(frame pointer) must substitute the current caller alias");
        Check(!facts.At(6, 6).MayAliasCurrentFrame && facts.At(6, 6).Targets.Contains(new(3, -1, 0)), "id(heap field) must not inherit another call's frame alias");
    }

    private static void CaptureSnapshots()
    {
        NativeFunctionCode value = new("captured-value", [new(2, NativeSlotRole.Capture)], [new(NativeOp.Return, A: 0)],
            new([], 2, [new(2, NativeCaptureMode.Value)]), CaptureCount: 1);
        NativeFunctionCode main = new("value-snapshot", [new(2), new(5), new(2)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.MakeFunction, 1, 1, Inputs: [0]), new(NativeOp.Allocate, 0, 0),
                new(NativeOp.Call, 2, 1, 1, Inputs: []), new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([], 2, []));
        NativeAddressAnalysis facts = Facts(Program(main, value));
        Check(facts.At(4, 2).Targets.SetEquals([new(0, -1)]), "ByValue captures must retain the value at MakeFunction, not the rebound source slot");
        NativeFunctionCode location = new("captured-location", [new(2, NativeSlotRole.Capture), new(14), new(2)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 2), new(NativeOp.Return, A: 2)],
            new([], 2, [new(2, NativeCaptureMode.Location)]), CaptureCount: 1);
        main = new("location-current", [new(2), new(5), new(2), new(3), new(14)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.FrameAddress, 4, 0), new(NativeOp.MakeFunction, 1, 1, Inputs: [4]),
                new(NativeOp.Allocate, 3, 1), new(NativeOp.Store, A: 4, B: 3, C: 0, Type: 2),
                new(NativeOp.Call, 2, 1, 1, Inputs: []), new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([], 2, []));
        facts = Facts(Program(main, location));
        Check(facts.At(6, 2).Targets.Contains(new(3, -1)), "ByLocation captures must include a later compatible referent write");
    }

    private static void ForeignContainer()
    {
        NativeFunctionCode holder = new("holder", [new(4, NativeSlotRole.Parameter), new(16), new(17)],
            [new(NativeOp.Allocate, 1, 2), new(NativeOp.AddressOffset, 2, 1, 2, 0),
                new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 4), new(NativeOp.Return, A: 1)], new([4], 16, []), ParameterCount: 1);
        NativeFunctionCode main = new("container", [new(0, NativeSlotRole.Parameter), new(4), new(5), new(16), new(17), new(4)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.MakeFunction, 2, 1, C: 0, Inputs: []),
                new(NativeOp.Call, 3, 2, 1, Inputs: [1]), new(NativeOp.AddressOffset, 4, 3, 2, 0),
                new(NativeOp.Load, 5, 4, Type: 4), new(NativeOp.Nop), new(NativeOp.Return, A: 3)], new([0], 16, []), ParameterCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main, holder));
        Check(!facts.At(3, 3).MayAliasCurrentFrame, "A fresh container's region must not alias a retained frame cell");
        Check(facts.At(5, 5).MayAliasCurrentFrame && facts.At(5, 5).Targets.Contains(new(-1, 0)), "Loading the container's pointer must restore its actual caller-frame alias");
    }

    private static void RecursiveProducer()
    {
        NativeFunctionCode recursive = new("recursive", [new(1, NativeSlotRole.Parameter), new(2), new(5), new(2)],
            [new(NativeOp.Branch, A: 0, B: 1, C: 3), new(NativeOp.Allocate, 1, 0), new(NativeOp.Return, A: 1),
                new(NativeOp.MakeFunction, 2, 1, C: 0, Inputs: []), new(NativeOp.Call, 3, 2, 1, Inputs: [0]), new(NativeOp.Return, A: 3)],
            new([1], 2, []), ParameterCount: 1);
        NativeFunctionCode main = new("recursive-caller", [new(1, NativeSlotRole.Parameter), new(5), new(2)],
            [new(NativeOp.MakeFunction, 1, 1, C: 0, Inputs: []), new(NativeOp.Call, 2, 1, 1, Inputs: [0]),
                new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([1], 2, []), ParameterCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main, recursive));
        Check(!facts.At(2, 2).MayAliasCurrentFrame && facts.ProvenLayoutsAt(2, 2)?.SetEquals([0]) == true,
            "Recursive producer summaries must converge without treating the callee activation as caller storage");
    }

    private static void LiteralAndOrdinaryCalls()
    {
        NativeFunctionCode producer = new("producer", [new(2)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Return, A: 0)], new([], 2, []));
        NativeFunctionCode main = new("literal", [new(5), new(2), new(7), new(2), new(9)],
            [new(NativeOp.MakeFunction, 0, 1, C: 0, Inputs: []), new(NativeOp.Call, 1, 0, 1, Inputs: []),
                new(NativeOp.MakeRange, 2, Type: 7, Inputs: [1]), new(NativeOp.OpenIterator, A: 2, B: 0),
                new(NativeOp.NextIterator, 3, 0, 6), new(NativeOp.Jump, A: 4),
                new(NativeOp.CloseIterator, A: 0), new(NativeOp.Constant, 4, 0), new(NativeOp.Return, A: 4)],
            new([], 9, []), IteratorCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main, producer));
        Check(facts.ProvenLayoutsAt(5, 3)?.SetEquals([0]) == true, "An initialized literal retains its actual element producer");
        NativeFunctionCode rangeProducer = new("range-producer", [new(2), new(7)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.MakeRange, 1, Type: 7, Inputs: [0]), new(NativeOp.Return, A: 1)], new([], 7, []));
        main = new("ordinary-range-call", [new(5), new(7), new(2), new(9)],
            [new(NativeOp.MakeFunction, 0, 1, C: 0, Inputs: []), new(NativeOp.Call, 1, 0, 1, Inputs: []),
                new(NativeOp.OpenIterator, A: 1, B: 0), new(NativeOp.NextIterator, 2, 0, 5), new(NativeOp.Jump, A: 3),
                new(NativeOp.CloseIterator, A: 0), new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)],
            new([], 9, []), IteratorCount: 1);
        facts = Facts(Program(main, rangeProducer));
        Check(facts.ProvenLayoutsAt(4, 2)?.SetEquals([0]) == true, "An ordinary Range call retains its returned element provenance");
    }

    private static void IteratorEdges()
    {
        NativeFunctionCode main = new("iterator-edges", [new(2, NativeSlotRole.Parameter), new(2), new(7), new(9)],
            [new(NativeOp.Allocate, 1, 0), new(NativeOp.MakeRange, 2, Type: 7, Inputs: [1]), new(NativeOp.OpenIterator, A: 2, B: 0),
                new(NativeOp.NextIterator, 0, 0, 6), new(NativeOp.Nop), new(NativeOp.Jump, A: 3), new(NativeOp.CloseIterator, A: 0),
                new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)], new([2], 9, []), ParameterCount: 1, IteratorCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main));
        Check(!facts.At(4, 0).MayAliasCurrentFrame && facts.At(6, 0).MayAliasCurrentFrame,
            "Iterator success defines the new item; failure preserves entry/backedge alternatives, not the success-only definition");
    }

    private static void FreshnessAndPublication()
    {
        NativeFunctionCode main = new("two-fresh", [new(0, NativeSlotRole.Parameter), new(5), new(2), new(2)],
            [new(NativeOp.MakeFunction, 1, 1, C: 0, Inputs: []), new(NativeOp.Call, 2, 1, 1, Inputs: [0]),
                new(NativeOp.Call, 3, 1, 1, Inputs: [0]), new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([0], 2, []), ParameterCount: 1);
        NativeFunctionCode extra = new("later-root", [new(0, NativeSlotRole.Parameter), new(5), new(2)],
            [new(NativeOp.MakeFunction, 1, 1, C: 0, Inputs: []), new(NativeOp.Call, 2, 1, 1, Inputs: [0]),
                new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([0], 2, []), ParameterCount: 1);
        NativeProgram program = Program(main, Factory(), extra);
        NativeArtifact.Validate(program);
        NativeAddressAnalysis.ProgramFacts owner = new(program);
        NativeAddressAnalysis facts = owner.Function(0);
        var first = facts.At(3, 2); var second = facts.At(3, 3);
        Check(first.Targets.Count == 0 && second.Targets.Count == 0 && !ReferenceEquals(first.Foreign.Single().Region, second.Foreign.Single().Region),
            "Two calls to one allocation site must not become one caller-local must-alias object");
        Parallel.For(0, 48, index =>
        {
            NativeAddressAnalysis view = owner.Function(index % 2 == 0 ? 0 : 2);
            int pc = index % 2 == 0 ? 3 : 2;
            if (view.At(pc, 2).MayAliasCurrentFrame || view.ProvenLayoutsAt(pc, 2)?.SetEquals([0]) != true)
                throw new InvalidOperationException("Demanded function publication must be complete and safe across concurrent image links");
            foreach (var origin in first.Foreign) if (origin.Region.Layout != 0) throw new InvalidOperationException("Published foreign origins changed");
        });
        Check(first.Foreign.Count == 1 && !first.MayAliasCurrentFrame, "Adding a later root must not mutate an already published component");
        NativeFunctionCode external = new("external-range", [new(7, NativeSlotRole.Parameter), new(2), new(9)],
            [new(NativeOp.OpenIterator, A: 0, B: 0), new(NativeOp.NextIterator, 1, 0, 3), new(NativeOp.Jump, A: 1),
                new(NativeOp.CloseIterator, A: 0), new(NativeOp.Constant, 2, 0), new(NativeOp.Return, A: 2)],
            new([7], 9, []), ParameterCount: 1, IteratorCount: 1);
        NativeAddressAnalysis unknown = Facts(Program(external));
        Check(unknown.At(2, 1).MayAliasCurrentFrame && unknown.ProvenLayoutsAt(2, 1) is null,
            "An external Range's declared Scope element domain is not allocation evidence");
        NativeFunctionCode thunk = new("function-item", [new(2)], [new(NativeOp.Allocate, 0, 0), new(NativeOp.Return, A: 0)], new([], 2, []));
        NativeFunctionCode literal = new("literal-function", [new(5), new(11), new(5), new(9)],
            [new(NativeOp.MakeFunction, 0, 1, C: 0, Inputs: []), new(NativeOp.MakeRange, 1, Type: 11, Inputs: [0]),
                new(NativeOp.OpenIterator, A: 1, B: 0), new(NativeOp.NextIterator, 2, 0, 5), new(NativeOp.Jump, A: 3),
                new(NativeOp.CloseIterator, A: 0), new(NativeOp.Constant, 3, 0), new(NativeOp.Return, A: 3)], new([], 9, []), IteratorCount: 1);
        NativeAddressAnalysis eager = Facts(Program(literal, thunk));
        Check(eager.CallablesAt(4, 2)?.Single().Function == 1 && eager.ProvenLayoutsAt(4, 2) is null,
            "A literal Function item must not be replaced by the result of invoking it");
    }

    private static void CorrelatedFields()
    {
        NativeFunctionCode field = new("field", [new(18, NativeSlotRole.Parameter), new(10), new(8)],
            [new(NativeOp.AddressOffset, 1, 0, 3, 0), new(NativeOp.Load, 2, 1, Type: 8), new(NativeOp.Return, A: 2)],
            new([18], 8, []), ParameterCount: 1);
        NativeFunctionCode wrapper = new("wrapper", [new(18, NativeSlotRole.Parameter), new(18, NativeSlotRole.Parameter), new(5), new(8), new(8)],
            [new(NativeOp.MakeFunction, 2, 2, C: 0, Inputs: []), new(NativeOp.Call, 3, 2, 2, Inputs: [0]),
                new(NativeOp.Call, 4, 2, 2, Inputs: [1]), new(NativeOp.Return, A: 4)], new([18, 18], 8, []), ParameterCount: 2);
        NativeFunctionCode main = new("correlated", [new(2), new(3), new(18), new(18), new(10), new(5), new(8)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Allocate, 1, 1), new(NativeOp.Allocate, 2, 3),
                new(NativeOp.AddressOffset, 4, 2, 3, 0), new(NativeOp.Store, A: 4, B: 0, C: 1, Type: 8),
                new(NativeOp.Allocate, 3, 3), new(NativeOp.AddressOffset, 4, 3, 3, 0), new(NativeOp.Store, A: 4, B: 1, C: 1, Type: 8),
                new(NativeOp.MakeFunction, 5, 1, C: 0, Inputs: []), new(NativeOp.Call, 6, 5, 1, Inputs: [2, 3]),
                new(NativeOp.Nop), new(NativeOp.Return, A: 6)], new([], 8, []));
        NativeAddressAnalysis facts = Facts(Program(main, wrapper, field));
        Check(facts.ProvenLayoutsAt(10, 6)?.SetEquals([1]) == true && !facts.At(10, 6).MayAliasCurrentFrame,
            "wrapper(field(a), field(b)) must preserve b's distinct child layout and actual environment");
        NativeFunctionCode duplicate = Factory();
        main = new("indexed", [new(0, NativeSlotRole.Parameter), new(5), new(2)],
            [new(NativeOp.MakeFunction, 1, 2, C: 0, Inputs: []), new(NativeOp.Call, 2, 1, 2, Inputs: [0]),
                new(NativeOp.Nop), new(NativeOp.Return, A: 2)], new([0], 2, []), ParameterCount: 1);
        facts = Facts(Program(main, duplicate, duplicate));
        Check(facts.ProvenLayoutsAt(2, 2)?.SetEquals([0]) == true && facts.At(2, 2).Foreign.All(target => target.Region.Function == 2),
            "A shared function code object at two indices must retain the actual invoked function identity");
        duplicate = new("same-identity", [new(4, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], new([4], 4, []), ParameterCount: 1);
        main = new("index-order", [new(0, NativeSlotRole.Parameter), new(4), new(5), new(4), new(4)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.MakeFunction, 2, 1, C: 0, Inputs: []),
                new(NativeOp.Call, 3, 2, 1, Inputs: [1]), new(NativeOp.MakeFunction, 2, 2, C: 0, Inputs: []),
                new(NativeOp.Call, 4, 2, 2, Inputs: [1]), new(NativeOp.Nop), new(NativeOp.Return, A: 4)], new([0], 4, []), ParameterCount: 1);
        facts = Facts(Program(main, duplicate, duplicate));
        Check(facts.At(5, 4).MayAliasCurrentFrame && facts.At(5, 4).Targets.Contains(new(-1, 0)),
            "Registering shared-code function #1 first must not leave function #2's formal alias summary uncomputed");
    }

    private static void RecursiveApplications()
    {
        NativeFunctionCode recursive = new("recursive-application", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(0), new(0), new(0)],
            [new(NativeOp.Branch, A: 1, B: 1, C: 2), new(NativeOp.Return, A: 0), new(NativeOp.Call, 2, A: 0, Inputs: [0]),
                new(NativeOp.MakeFunction, 3, A: 0, C: 0), new(NativeOp.Call, 4, A: 3, B: 0, Inputs: [2, 1]), new(NativeOp.Return, A: 4)],
            new([0, 1], 0, []), ParameterCount: 2);
        NativeFunctionCode identity = new("function-id", [new(0, NativeSlotRole.Parameter)],
            [new(NativeOp.Return, A: 0)], new([0], 0, []), ParameterCount: 1);
        NativeProgram program = new([], [recursive, identity], [new(TypeKind.Function), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(program);
        NativeAddressAnalysis facts = new NativeAddressAnalysis.ProgramFacts(program).Function(0);
        Check(facts.At(1, 0).Formals.Count == 1, "Higher-order recursive applications must close over finite producer/use nodes without inventing an empty formal value");
    }

    private static void BulkMemoryEffects()
    {
        NativeFunctionCode main = new("bulk-memory", [new(2), new(3), new(18), new(18), new(10), new(0), new(8)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Allocate, 1, 1), new(NativeOp.Allocate, 2, 3), new(NativeOp.Allocate, 3, 3),
                new(NativeOp.AddressOffset, 4, 2, 3, 0), new(NativeOp.Store, A: 4, B: 0, C: 1, Type: 8),
                new(NativeOp.AddressOffset, 4, 3, 3, 0), new(NativeOp.Store, A: 4, B: 1, C: 1, Type: 8), new(NativeOp.Constant, 5, 0),
                new(NativeOp.CopyMemory, A: 2, B: 3, C: 5, Immediate: 3), new(NativeOp.AddressOffset, 4, 2, 3, 0),
                new(NativeOp.Load, 6, 4, Type: 8), new(NativeOp.Nop), new(NativeOp.Return, A: 6)], new([], 8, []));
        NativeProgram program = Program(main) with { Constants = [new(NativeKind.Number, Number: 1)] };
        NativeAddressAnalysis facts = Facts(program);
        IReadOnlySet<int>? copied = facts.ProvenLayoutsAt(12, 6);
        Check(copied is null || copied.Contains(1), "CopyMemory must invalidate or update the selected destination's old referent proof");
        main = main with { Instructions = main.Instructions.Select(instruction => instruction.Op == NativeOp.CopyMemory
            ? instruction with { Op = NativeOp.ClearMemory, B = -1 } : instruction).ToArray() };
        facts = Facts(program with { Functions = [main] });
        Check(facts.ProvenLayoutsAt(12, 6) is null, "ClearMemory cannot leave a pre-clear referent proof behind an uninitialized load");
    }

    private static void RecursiveFields()
    {
        NativeFunctionCode walk = new("recursive-fields", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(2), new(0), new(3), new(0)],
            [new(NativeOp.Branch, A: 1, B: 1, C: 2), new(NativeOp.Return, A: 0), new(NativeOp.AddressOffset, 2, 0, 0, 0),
                new(NativeOp.Load, 3, 2, Type: 0), new(NativeOp.MakeFunction, 4, 0, C: 0),
                new(NativeOp.Call, 5, 4, 0, Inputs: [3, 1]), new(NativeOp.Return, A: 5)], new([0, 1], 0, []), ParameterCount: 2);
        NativeProgram program = new([], [walk], [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Bool),
            new(TypeKind.Never, Pointee: 0), new(TypeKind.Function)], [new([new("next", 0)])]);
        NativeArtifact.Validate(program);
        NativeAddressAnalysis facts = new NativeAddressAnalysis.ProgramFacts(program).Function(0);
        Check(facts.At(1, 0).Formals.Count == 1, "Recursive typed-field transport must be a finite cyclic constraint, not an unfolded access-path tree");
    }

    private static void SharedSummaryDag()
    {
        List<NativeCodeType> types = [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Function)];
        List<NativeSlot> slots = [new(0)];
        List<NativeInstruction> code = [new(NativeOp.Allocate, 0, 0)];
        int previous = 0, previousType = 0;
        for (int depth = 0; depth < 11; depth++)
        {
            int itemType = types.Count; types.Add(new(TypeKind.Range, Element: previousType));
            int left = slots.Count; slots.Add(new(itemType));
            int right = slots.Count; slots.Add(new(itemType));
            code.Add(new(NativeOp.MakeRange, left, Type: itemType, Inputs: [previous]));
            code.Add(new(NativeOp.MakeRange, right, Type: itemType, Inputs: [previous]));
            previousType = types.Count; types.Add(new(TypeKind.Range, Element: itemType));
            previous = slots.Count; slots.Add(new(previousType));
            code.Add(new(NativeOp.MakeRange, previous, Type: previousType, Inputs: [left, right]));
        }
        code.Add(new(NativeOp.Return, A: previous));
        NativeFunctionCode factory = new("shared-dag", slots, code, new([], previousType, []));
        NativeFunctionCode main = new("import-shared-dag", [new(1), new(previousType)],
            [new(NativeOp.MakeFunction, 0, 1, C: 0), new(NativeOp.Call, 1, 0, 1, Inputs: []), new(NativeOp.Return, A: 1)], new([], previousType, []));
        NativeProgram program = new([], [main, factory], types, [new([])]);
        NativeAddressAnalysis facts = Facts(program);
        Check(facts.At(2, 1).Elements.Count == 2, "Shared summary diamonds must import graph edges without replaying every root-to-leaf path");
    }

    private static void MixedNative()
    {
        NativeFunctionCode identity = new("source", [new(13, NativeSlotRole.Parameter), new(7)],
            [new(NativeOp.MakeRange, 1, Type: 7, Inputs: []), new(NativeOp.Return, A: 1)], new([13], 7, []), ParameterCount: 1);
        NativeFunctionCode main = new("native-wins", [new(0, NativeSlotRole.Parameter), new(4), new(13), new(5), new(15), new(4), new(9)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.MakeRange, 2, Type: 13, Inputs: [1]),
                new(NativeOp.MakeFunction, 3, Symbol: "ranges.identity"), new(NativeOp.Call, 4, 3, 1, Inputs: [2]),
                new(NativeOp.OpenIterator, A: 4, B: 0), new(NativeOp.NextIterator, 5, 0, 7), new(NativeOp.Jump, A: 5),
                new(NativeOp.CloseIterator, A: 0), new(NativeOp.Constant, 6, 0), new(NativeOp.Return, A: 6)],
            new([0], 9, []), ParameterCount: 1, IteratorCount: 1);
        NativeAddressAnalysis facts = Facts(Program(main, identity));
        Check(facts.At(6, 5).MayAliasCurrentFrame, "Actual NativeName must win over Call.B's source body");
        Check(facts.CallablesAt(3, 3)?.Single().NativeName == "ranges.identity", "Mixed native/source calls cannot be source-inlined from B alone");
    }
}
