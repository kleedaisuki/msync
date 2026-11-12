using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
int checks = 0;

await Diamond();
await PortableScopeJoin();
await CallOrder();
await Iterator();
await ImplicitReturn();
FailClosed();
await Heap();
foreach (string path in args)
{
    var supplied = Artifact.Read(path, formBlocks: false);
    var image = supplied.Stage == ArtifactStage.Executable ? supplied : BindRoundTrip(supplied);
    for (int i = 0; i < image.Functions.Count; i++) ValidateProjection(image, Import(image, i));
    Console.WriteLine($"Imported {image.Functions.Count} functions from {path}.");
}
Console.WriteLine($"Execution-region import: {checks} contracts passed.");

async Task Diamond()
{
    var image = BindRoundTrip(Program(
        [new(OpCode.True), new(OpCode.JumpIfFalse, 4), new(OpCode.Constant, 0), new(OpCode.Jump, 5),
         new(OpCode.Constant, 1), new(OpCode.Dup), new(OpCode.Add), new(OpCode.Return)],
        [new NumberValue(4), new NumberValue(9)]));
    var region = Import(image, 0);
    ValidateProjection(image, region);
    var merge = region.BlockAt(5)!;
    Require(merge.Parameters.Length == 1, "a nonempty merge needs one parameter");
    Require(Arguments(region, 2, 5).Single() == region.OperationAt(2)!.Result, "true edge supplies first result");
    Require(Arguments(region, 4, 5).Single() == region.OperationAt(4)!.Result, "false edge supplies second result");
    var add = region.OperationAt(6)!;
    Require(add.Inputs.SequenceEqual(Enumerable.Repeat(merge.Parameters[0], 2)), "Dup repeats identity, not computation");
    Require(region.OperationAt(5)!.Result is null && Expand(region, add.StackBefore).SequenceEqual(add.Inputs),
        "resume retains both occurrences of the same value");
    Require(await Interpret(image) == 8, "portable/bound diamond selects original branch");

    var duplicate = Import(BindRoundTrip(Program([new(OpCode.True), new(OpCode.JumpIfTrue, 2),
        new(OpCode.Null), new(OpCode.Return)], result: TypeRef.Null)), 0);
    Require(duplicate.Blocks[0].Successors.Select(edge => edge.Kind).SequenceEqual(
        [ExecutionEdgeKind.Taken, ExecutionEdgeKind.Fallthrough]), "a same-PC conditional still retains both outcomes");
}

async Task PortableScopeJoin()
{
    var portable = Program([
        new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.IsNull), new(OpCode.JumpIfTrue, 7),
        new(OpCode.Constant, 0), new(OpCode.Constant, 2), new(OpCode.NewScope, 0), new(OpCode.Jump, 10),
        new(OpCode.Constant, 2), new(OpCode.Constant, 1), new(OpCode.NewScope, 1),
        new(OpCode.LoadField, 0), new(OpCode.Return)
    ], [new NumberValue(11), new NumberValue(22), new StringValue("other")]) with
    {
        ScopeLayouts = [new([new("score", 0), new("left", 2)]), new([new("right", 2), new("score", 0)])],
        FieldProjections = [new("score", [new(0, 0), new(1, 1)])]
    };
    var image = BindRoundTrip(portable);
    var region = Import(image, 0);
    ValidateProjection(image, region);
    var field = region.Blocks.SelectMany(block => block.Operations).Single(operation => operation.Instruction.Op == OpCode.LoadField);
    var merge = region.BlockAt(field.Pc)!;
    var incoming = region.Blocks.SelectMany(block => block.Successors).Where(edge => edge.TargetPc == merge.StartPc).ToArray();
    Require(merge.Parameters.Length == 1 && field.Inputs.SequenceEqual(merge.Parameters)
        && incoming.Select(edge => Expand(region, edge.Arguments).Single()).Distinct().Count() == 2,
        "A portable field place consumes the selected Scope value from the branch join");
    Require(image.FieldProjections[field.Instruction.A].Cases.SequenceEqual([new(0, 0), new(1, 1)]),
        "Region import retains both actual layout ordinals on the canonical field instruction");
    Require(await Interpret(image, "selected\n") == 11 && await Interpret(image) == 22,
        "Both source-free branch choices read the proper typed field after serialization");
}

async Task CallOrder()
{
    var portable = Program([new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Constant, 1),
        new(OpCode.Call, 2), new(OpCode.Return)], [new NumberValue(8), new NumberValue(3)],
        functions: [new("subtract", 2, 2,
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Subtract), new(OpCode.Return)], TypeRef.Number,
            ParameterTypes: [TypeRef.Number, TypeRef.Number])]);
    var image = BindRoundTrip(portable);
    var region = Import(image, 0);
    ValidateProjection(image, region);
    Require(region.OperationAt(3)!.Inputs.Select(value => value.Index).SequenceEqual(
        Enumerable.Range(0, 3).Select(pc => region.OperationAt(pc)!.Result!.Value.Index)),
        "call keeps callable, first argument, second argument in evaluation order");
    Require(await Interpret(image) == 5, "call remains noncommutative after artifact round trip");
}

async Task Iterator()
{
    Value[] elements = [new NumberValue(2), new NumberValue(3)];
    var portable = Program([new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.GetIterator),
        new(OpCode.InitializeLocal, 0), new(OpCode.IteratorNext, 0, 8), new(OpCode.Add), new(OpCode.Jump, 4),
        new(OpCode.Nop), new(OpCode.Return)],
        [new NumberValue(5), new RangeValue(elements, TypeRef.Number)], locals: 1);
    // Keep the unreachable Nop so this checks the importer, not the binder's deletion.
    var image = RoundTrip(Binding.Relocate(RoundTrip(portable), new(root, root)));
    var region = Import(image, 0);
    ValidateProjection(image, region);
    var next = region.OperationAt(4)!;
    var edges = region.BlockAt(4)!.Successors;
    var yield = edges.Single(edge => edge.Kind == ExecutionEdgeKind.IteratorYield);
    var done = edges.Single(edge => edge.Kind == ExecutionEdgeKind.IteratorExhausted);
    Require(Expand(region, yield.Arguments).SequenceEqual([.. Expand(region, next.StackBefore), next.Result!.Value]),
        "yield appends one value after preserved operand");
    Require(Expand(region, done.Arguments).SequenceEqual(Expand(region, next.StackBefore)), "exhaustion does not publish a value");
    Require(region.BlockAt(5)!.Parameters.Length == 2 && region.BlockAt(8)!.Parameters.Length == 1,
        "iterator successor parameters have different depths");
    Require(region.OperationAt(7) is null && await Interpret(image) == 10, "unreachable padding is not lowered");
}

async Task ImplicitReturn()
{
    var image = BindRoundTrip(Program([new(OpCode.Constant, 0), new(OpCode.Jump, 2)], [new NumberValue(23)]));
    var region = Import(image, 0);
    ValidateProjection(image, region);
    var exit = region.Blocks[0].Successors.Single();
    Require(exit.TargetPc == region.ExitPc && exit.Arguments.Depth == 1, "end edge retains implicit return result");
    Require(await Interpret(image) == 23, "jump to code end retains top value");

    var empty = Import(AsBound(Program([])), 0);
    Require(empty.Blocks.IsEmpty && empty.ExitPc == 0, "empty function falls out with implicit Null");
}

void FailClosed()
{
    Reject(AsBound(Program([new(OpCode.Pop), new(OpCode.Return)])), ExecutionRegionFailureKind.StackUnderflow, 0);
    Reject(AsBound(Program([new(OpCode.Jump, -1)])), ExecutionRegionFailureKind.InvalidTarget, 0);
    Reject(AsBound(Program([new((OpCode)byte.MaxValue)])), ExecutionRegionFailureKind.UnsupportedOperation, 0);
    Reject(Program([new(OpCode.Return)]), ExecutionRegionFailureKind.UnboundImage, -1);
    Reject(AsBound(Program([new(OpCode.True), new(OpCode.JumpIfTrue, 3), new(OpCode.Null), new(OpCode.Return)])),
        ExecutionRegionFailureKind.StackMergeMismatch, 3);
    Reject(AsBound(Program([new(OpCode.IteratorNext, 0, 1), new(OpCode.Return)], locals: 1)),
        ExecutionRegionFailureKind.StackMergeMismatch, 1);
    var invalidDepthLoop = AsBound(Program([new(OpCode.Null), new(OpCode.Jump, 0)]));
    Reject(invalidDepthLoop, ExecutionRegionFailureKind.StackMergeMismatch, 0);
    var transfer = Import(AsBound(Program([new(OpCode.Null), new(OpCode.Null), new(OpCode.Transfer),
        new(OpCode.Pop), new(OpCode.Return)])), 0);
    Require(transfer.OperationAt(2)!.RequiresCanonicalDispatch && transfer.OperationAt(2)!.StackBefore.Depth == 2,
        "value-dependent Transfer+Pop fusion exits before consuming operands");
    var unused = Import(AsBound(Program([new(OpCode.Return), new((OpCode)byte.MaxValue)])), 0);
    Require(unused.OperationAt(1) is null, "an unreachable unknown operation does not poison import");
}

async Task Heap()
{
    const int size = 128;
    string template = File.ReadAllText(Path.Combine(root, "tests", "Performance", "LibraryMatrix", "heap.msync"));
    var expected = Enumerable.Range(1, size).Select(index => 2 * (index * 40503 % 65536)).Order();
    string source = template.Replace("HEAP_EXPECTED", string.Join(", ", expected), StringComparison.Ordinal)
        .Replace("SIZE", size.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    var image = BindRoundTrip(Compiler.Compile(source, "heap.msync", 2));
    bool siftDownJoin = false;
    foreach (int function in Enumerable.Range(0, image.Functions.Count))
    {
        var region = Import(image, function);
        ValidateProjection(image, region);
        var operations = region.Blocks.SelectMany(block => block.Operations).ToArray();
        if (!operations.Any(operation => operation.Instruction is { Op: OpCode.NativeCall, Text: "storage.vector_set" })
            || !operations.Any(operation => operation.Instruction.Op == OpCode.Multiply)
            || !region.Blocks.Any(block => block.Successors.Any(edge => edge.TargetPc < block.StartPc)))
            continue;
        foreach (var block in region.Blocks.Where(block => block.Parameters.Length == 1))
        {
            if (block.Operations[0].Instruction.Op is not (OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.Not)) continue;
            var incoming = region.Blocks.SelectMany(sourceBlock => sourceBlock.Successors)
                .Where(edge => edge.TargetPc == block.StartPc).ToArray();
            if (incoming.Length < 2 || incoming.Select(edge => Expand(region, edge.Arguments).Single()).Distinct().Count() < 2)
                continue;
            Require(block.Operations[0].Inputs.SequenceEqual(block.Parameters)
                && Expand(region, block.Operations[0].StackBefore).SequenceEqual(block.Parameters),
                "heap resume sees the selected comparison result before evaluating the join");
            siftDownJoin = true;
        }
    }
    Require(siftDownJoin, "the real heap sift-down has a nonempty value merge");
    Require(await Interpret(image) == size, "independent sorted sequence verifies heap source and executable");
}

void ValidateProjection(Module image, ExecutionRegion region)
{
    var canonical = image.Functions[region.FunctionId].Instructions;
    foreach (var block in region.Blocks)
    {
        var current = block.Parameters.ToList();
        foreach (var op in block.Operations)
        {
            Require(op.Instruction == canonical[op.Pc], "projection retains original PC and instruction");
            Require(op.Effects == InstructionEffects.Effects(canonical[op.Pc]), "projection exposes canonical effect facts");
            Require(Expand(region, op.StackBefore).SequenceEqual(current), "pre-op recipe retains full ordered stack");
            Require(current.TakeLast(op.Inputs.Length).SequenceEqual(op.Inputs), "explicit operands are suffix in push order");
            current.RemoveRange(current.Count - op.Inputs.Length, op.Inputs.Length);
            if (op.Instruction.Op == OpCode.Dup) current.AddRange([op.Inputs[0], op.Inputs[0]]);
            else if (op.Result is { } result) current.Add(result);
        }
        foreach (var edge in block.Successors)
        {
            int count = current.Count - (edge.Kind == ExecutionEdgeKind.IteratorExhausted ? 1 : 0);
            Require(Expand(region, edge.Arguments).SequenceEqual(current.Take(count)), "edge carries its exact stack");
            if (edge.TargetPc != region.ExitPc)
                Require(region.BlockAt(edge.TargetPc)!.Parameters.Length == count, "edge arguments match target block arity");
        }
    }
}

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Reject(Module image, ExecutionRegionFailureKind kind, int pc)
{
    bool accepted = ExecutionRegionImporter.TryImport(image, 0, out var region, out var reason);
    Require(!accepted && region is null && reason.Kind == kind && reason.Pc == pc,
        $"expected {kind}@{pc}, got {reason}");
}

Module AsBound(Module portable) => portable with { Stage = ArtifactStage.Executable, Context = new(root, root) };
Module BindRoundTrip(Module portable) => RoundTrip(Binding.Bind(RoundTrip(portable), new(root, root)));

static Module Program(Instruction[] code, Value[]? constants = null, int locals = 0,
    FunctionCode[]? functions = null, TypeRef? result = null)
{
    FunctionCode entry = new("main", 0, locals, code, result ?? TypeRef.Number);
    return new(constants ?? [], new[] { entry }.Concat(functions ?? []).Select(function => function with
    {
        CodeSignature = new(function.ParameterTypes?.Select(TypeId).ToArray() ?? [], TypeId(function.ReturnType), [])
    }).ToArray())
    { CodeTypes = [new(TypeKind.Number), new(TypeKind.Null), new(TypeKind.String)] };

    static int TypeId(TypeRef type) => type.Kind switch
    {
        TypeKind.Number => 0,
        TypeKind.Null => 1,
        TypeKind.String => 2,
        _ => throw new ArgumentException($"No fixture code type for {type}.", nameof(type))
    };
}

static Module RoundTrip(Module image)
{
    using var bytes = new MemoryStream();
    Artifact.Write(image, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes, formBlocks: false);
}

static ExecutionRegion Import(Module image, int function)
{
    if (ExecutionRegionImporter.TryImport(image, function, out var region, out var reason)) return region;
    throw new InvalidOperationException($"function {function}: {reason}");
}

static ExecutionValueId[] Expand(ExecutionRegion region, ExecutionStackState state)
{
    var result = new ExecutionValueId[state.Depth];
    region.CopyStack(state, result);
    return result;
}

static ExecutionValueId[] Arguments(ExecutionRegion region, int source, int target) =>
    Expand(region, region.BlockAt(source)!.Successors.Single(edge => edge.TargetPc == target).Arguments);

static async Task<double> Interpret(Module image, string input = "")
{
    using StringReader reader = new(input);
    return ((NumberValue)await new Interpreter(image, new() { Input = reader }).ExecuteAsync()).Data;
}
