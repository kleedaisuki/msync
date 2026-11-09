using System.Collections.Immutable;
using System.Text;
using Msync.Core;
using Msync.Hir;

int checks = 0;
foreach (long bits in new[] { 0L, long.MinValue, 0x3ff0000000000000L, 0x7ff800000000002aL, unchecked((long)0xfff800000000001bUL) })
{
    HirModule original = Scalar(BitConverter.Int64BitsToDouble(bits));
    byte[] first = Encode(original);
    Check(HirArtifact.IsHir(first), "Magic identifies semantic HIR");
    HirModule decoded = Decode(first);
    Check(BitConverter.DoubleToInt64Bits(decoded.Literals[0].Number) == bits, "Exact floating-point bits");
    Check(first.AsSpan().SequenceEqual(Encode(decoded)), "Canonical byte roundtrip");
    Check(decoded.Sources[0].Name == "file:///workspace/空间 𐐀.msync", "Unicode source identity");
    HirModule diagnosticOnly = decoded with { Sources = [decoded.Sources[0] with { Text = "This is diagnostic text, not parsable msync source." }] };
    Module lowered = HirLowerer.Lower(Decode(Encode(diagnosticOnly))).Module;
    Check(lowered.Constants.OfType<NumberValue>().Any(number => BitConverter.DoubleToInt64Bits(number.Data) == bits), "Loaded HIR lowers without parsing source text");
    using MemoryStream firstBytecode = new();
    using MemoryStream secondBytecode = new();
    Artifact.Write(lowered, firstBytecode);
    Artifact.Write(HirLowerer.Lower(diagnosticOnly).Module, secondBytecode);
    Check(firstBytecode.ToArray().AsSpan().SequenceEqual(secondBytecode.ToArray()), "Two pure lowerings assign deterministic bytecode layout");
}

HirModule rich = Tables();
byte[] encoded = Encode(rich);
HirModule read = Decode(encoded);
Check(encoded.AsSpan().SequenceEqual(Encode(read)), "All semantic tables roundtrip deterministically");
Check(read.Types[10].TypeParameter == 0 && read.Types[11].Concept == 0, "Open and constrained types");
Check(read.Types[12].Nullable && read.Types[12].Alternatives.SequenceEqual([new HirTypeId(10), new HirTypeId(3)]), "Generic nullable alternatives");
Check(read.Locations[5].TemplateOwner == new HirTemplateId(0), "Template-owned location");
Check(read.Functions[2].Captures.Select(capture => capture.Mode).SequenceEqual([HirCaptureMode.Location, HirCaptureMode.Value]), "Capture modes remain distinct");
Check(read.Templates[0].ResultAnnotation == new HirTypeId(10) && read.Templates[0].TypeParameterTypes[0] == new HirTypeId(10), "Bound generic annotations survive");
Check(read.TemplateClosures[0].Template == new HirTemplateId(0), "Generic closure identity");
Check(read.Concepts[0].Members[0].Name == "value" && read.Concepts[0].Members[0].Nullable, "Structural concept requirements");
Check(read.Ranges[0].Deferred.SequenceEqual([false, true]), "Range demand ownership");
Check(read.Adaptations[0].Targets[0].Target == new HirFunctionId(2), "Callable adaptation");
Check(read.Calls[2].Targets[0].Identity.IsTemplate, "Generic dispatch identity is not a function index");
Check(read.Literals.Single(value => value.Kind == HirLiteralKind.String).Text == "line one\nline two\r\n𐐀", "Literal newlines remain data");
Check(read.Literals.Single(value => value.Kind == HirLiteralKind.Path).Text == "~/two words/\\file", "Raw path provenance");
Check(read.Literals.Single(value => value.Kind == HirLiteralKind.Type).Type == new TypeRef(TypeKind.Range, new(TypeKind.Range, TypeRef.String.WithNull)), "Nested runtime type descriptor");
Check(read.IsSession && read.Exports[0].Name == "shared", "Explicit session export metadata");
Check(read.Scopes.All(scope => scope.Site is not null) && read.Scopes.Select(scope => scope.Site).Distinct().Count() == read.Scopes.Length,
    "Allocation-site identities survive payload tables independently");
HirModule external = Decode(Encode(read with { UnresolvedImports = [new(0)] }));
Check(external.UnresolvedImports.SequenceEqual([new HirLocationId(0)]), "Unresolved external locations are explicit semantic imports");
Check(HirPrinter.Print(rich) == HirPrinter.Print(read), "Printed semantic structure survives persistence");
HirPrintedModule described = HirPrinter.Describe(read);
Check(described.Definitions.Any(item => item.Identifier == "@l0" && item.Category == HirTextCategory.Location)
    && described.Definitions.Any(item => item.Identifier == "@f0" && item.Category == HirTextCategory.Function), "Logical locations and functions have semantic definitions");
Check(described.Text.Contains("bits 0x8000000000000000", StringComparison.Ordinal), "Printed Number bits preserve negative zero");
string[] printedLines = described.Text.Split('\n');
Check(described.References.All(reference => printedLines[reference.Line].AsSpan(reference.Character, reference.Length).SequenceEqual(reference.Identifier)
    && printedLines[reference.TargetLine].AsSpan(reference.TargetCharacter, reference.Length).SequenceEqual(reference.Identifier)), "Printed references target exact definitions");
HirPrintedModule quoted = HirPrinter.Describe(read with { Literals = read.Literals.Add(new(HirLiteralKind.String, Text: "@f0 @l0")) });
int quotedLine = quoted.Definitions.Last(item => item.Category == HirTextCategory.Literal).Line;
Check(!quoted.References.Any(item => item.Line == quotedLine), "Identifier-shaped literal text does not invent semantic references");

HirModule scalarCycle = Scalar(42);
RejectModule(scalarCycle with
{
    Nodes = [scalarCycle.Nodes[0] with { Kind = HirKind.Block, Children = new(0, 1) }], Edges = [new(0)]
}, "Structural child cycle rejected");
RejectModule(scalarCycle with
{
    Types = scalarCycle.Types.Add(new(TypeRef.Bool)),
    Nodes = [scalarCycle.Nodes[0] with { Kind = HirKind.Match, Children = new(0, 1) },
        scalarCycle.Nodes[0] with { Type = new(1), Data = 1 }],
    Literals = scalarCycle.Literals.Add(new(HirLiteralKind.Bool, Boolean: true)),
    Edges = [new(1)], Matches = [new([new(new(HirPatternKind.Any), new(0))])]
}, "Match-arm structural cycle rejected");
HirCapture selfCapture = new(new(0), HirCaptureMode.Value, new(-1), new(0));
RejectModule(scalarCycle with
{
    Types = scalarCycle.Types.Add(new(TypeRef.Function, Signature: 0)),
    Nodes = [scalarCycle.Nodes[0] with { Kind = HirKind.Closure, Type = new(1) }, scalarCycle.Nodes[0]],
    Locations = [new("capture", new(1), scalarCycle.Nodes[0].Source, new(1))],
    Functions = [new("entry", [], [], new(1), new(0)), new("target", [], [selfCapture], new(0), new(1))],
    Closures = [new(new(1), [selfCapture])], Signatures = [new([], new(0))]
}, "Value-capture structural cycle rejected");
RejectModule(scalarCycle with { Functions = [scalarCycle.Functions[0] with { IsOpen = true }] }, "Open function is not an executable entry");
RejectModule(scalarCycle with { Functions = scalarCycle.Functions.Add(new("unresolved", [], [], new(0), HirNodeId.None, IsOpen: true)) }, "A reserved non-entry function is not a finalized artifact");

HirModule symbolicTest = scalarCycle with
{
    Types = scalarCycle.Types.Add(new(TypeRef.Type)).Add(new(TypeRef.Bool)),
    Nodes = scalarCycle.Nodes.Add(scalarCycle.Nodes[0] with { Kind = HirKind.TypeValue, Type = new(1), Data = 0 })
        .Add(scalarCycle.Nodes[0] with { Kind = HirKind.IsType, Type = new(2), Children = new(0, 2), Data = -1 }),
    Edges = [new(0), new(1)],
    Templates = [new("type_test", [], [], new(2), scalarCycle.Nodes[0].Source)]
};
HirModule symbolicRead = Decode(Encode(symbolicTest));
Check(symbolicRead.Children(symbolicRead.Nodes[2]).SequenceEqual([new HirNodeId(0), new(1)]),
    "Bound generic type test retains its unevaluated symbolic target");
Check(HirPrinter.Print(symbolicRead).Contains("IsType target-expression", StringComparison.Ordinal),
    "Bound type test does not invent a finalized target type");
HirModule dynamicTest = symbolicTest with { Functions = [symbolicTest.Functions[0] with { Body = new(2), Result = new(2) }] };
Check(Decode(Encode(dynamicTest)).Nodes[2].Children.Count == 2,
    "Closed dynamic type test retains its evaluated Type-valued target");
RejectModule(dynamicTest with { Edges = [new(0), new(0)] },
    "Closed dynamic type test rejects a non-Type target");
RejectModule(dynamicTest with { Nodes = dynamicTest.Nodes.SetItem(2, dynamicTest.Nodes[2] with { Data = -2 }) },
    "Dynamic type-test target tag is explicit");

HirModule openSession = scalarCycle with
{
    IsSession = true,
    Calls = [new(HirCallKind.Dynamic, new(-1)) { SessionSlot = 0 }],
    Adaptations = [new([]) { SessionSlot = 0 }],
    SessionDispatches = [new(0, [new(0)], new(0), [new(new(0), new(0))])]
};
HirModule openSessionRead = Decode(Encode(openSession));
Check(openSessionRead.SessionDispatches[0].Parameters.SequenceEqual([new HirTypeId(0)])
    && openSessionRead.SessionDispatches[0].Result == new HirTypeId(0)
    && openSessionRead.SessionDispatches[0].Targets[0].Target == new HirFunctionId(0)
    && openSessionRead.Calls[0].SessionSlot == 0 && openSessionRead.Adaptations[0].SessionSlot == 0,
    "Open session dispatch contracts retain only semantic identities");
Check(Decode(Encode(openSession with { Calls = [openSession.Calls[0] with { Kind = HirCallKind.Function }] })).Calls[0].SessionSlot == 0,
    "Open dispatch owns callable selection independently of a finite function target");
HirPrintedModule openSessionView = HirPrinter.Describe(openSessionRead);
Check(openSessionView.Definitions.Any(definition => definition.Identifier == "@d0")
    && openSessionView.References.Count(reference => reference.Identifier == "@d0") == 2,
    "Session call and callback adaptation resolve to a visible semantic dispatch contract");
RejectModule(openSession with { IsSession = false }, "Closed artifacts cannot claim open session dispatch");
RejectModule(openSession with { SessionDispatches = [] }, "Session call and adaptation require a declared slot");
bool requiresSession = false;
try { _ = HirLowerer.Lower(openSessionRead); }
catch (InvalidOperationException error)
{
    requiresSession = error.Message.Contains("active HIR session", StringComparison.Ordinal);
}
Check(requiresSession, "Source-free lowering explains its live session requirement");

byte[] tiny = Encode(Scalar(42));
for (int length = 0; length < tiny.Length; length++) Reject(tiny[..length], "Truncation rejected");
byte[] badMagic = (byte[])tiny.Clone(); badMagic[0] ^= 0xff; Reject(badMagic, "Bad magic rejected");
byte[] badVersion = (byte[])tiny.Clone(); badVersion[8] = 255; Reject(badVersion, "Unknown container version rejected");
byte[] badLanguage = (byte[])tiny.Clone(); badLanguage[11] = (byte)'9'; Reject(badLanguage, "Unknown semantic version rejected");
byte[] badCount = (byte[])tiny.Clone();
int sourceCount = 8 + sizeof(ushort) + 1 + HirArtifact.LanguageVersion.Length + sizeof(int) + 1;
badCount.AsSpan(sourceCount, sizeof(int)).Fill(255); Reject(badCount, "Negative physical count rejected");
byte[] impossibleCount = (byte[])tiny.Clone(); BitConverter.GetBytes(int.MaxValue).CopyTo(impossibleCount, sourceCount);
Reject(impossibleCount, "Impossible count rejected without a huge allocation");
byte[] invalidUtf8 = (byte[])tiny.Clone();
int unicode = invalidUtf8.AsSpan().IndexOf(Encoding.UTF8.GetBytes("空间"));
Check(unicode >= 0, "The source identity is encoded as UTF-8");
invalidUtf8[unicode] = 255; Reject(invalidUtf8, "Invalid UTF-8 is not replaced silently");
byte[] invalidBoolean = (byte[])tiny.Clone(); invalidBoolean[sourceCount - 1] = 2;
Reject(invalidBoolean, "Unknown boolean tag rejected");
byte[] nodePattern = new byte[32]; nodePattern[0] = (byte)HirKind.Literal;
int nodeOffset = tiny.AsSpan().IndexOf(nodePattern);
Check(nodeOffset >= 0, "A scalar literal has the canonical fixed semantic node encoding");
byte[] invalidNode = (byte[])tiny.Clone(); invalidNode[nodeOffset] = 255;
Reject(invalidNode, "Unknown semantic node tag rejected");
byte[] invalidEffects = (byte[])tiny.Clone(); invalidEffects[nodeOffset + 31] = 128;
Reject(invalidEffects, "Unknown effect flags rejected");
Check(!HirArtifact.IsHir("MSYNCBC\0"u8), "Bytecode is not mislabeled HIR");

using (MemoryStream stream = new())
{
    HirArtifact.Write(rich, stream);
    Check(stream.CanWrite, "Writer leaves borrowed stream open");
    stream.Position = 0; _ = HirArtifact.Read(stream);
    Check(stream.CanRead, "Reader leaves borrowed stream open");
}
using (MemoryStream input = new(encoded))
using (NonSeekingStream pipe = new(input))
    Check(Encode(HirArtifact.Read(pipe)).AsSpan().SequenceEqual(encoded), "Streaming input has no seek requirement");

string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "hir-artifact-codec", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string path = Path.Combine(directory, "空间 𐐀.mhir");
    HirArtifact.Write(Scalar(1), path);
    HirArtifact.Write(Scalar(2), path);
    Check(HirArtifact.Read(path).Literals[0].Number == 2, "Owned atomic replacement publishes the next module");
    Check(Directory.EnumerateFiles(directory).Count() == 1, "No staging files retained");
    byte[] before = File.ReadAllBytes(path);
    try { HirArtifact.Write(Scalar(3) with { Entry = new(55) }, path); throw new InvalidOperationException("Invalid module accepted"); }
    catch (MsyncException) { checks++; }
    Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "Invalid export does not replace the previous file");
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"PASS {checks} Typed HIR codec contracts");

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void Reject(byte[] bytes, string message)
{
    try { _ = Decode(bytes); }
    catch (MsyncException) { checks++; return; }
    throw new InvalidOperationException(message);
}

void RejectModule(HirModule module, string message)
{
    try { _ = Encode(module); }
    catch (MsyncException) { checks++; return; }
    throw new InvalidOperationException(message);
}

byte[] Encode(HirModule module)
{
    using MemoryStream stream = new(); HirArtifact.Write(module, stream); return stream.ToArray();
}

HirModule Decode(byte[] bytes)
{
    using MemoryStream stream = new(bytes); return HirArtifact.Read(stream);
}

static HirModule Scalar(double value)
{
    HirBuilder builder = new();
    builder.Sources.Add(new("file:///workspace/空间 𐐀.msync"));
    HirTypeId number = builder.Type(TypeRef.Number);
    HirNodeId literal = builder.Literal(new(HirLiteralKind.Number, Number: value), number, new(new(0), 0, 0));
    builder.Functions.Add(new("entry", [], [], number, literal));
    return builder.Freeze(new(0));
}

static HirModule Tables()
{
    HirSourceRange source = new(new(0), 0, 0);
    HirBuilder builder = new();
    builder.Sources.Add(new("file:///workspace/空间 𐐀.msync", "line one\nline two\r\n𐐀"));
    builder.Type(TypeRef.Number); // 0
    builder.Type(TypeRef.Bool);
    builder.Type(TypeRef.Null);
    builder.Type(TypeRef.String);
    builder.Type(TypeRef.Path);
    builder.Type(TypeRef.Connection);
    builder.Type(TypeRef.Type);
    builder.Type(new HirType(TypeRef.Scope, Shape: 0));
    builder.Type(new HirType(TypeRef.Function, Signature: 0));
    builder.Type(new HirType(new(TypeKind.Range, TypeRef.Number), Element: 0));
    builder.Type(new HirType(TypeRef.Any, TypeParameter: 0));
    builder.Type(new HirType(TypeRef.Scope, Concept: 0));
    builder.Type(new HirType(TypeRef.Any, Nullable: true, Alternatives: [new(10), new(3)]));
    builder.Locations.Add(new("shared", new(0), source, new(0)));
    builder.Locations.Add(new("captured", new(0), source, new(2)));
    builder.Locations.Add(new("value_copy", new(0), source, new(2)));
    builder.Locations.Add(new("name", new(3), source, new(0), HirLocationKind.Field));
    builder.Locations.Add(new("self", new(7), source, new(0)));
    builder.Locations.Add(new("item", new(10), source, new(-1), HirLocationKind.Parameter, AdmitsNull: true, TemplateOwner: new(0)));
    builder.Locations.Add(new("iteration", new(0), source, new(0)));
    HirNodeId literal = builder.Literal(new(HirLiteralKind.Number, Number: BitConverter.Int64BitsToDouble(long.MinValue)), new(0), source);
    HirNodeId genericRead = builder.Add(HirKind.ReadLocation, new(10), source, data: 5, effects: HirEffects.ReadLocation);
    HirNodeId captureRead = builder.Add(HirKind.ReadLocation, new(0), source, data: 1, effects: HirEffects.ReadLocation);
    builder.Literals.Add(new(HirLiteralKind.Null));
    builder.Literals.Add(new(HirLiteralKind.Bool, Boolean: true));
    builder.Literals.Add(new(HirLiteralKind.String, Text: "line one\nline two\r\n𐐀"));
    builder.Literals.Add(new(HirLiteralKind.Path, Text: "~/two words/\\file"));
    builder.Literals.Add(new(HirLiteralKind.Connection, Text: "forge", ConnectionKind: ConnectionKind.Ssh));
    builder.Literals.Add(new(HirLiteralKind.Type, Type: new(TypeKind.Range, new(TypeKind.Range, TypeRef.String.WithNull))));
    ImmutableArray<HirCapture> captures = [new(new(1), HirCaptureMode.Location, new(0), HirNodeId.None), new(new(2), HirCaptureMode.Value, new(-1), literal)];
    builder.Functions.Add(new("entry", [], [], new(0), literal));
    builder.Functions.Add(new("unused_closed", [], [], new(0), literal));
    builder.Functions.Add(new("captured_reader", [], captures, new(0), captureRead));
    builder.Templates.Add(new("identity", ["Item"], [new(5)], genericRead, source, [], new(10), [new(10)]));
    builder.ScopeLayouts.Add(new([new("name", new(3))]));
    builder.Signatures.Add(new([], new(0)));
    builder.Calls.Add(new(HirCallKind.Function, new(2)));
    builder.Calls.Add(new(HirCallKind.Native, new(-1), "math.abs"));
    builder.Calls.Add(new(HirCallKind.Dynamic, new(-1), Targets: [new(new(0, true), new(2))]));
    builder.Calls.Add(new(HirCallKind.Path, new(-1)));
    builder.Closures.Add(new(new(2), captures));
    builder.Scopes.Add(new(0, [new(3)], 7, new(4)));
    builder.Fors.Add(new(new(0), new(6)));
    builder.Matches.Add(new([new(new(HirPatternKind.Any), literal)]));
    builder.Barriers.Add(new(true, false));
    builder.Ranges.Add(new([false, true]));
    builder.Adaptations.Add(new([new(new(2), new(2))]));
    builder.TemplateClosures.Add(new(new(0), []));
    builder.Concepts.Add(new("Carrier", source, [], [new(10)], [new("value", new(10), source, Nullable: true)]));
    builder.SetExports([new("shared", new(0))]);
    return builder.Freeze(new(0), isSession: true);
}

sealed class NonSeekingStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
