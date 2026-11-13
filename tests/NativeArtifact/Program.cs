using System.Text.Json;
using Msync.Core;
using Msync.Core.Native;

if (args is ["measure", var measureInput, var measureOutput])
{
    EncodingMeasurements.Run(measureInput, measureOutput);
    return;
}
if (args is ["produce", var producePath])
{
    NativeArtifact.Write(Fixtures.Plan(), producePath);
    Console.WriteLine("produced native-v1");
    return;
}
if (args is ["consume", var consumePath])
{
    NativeProgram loaded = NativeArtifact.Read(consumePath);
    using JsonDocument json = JsonDocument.Parse(NativeArtifact.ToJson(loaded));
    if (json.RootElement.GetProperty("representation").GetString() != "native-v1"
        || loaded.ExecutionPlans![0].Transfers[1].Source.Kind != NativeKind.Range)
        throw new InvalidOperationException("Standalone native artifact lost its execution representation.");
    Console.WriteLine("consumed native-v1 without source or HIR");
    return;
}

int checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
NativeProgram Roundtrip(NativeProgram program, bool identical = true)
{
    using MemoryStream first = new();
    NativeArtifact.Write(program, first);
    byte[] bytes = first.ToArray();
    first.Position = 0;
    NativeProgram decoded = NativeArtifact.Read(first);
    Require(first.CanRead && first.Position == first.Length, "Reader closed or failed to consume caller stream");
    using MemoryStream second = new();
    NativeArtifact.Write(decoded, second);
    if (identical) Require(second.ToArray().SequenceEqual(bytes), "Native encoding is not deterministic");
    using ReadPipe pipe = new(bytes);
    Require(NativeArtifact.Read(pipe).Functions.Count == program.Functions.Count, "Nonseekable reader changed program");
    return decoded;
}
void Reject(NativeProgram program, string message)
{
    using MemoryStream bytes = new();
    NativeArtifact.WriteProduced(program, bytes);
    bytes.Position = 0;
    try { NativeArtifact.Read(bytes); throw new InvalidOperationException("Accepted invalid native IR: " + message); }
    catch (MsyncException error)
    {
        Require(error.Message.Contains("byte", StringComparison.Ordinal)
            && error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), error.Message);
    }
}

NativeConstant[] literals =
[
    new(NativeKind.Null), new(NativeKind.Bool, Boolean: true), new(NativeKind.Number, Number: -0.0),
    new(NativeKind.String, "可莉\U0001f340\ud800\udfff\udc00"), new(NativeKind.Path, "./a\\b/可莉"),
    new(NativeKind.Connection, "saved-alias", ConnectionKind: ConnectionKind.Ssh),
    new(NativeKind.Type, Type: new(TypeKind.Range, TypeRef.Path))
];
foreach (NativeConstant literal in literals)
{
    NativeProgram loaded = Roundtrip(Fixtures.Literal(literal));
    NativeConstant actual = loaded.Constants[0];
    Require(actual.Text == literal.Text && actual.ConnectionKind == literal.ConnectionKind
        && BitConverter.DoubleToUInt64Bits(actual.Number) == BitConverter.DoubleToUInt64Bits(literal.Number),
        "Literal representation changed");
}
NativePath capturedLiteral = new("relative", "/capture", new(ConnectionKind.Ssh, "configured-alias"), "/home/klee", "local-name");
Require(Roundtrip(Fixtures.Literal(new(NativeKind.Path) { Path = capturedLiteral, OriginId = 42, OriginOffset = 73 })).Constants[0].Path == capturedLiteral,
    "Literal Path lost endpoint or captured local projection.");
NativeConstant originLiteral = Roundtrip(Fixtures.Literal(new(NativeKind.Path, "可莉\ud800") { OriginId = 4, OriginOffset = 8 })).Constants[0];
Require(originLiteral.OriginId == 4 && originLiteral.OriginOffset == 8 && originLiteral.Text == "可莉\ud800", "Literal definition origin or Unicode changed.");
Reject(Fixtures.Literal(new(NativeKind.Path, "x") { OriginId = -2 }), "definition origin");
Reject(Fixtures.Literal(new(NativeKind.Path, "x") { OriginOffset = -1 }), "definition origin");
Reject(Fixtures.Literal(new(NativeKind.Number) { OriginId = 0 }), "definition origin");
NativeProgram memory = Roundtrip(Fixtures.Memory());
Require(memory.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.RangeFromMemory),
    "Native general memory instructions did not roundtrip.");
NativeProgram ChangeMemory(int pc, NativeInstruction replacement) => memory with
{
    Functions = [memory.Functions[0] with { Instructions = memory.Functions[0].Instructions.Select((instruction, index) => index == pc ? replacement : instruction).ToArray() }]
};
Reject(ChangeMemory(1, new(NativeOp.Allocate, 1, 0, C: 3)), "requires Number");
Reject(ChangeMemory(2, new(NativeOp.IndexAddress, 2, 1, 0, 99)), "layout index");
Reject(ChangeMemory(3, new(NativeOp.CopyMemory, A: 1, B: 2, C: 0, Immediate: long.MaxValue)), "stride layout");
Reject(ChangeMemory(4, new(NativeOp.RangeFromMemory, 3, 1, 0, 0, Immediate: 1)), "selected field");
Reject(ChangeMemory(4, new(NativeOp.RangeFromMemory, 3, 1, 0, 0, Symbol: "borrow")), "ownership mode");
Roundtrip(ChangeMemory(4, new(NativeOp.RangeFromMemory, 3, 1, 0, 0, Symbol: "move")));
Reject(ChangeMemory(4, new(NativeOp.RangeFromMemory, 3, 1, 0, 0)) with
    { Types = [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Range, Element: 3), new(TypeKind.Function)] }, "element contract");

Random random = new(9031);
for (int index = 0; index < 256; index++)
{
    ulong bits = unchecked((ulong)random.NextInt64()) | ((ulong)random.Next(2) << 63);
    double number = BitConverter.UInt64BitsToDouble(bits);
    NativeProgram decoded = Roundtrip(Fixtures.Literal(new(NativeKind.Number, Number: number)));
    Require(BitConverter.DoubleToUInt64Bits(decoded.Constants[0].Number) == bits, "binary64 payload changed");
}
foreach (ulong bits in new ulong[] { 0, 0x8000000000000000, 0x7ff0000000000000,
    0xfff0000000000000, 0x7ff8000000001234, 0xfff0000000000042 })
{
    NativeProgram decoded = Roundtrip(Fixtures.Literal(new(NativeKind.Number, Number: BitConverter.UInt64BitsToDouble(bits))));
    Require(BitConverter.DoubleToUInt64Bits(decoded.Constants[0].Number) == bits, "Special IEEE bits changed");
}
Roundtrip(Fixtures.Literal(new(NativeKind.Null), TypeKind.Null | TypeKind.Number));
Reject(Fixtures.Literal(new(NativeKind.Number, Number: 1), TypeKind.String), "does not fit");

NativeProgram nativeElementMetadata = new([new(NativeKind.Number, Number: 7), new(NativeKind.Type, Type: new(TypeKind.Storage, TypeRef.Number))],
    [new("native-element-metadata", [new(0), new(1), new(2)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1),
         new(NativeOp.CallNative, 2, C: 1, Type: 2, Symbol: "ranges.of", Inputs: [0]), new(NativeOp.Return, A: 2)], new([], 2, []))],
    [new(TypeKind.Number), new(TypeKind.Type), new(TypeKind.Range, Element: 0)], []);
Roundtrip(nativeElementMetadata);
NativeProgram InvalidNativeMetadata(NativeInstruction instruction) => nativeElementMetadata with
    { Functions = [nativeElementMetadata.Functions[0] with { Instructions = [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 1), instruction, new(NativeOp.Return, A: 2)] }] };
NativeInstruction metadataCall = nativeElementMetadata.Functions[0].Instructions[2];
Require(NativeInstructionFacts.ValueReads(metadataCall).Order().SequenceEqual([0, 1]), "Native result metadata was omitted from value reads.");
Require(NativeInstructionFacts.RewriteValues(metadataCall, register => register + 1).C == 2, "Native result metadata was omitted from register rewriting.");
Reject(InvalidNativeMetadata(metadataCall with { C = 0 }), "requires Type");
Reject(InvalidNativeMetadata(metadataCall with { C = 9 }), "register");
Reject(InvalidNativeMetadata(metadataCall with { C = -2 }), "descriptor register");
Reject(nativeElementMetadata with { Functions = [nativeElementMetadata.Functions[0] with
    { Instructions = [new(NativeOp.Constant, 0, 0), new(NativeOp.Nop), metadataCall, new(NativeOp.Return, A: 2)] }] }, "not definitely initialized");
foreach (NativeCodeType invalidResult in new NativeCodeType[] { new(TypeKind.Number), new(TypeKind.Any, Element: 0),
    new(TypeKind.Range | TypeKind.Null, Element: 0), new(TypeKind.Range), new(TypeKind.Never, Pointee: 0) })
    Reject(nativeElementMetadata with { Types = [new(TypeKind.Number), new(TypeKind.Type), invalidResult] }, "exact Range result");

NativeProgram logicalRanges = Roundtrip(Fixtures.LogicalRangeDescriptors());
NativeProgram InvalidRangeDescriptor(int pc, NativeInstruction instruction) => logicalRanges with
    { Functions = logicalRanges.Functions.Select((function, index) => index == 0 ? function with
        { Instructions = function.Instructions.Select((current, at) => at == pc ? instruction : current).ToArray() } : function).ToArray() };
Require(NativeInstructionFacts.ValueReads(logicalRanges.Functions[0].Instructions[2]).Order().SequenceEqual([0, 1]),
    "Logical Range metadata Type operand was omitted from register reads.");
Require(NativeInstructionFacts.RewriteValues(logicalRanges.Functions[0].Instructions[6], value => value + 1).C == 2,
    "Deferred Range metadata Type operand was omitted from register rewriting.");
Reject(InvalidRangeDescriptor(2, logicalRanges.Functions[0].Instructions[2] with { C = 0 }), "requires Type");
Reject(InvalidRangeDescriptor(2, logicalRanges.Functions[0].Instructions[2] with { C = -2 }), "descriptor register");
Reject(InvalidRangeDescriptor(1, new(NativeOp.Nop)), "not definitely initialized");
Reject(InvalidRangeDescriptor(5, logicalRanges.Functions[0].Instructions[5] with { C = 0 }), "requires Type");
Reject(InvalidRangeDescriptor(6, logicalRanges.Functions[0].Instructions[6] with { C = 0 }), "requires Type");
Reject(InvalidRangeDescriptor(8, logicalRanges.Functions[0].Instructions[8] with { Inputs = [0] }), "requires Type");
Reject(InvalidRangeDescriptor(8, logicalRanges.Functions[0].Instructions[8] with { Inputs = [1, 1] }), "only one");

NativeProgram typeConstruction = new([], [new("type-construction", [new(0, NativeSlotRole.Parameter), new(1), new(1), new(1)],
    [new(NativeOp.Unary, 1, 0, Immediate: (long)NativeUnary.ValueType),
     new(NativeOp.MakeType, 2, 1, Immediate: (long)TypeKind.Storage),
     new(NativeOp.MakeType, 3, Immediate: (long)TypeKind.Number), new(NativeOp.Return, A: 2)],
    new([0], 1, []), ParameterCount: 1)], [new(TypeKind.Number | TypeKind.Bool), new(TypeKind.Type)], []);
Roundtrip(typeConstruction);
Require(NativeInstructionFacts.ValueReads(typeConstruction.Functions[0].Instructions[1]).SequenceEqual([1])
    && !NativeInstructionFacts.ValueReads(typeConstruction.Functions[0].Instructions[2]).Any(), "MakeType optional element register reads are incorrect.");
NativeProgram InvalidTypeConstruction(NativeInstruction instruction) => typeConstruction with
    { Functions = [typeConstruction.Functions[0] with { Instructions = [instruction, new(NativeOp.Return, A: 1)] }] };
Reject(InvalidTypeConstruction(new(NativeOp.MakeType, 1, A: -2)), "element register");
Reject(InvalidTypeConstruction(new(NativeOp.MakeType, 1, Immediate: 4096)), "descriptor kind");
Reject(InvalidTypeConstruction(new(NativeOp.MakeType, 1, 0, Immediate: (long)TypeKind.Range)), "requires Type");
Reject(InvalidTypeConstruction(new(NativeOp.MakeType, 1, 1, Immediate: (long)TypeKind.Number)), "container descriptor");
Reject(InvalidTypeConstruction(new(NativeOp.MakeType, 1, 2, Immediate: (long)TypeKind.Range)), "not definitely initialized");
NativeProgram plainFunction = Fixtures.Memory();
NativeInstruction[] plainCode = plainFunction.Functions[0].Instructions.ToArray();
plainCode[6] = plainCode[6] with { C = 0 };
Roundtrip(plainFunction with { Functions = [plainFunction.Functions[0] with { Instructions = plainCode }] });
plainCode[6] = plainCode[6] with { B = 0 };
Reject(plainFunction with { Functions = [plainFunction.Functions[0] with { Instructions = plainCode }] }, "cannot carry an environment");

NativeProgram layoutQuery = new([], [new("layout-query", [new(0, NativeSlotRole.Parameter), new(1)],
    [new(NativeOp.Unary, 1, 0, 2, Immediate: (long)NativeUnary.HasLayout), new(NativeOp.Return, A: 1)],
    new([0], 1, []), ParameterCount: 1)], [new(TypeKind.Scope, Layouts: [0, 1, 2]), new(TypeKind.Bool)], [new([]), new([]), new([])]);
Roundtrip(layoutQuery);
Require(NativeInstructionFacts.ValueReads(layoutQuery.Functions[0].Instructions[0]).SequenceEqual([0]),
    "HasLayout metadata was treated as a register read.");
Require(NativeInstructionFacts.RewriteValues(layoutQuery.Functions[0].Instructions[0], register => register + 3).B == 2,
    "HasLayout layout identity was rewritten as a value register.");
Reject(layoutQuery with { Functions = [layoutQuery.Functions[0] with
    { Instructions = [layoutQuery.Functions[0].Instructions[0] with { B = 3 }, new(NativeOp.Return, A: 1)] }] }, "layout index");

TypeRef historicalStorage = new(TypeKind.Storage, new(TypeKind.Range, new(TypeKind.Storage, TypeRef.Number)));
NativeConstant historicalMetadata = Roundtrip(Fixtures.Literal(new(NativeKind.Type, Type: historicalStorage))).Constants[0];
Require(historicalMetadata.Type == historicalStorage && historicalMetadata.Type.ToString() == "Storage<Range<Storage<Number>>>",
    "Historical Storage Type metadata lost its immutable description.");
Reject(Fixtures.Literal(new(NativeKind.Number, Type: historicalStorage)), "unknown builtin");
Reject(Fixtures.Literal(new(NativeKind.Type, Type: new((TypeKind)4096))), "unknown builtin");

TypeRef nested = TypeRef.Number;
for (int index = 0; index < 1024; index++) nested = new(TypeKind.Range, nested);
NativeProgram nestedType = Roundtrip(Fixtures.Literal(new(NativeKind.Type, Type: nested)));
TypeRef? current = nestedType.Constants[0].Type;
for (int index = 0; index < 1024; index++)
{ Require(current?.Kind == TypeKind.Range, "Nested Type structure changed"); current = current!.Element; }
Require(current == TypeRef.Number, "Nested Type leaf changed");

NativeProgram plan = Roundtrip(Fixtures.Plan());
NativeExecutionPlan execution = plan.ExecutionPlans!.Single();
Require(execution.Start == 71 && execution.End == 912 && execution.EliminatedTransfers == 3
    && execution.ResultTransfers!.SequenceEqual([1, 0]), "Plan provenance/results changed");
NativePath pathValue = execution.Transfers[1].Source.Items![0].Path!.Value;
Require(pathValue.Raw == "a\\b" && pathValue.LocalSpelling == "captured-name"
    && pathValue.WorkingDirectory == "/captured" && pathValue.HomeDirectory == "/home/klee"
    && pathValue.Connection == new NativeConnection(ConnectionKind.Ssh, "configured-alias"),
    "Plan Path provenance changed");
Require(execution.Transfers[1].Predecessors.SequenceEqual([0])
    && execution.Transfers[1].Span == new SourceSpan("absent-source.msync", 10, 7), "Plan effects/origin changed");
using (JsonDocument json = JsonDocument.Parse(NativeArtifact.ToJson(plan)))
{
    Require(json.RootElement.GetProperty("representation").GetString() == "native-v1", "Missing structural marker");
    Require(json.RootElement.GetProperty("functions")[0].GetProperty("instructions")[0].GetProperty("op").GetString() == "ExecutePlan",
        "Disassembly lost native instruction identities");
}
Require(NativeArtifact.Disassemble(plan).Contains("PlanResult", StringComparison.Ordinal), "Text disassembly lost instructions");

NativeProgram scalar = Fixtures.Literal(new(NativeKind.Number, Number: 1));
NativeProgram reflected = scalar with { Functions = [scalar.Functions[0] with
    { ReflectionSignature = new([], new(TypeKind.Storage, TypeRef.Number)) }] };
NativeFunctionReflectionSignature reflectedSignature = Roundtrip(reflected).Functions[0].ReflectionSignature!;
Require(reflectedSignature.Parameters.Count == 0 && reflectedSignature.Result == new TypeRef(TypeKind.Storage, TypeRef.Number),
    "Logical function reflection metadata was replaced by its physical execution ABI.");
using (JsonDocument reflectedJson = JsonDocument.Parse(NativeArtifact.ToJson(reflected)))
    Require(reflectedJson.RootElement.GetProperty("functions")[0].GetProperty("reflectionSignature").GetProperty("result")[0].GetString() == "Storage",
        "JSON inspection lost logical reflection signature metadata.");
Reject(scalar with { Functions = [scalar.Functions[0] with { ReflectionSignature = new([TypeRef.Number], TypeRef.Number) }] }, "reflection signature");
Reject(scalar with { Functions = [scalar.Functions[0] with { ReflectionSignature = new([], new((TypeKind)4096)) }] }, "unknown builtin");
Reject(scalar with { Functions = [scalar.Functions[0] with { ReflectionSignature = new([], new(TypeKind.Number, TypeRef.Number)) }] }, "invalid element");
Reject(scalar with { Constants = [new(NativeKind.Process)] }, "Runtime-owned");
Reject(scalar with { Types = [new(TypeKind.Storage)] }, "unknown builtin");
Reject(scalar with { Projections = [new([])] }, "finite layout");
Reject(scalar with { Functions = [scalar.Functions[0] with { Instructions = [new((NativeOp)255)] }] }, "Unknown native opcode");
Reject(scalar with { Functions = [scalar.Functions[0] with { Instructions = [new(NativeOp.Return, A: 0)] }] }, "not definitely initialized");
Reject(scalar with { Functions = [scalar.Functions[0] with { Instructions = [new(NativeOp.Nop, Result: 0), new(NativeOp.Return, A: 0)] }] }, "does not produce");
Reject(Fixtures.WrongLayout(), "one allocation layout");
Reject(Fixtures.ProjectedStore(), "every possible selected physical field");
Roundtrip(Fixtures.ScopeCarrierStore());
Reject(Fixtures.ScopeCarrierStore(TypeKind.Number), "every possible selected physical field");
Reject(Fixtures.ScopeCarrierStore(TypeKind.Null, nullable: false), "every possible selected physical field");
Reject(Fixtures.ScopeCarrierStore(TypeKind.Scope | TypeKind.Number), "every possible selected physical field");
Reject(Fixtures.ScopeCarrierStore() with { Projections = [new([new(0, 0)])] }, "does not cover");
NativeProgram pointerCarrier = Fixtures.ScopeCarrierStore();
Reject(pointerCarrier with { Types = pointerCarrier.Types.Select((type, index) => index == 1 ? new NativeCodeType(TypeKind.Never, Pointee: 0) : type).ToArray() }, "fixed type");
Require(Roundtrip(Fixtures.Hint(escape: false)).Allocations[0].Placement == NativeAllocationPlacement.Frame,
    "Proved frame hint was discarded");
Require(Roundtrip(Fixtures.Hint(escape: true), identical: false).Allocations[0].Placement == NativeAllocationPlacement.LogicalHeap,
    "Escaping allocation trusted an external frame hint");

using (MemoryStream original = new())
{
    NativeArtifact.Write(scalar, original);
    byte[] bytes = original.ToArray();
    for (int length = 0; length < bytes.Length; length++)
    {
        try { NativeArtifact.Read(new MemoryStream(bytes[..length])); throw new InvalidOperationException("Accepted a truncated native artifact"); }
        catch (MsyncException error) { Require(error.Message.Contains("byte", StringComparison.Ordinal), "Truncation lost byte offset"); }
    }
    byte[] unknown = bytes.ToArray(); unknown[8] = 255;
    try { NativeArtifact.Read(new MemoryStream(unknown)); throw new InvalidOperationException("Accepted unknown native format"); }
    catch (MsyncException error) { Require(error.Message.Contains("format", StringComparison.Ordinal), "Wrong format diagnostic"); }
}
using (MemoryStream old = new("MSYNCBC\0\u0006\0"u8.ToArray()))
{
    Require(!NativeArtifact.HasMagic(old.ToArray()), "Old bytecode was guessed to be native IR");
    try { NativeArtifact.Read(old); throw new InvalidOperationException("Native reader reinterpreted old bytecode"); }
    catch (MsyncException error) { Require(error.Message.Contains("Not an msync native", StringComparison.Ordinal), "Old format rejection lost identity"); }
}
try
{
    NativeArtifact.Write(scalar with { SessionRoots = [0] }, new MemoryStream());
    throw new InvalidOperationException("Exported a live session root");
}
catch (MsyncException error) { Require(error.Message.Contains("session", StringComparison.Ordinal), "Session export lost diagnostic"); }
CompactEncodingChecks.Run(Require);
Console.WriteLine($"PASS {checks} native artifact contracts, exact literals/plans, streamed detached encoding and malformed readers");

sealed class ReadPipe(byte[] bytes) : Stream
{
    private readonly MemoryStream source = new(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(count, 3));
    public override int Read(Span<byte> buffer) => source.Read(buffer[..Math.Min(buffer.Length, 3)]);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}
