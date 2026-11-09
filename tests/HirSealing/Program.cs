using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

int checks = 0;
HirModule original = Fixture();
HirSealingResult sealedResult = HirSealer.Compact(original);
HirModule compact = sealedResult.Module;
Check(compact.Nodes.Length == original.Nodes.Length - 1, "Abandoned bound node removed");
Check(sealedResult.NodeMap[0].IsNone && sealedResult.NodeMap[1].Value == 0, "Ascending deterministic node identities");
Check(compact.Sources == original.Sources && compact.Types == original.Types
    && compact.Locations == original.Locations && compact.ScopeLayouts == original.ScopeLayouts
    && compact.Signatures == original.Signatures && compact.Concepts == original.Concepts, "Semantic arena identities remain stable");
Check(compact.Functions.Length == original.Functions.Length && compact.Templates.Length == 1,
    "Unused closed functions and generic templates remain semantic roots");
Check(compact.Templates[0].Body.Value == sealedResult.NodeMap[original.Templates[0].Body.Value].Value,
    "Unused generic body retained and remapped");
Check(compact.Functions[1].Captures[0].SourceValue == sealedResult.NodeMap[original.Functions[1].Captures[0].SourceValue.Value],
    "Function capture value rooted and remapped");
Check(compact.Templates[0].Captures[0].SourceValue == sealedResult.NodeMap[original.Templates[0].Captures[0].SourceValue.Value],
    "Template capture value rooted and remapped");
Check(compact.Closures.Length == 1 && compact.TemplateClosures.Length == 1 && compact.Matches.Length == 1,
    "Only referenced executable payloads retained");
Check(compact.Literals.Length == original.Literals.Length - 1 && compact.Matches[0].Arms[0].Pattern.Data == 0,
    "Literal patterns and literal nodes share deterministic remapping");
Check(compact.Closures[0].Captures[0].SourceValue == sealedResult.NodeMap[original.Closures[1].Captures[0].SourceValue.Value]
    && compact.TemplateClosures[0].Captures[0].SourceValue == sealedResult.NodeMap[original.TemplateClosures[1].Captures[0].SourceValue.Value],
    "Closure value expressions retained outside child edge arena");
HirValidation.Validate(compact);
Check(ReferenceEquals(HirSealer.Seal(compact), compact), "Sealed graph takes no-rewrite path");
Check(Encode(compact).AsSpan().SequenceEqual(Encode(HirSealer.Seal(compact))), "Sealing twice is byte-identical");
Check(Lower(original).AsSpan().SequenceEqual(Lower(compact)), "Sealing preserves exact lowered bytecode");
Reject(original with { Functions = original.Functions.SetItem(0, original.Functions[0] with { Body = new(999) }) }, "Invalid live node rejected");
Reject(original with { Nodes = original.Nodes.SetItem(original.Functions[0].Body.Value,
    original.Nodes[original.Functions[0].Body.Value] with { Kind = HirKind.Closure, Data = 999 }) }, "Invalid live payload rejected");
HirModule abandonedInvalid = original with { Nodes = original.Nodes.SetItem(0, original.Nodes[0] with { Kind = HirKind.Closure, Data = 999 }) };
Check(Encode(HirSealer.Seal(abandonedInvalid)).AsSpan().SequenceEqual(Encode(compact)), "Abandoned inference payload is not exported");

HirModule payloads = PayloadFixture();
HirModule sealedPayloads = HirSealer.Seal(payloads);
HirValidation.Validate(sealedPayloads);
Check(sealedPayloads.Calls.Length == 1 && sealedPayloads.Scopes.Length == 1
    && sealedPayloads.Fors.Length == 1 && sealedPayloads.Barriers.Length == 1
    && sealedPayloads.Ranges.Length == 1 && sealedPayloads.Adaptations.Length == 1,
    "Every executable payload arena is compacted");
Check(sealedPayloads.Nodes.Where(node => node.Kind is HirKind.Call or HirKind.Scope or HirKind.For
    or HirKind.Barrier or HirKind.Range or HirKind.LazyRange or HirKind.AdaptFunction).All(node => node.Data == 0),
    "Every executable payload node uses its remapped identity");
Check(sealedPayloads.Edges.Length == payloads.Edges.Length - 1, "Unused child-edge storage removed");
Check(sealedPayloads.UnresolvedImports == payloads.UnresolvedImports, "External imports preserve stable location identities");
Check(ReferenceEquals(sealedPayloads, HirSealer.Seal(sealedPayloads)), "Full payload fixture seals idempotently");

if (args.Length > 0)
{
    HirModule file = HirArtifact.Read(args[0]);
    HirModule reduced = HirSealer.Seal(file);
    Check(Lower(file).AsSpan().SequenceEqual(Lower(reduced)), "Loaded real-source HIR preserves source-free lowering");
    Console.WriteLine($"Loaded artifact: {file.Nodes.Length} -> {reduced.Nodes.Length} semantic nodes.");
}
Console.WriteLine($"PASS {checks} Typed HIR sealing contracts");

void Check(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}
void Reject(HirModule value, string message)
{
    try { HirSealer.Seal(value); }
    catch (MsyncException) { Check(true, message); return; }
    throw new InvalidOperationException(message);
}
static byte[] Encode(HirModule module)
{
    using MemoryStream stream = new();
    HirArtifact.Write(module, stream);
    return stream.ToArray();
}
static byte[] Lower(HirModule module)
{
    using MemoryStream stream = new();
    Artifact.Write(HirLowerer.Lower(module, new(0)).Module, stream);
    return stream.ToArray();
}
static HirModule Fixture()
{
    HirBuilder builder = new();
    builder.Sources.Add(new("file:///fixture.msync", "diagnostic only"));
    var source = new HirSourceRange(new(0), 0, 1);
    var number = builder.Type(TypeRef.Number);
    var function = builder.Type(TypeRef.Function);
    var open = builder.Type(new HirType(TypeRef.Any, TypeParameter: 0));
    builder.Locations.Add(new("captured", number, source, new(1)));
    builder.Locations.Add(new("generic_capture", number, source, new(-1), TemplateOwner: new(0)));
    builder.Literals.Add(new(HirLiteralKind.Number, Number: -999));
    builder.Add(HirKind.Literal, number, source, data: 0);
    var literal = builder.Literal(new(HirLiteralKind.Number, Number: 42), number, source);
    var captured = builder.Literal(new(HirLiteralKind.Number, Number: 7), number, source);
    var genericCaptured = builder.Literal(new(HirLiteralKind.Number, Number: 8), number, source);
    var closureCaptured = builder.Literal(new(HirLiteralKind.Number, Number: 9), number, source);
    var templateCaptured = builder.Literal(new(HirLiteralKind.Number, Number: 10), number, source);
    var read = builder.Add(HirKind.ReadLocation, number, source, data: 0);
    builder.Functions.Add(new("main", [], [], number, HirNodeId.None));
    builder.Functions.Add(new("unused_closed", [], [new(new(0), HirCaptureMode.Value, new(-1), captured)], number, read));
    var genericBody = builder.Add(HirKind.TypeValue, open, source, data: open.Value);
    builder.Templates.Add(new("unused_generic", ["T"], [], genericBody, source,
        [new(new(1), HirCaptureMode.Value, new(-1), genericCaptured)], TypeParameterTypes: [open]));
    builder.Closures.Add(new(new(1), []));
    builder.Closures.Add(new(new(1), [new(new(0), HirCaptureMode.Value, new(-1), closureCaptured)]));
    builder.TemplateClosures.Add(new(new(0), []));
    builder.TemplateClosures.Add(new(new(0), [new(new(1), HirCaptureMode.Value, new(-1), templateCaptured)]));
    var closure = builder.Add(HirKind.Closure, function, source, data: 1);
    var templateClosure = builder.Add(HirKind.TemplateClosure, function, source, data: 1);
    builder.Matches.Add(new([]));
    builder.Matches.Add(new([new(new(HirPatternKind.Literal, 1), literal), new(new(HirPatternKind.Any), literal)]));
    var match = builder.Add(HirKind.Match, number, source, [literal], data: 1);
    var discardClosure = builder.Add(HirKind.Discard, builder.Type(TypeRef.Null), source, [closure]);
    var discardTemplate = builder.Add(HirKind.Discard, builder.Type(TypeRef.Null), source, [templateClosure]);
    var body = builder.Add(HirKind.Block, number, source, [discardClosure, discardTemplate, match]);
    builder.Functions[0] = builder.Functions[0] with { Body = body };
    return builder.Freeze(new(0));
}

static HirModule PayloadFixture()
{
    HirBuilder builder = new();
    builder.Sources.Add(new("payloads.msync", "x"));
    HirSourceRange source = new(new(0), 0, 1);
    var number = builder.Type(TypeRef.Number);
    var function = builder.Type(TypeRef.Function);
    var scopeType = builder.Type(TypeRef.Scope);
    var rangeType = builder.Type(new TypeRef(TypeKind.Range, TypeRef.Number));
    builder.Locations.Add(new("item", number, source, new(0)));
    builder.ScopeLayouts.Add(new([]));
    var value = builder.Literal(new(HirLiteralKind.Number, Number: 42), number, source);
    var native = builder.Add(HirKind.NativeFunction, function, source, text: "test");
    builder.Edges.Add(value);
    builder.Calls.Add(new(HirCallKind.Native, new(-1), "unused"));
    builder.Calls.Add(new(HirCallKind.Native, new(-1), "test"));
    builder.Scopes.Add(new(0, [])); builder.Scopes.Add(new(0, []));
    builder.Fors.Add(new(new(0), new(0))); builder.Fors.Add(new(new(1), new(0)));
    builder.Barriers.Add(new(false, false)); builder.Barriers.Add(new(true, true));
    builder.Ranges.Add(new([])); builder.Ranges.Add(new([false]));
    builder.Adaptations.Add(new([])); builder.Adaptations.Add(new([]));
    var call = builder.Add(HirKind.Call, number, source, [native], data: 1);
    var scope = builder.Add(HirKind.Scope, scopeType, source, data: 1);
    var range = builder.Add(HirKind.Range, rangeType, source, [value], data: 1);
    var lazy = builder.Add(HirKind.LazyRange, rangeType, source, [value], data: 1);
    var loop = builder.Add(HirKind.For, builder.Type(TypeRef.Null), source, [range, value], data: 1);
    var barrier = builder.Add(HirKind.Barrier, number, source, [value], data: 1);
    var adaptation = builder.Add(HirKind.AdaptFunction, function, source, [native], data: 1);
    var body = builder.Add(HirKind.Block, number, source, [call, scope, range, lazy, loop, barrier, adaptation, value]);
    builder.Functions.Add(new("main", [], [], number, body));
    return builder.Freeze(new(0)) with { UnresolvedImports = [new(0)] };
}
