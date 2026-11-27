using System.Collections.Immutable;
using Msync.Compiler;
using Msync.Core;
using Msync.Hir;

int checks = 0;

const string joined = """
    let flag = $std::io::read_line() is null;
    let selected = if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } };
    selected::key
    """;
Module shapeJoin = Lower(joined);
var selector = shapeJoin.FieldProjections.Single(place => place.Name == "key");
Require(selector.Cases.Count == 2 && selector.Cases.Select(place => place.Ordinal).Order().SequenceEqual([0, 1]),
    "the HIR join should supply both logical key ordinals");
Require(selector.Cases.All(place => shapeJoin.ScopeLayouts[place.Layout].Fields[place.Ordinal].Name == "key"),
    "every case should address the originally declared member");
Require(Ops(shapeJoin).Count(op => op.Op == OpCode.NewScope
        && selector.Cases.Any(place => place.Layout == op.A)) == 2
    && Ops(shapeJoin).Any(op => op.Op == OpCode.LoadField) && !Ops(shapeJoin).Any(op => op.Op == OpCode.AdmitScope),
    "construction and join should require no admission between constructor and field");

const string returned = """
    fn choose(flag: Bool) {
        if flag { object { alpha: 0, key: 11 } } else { object { key: 22, zed: 0 } }
    };
    let flag = $std::io::read_line() is null;
    let selected = choose(flag);
    selected::key
    """;
Module call = Lower(returned);
var specialization = call.Functions.Single(function => function.Name == "choose" && function.ReturnType == TypeRef.Scope);
Require(specialization.CodeSignature is { Parameters.Count: 1 } signature
    && call.CodeTypes[signature.Parameters[0]].Kind == TypeKind.Bool
    && call.CodeTypes[signature.Result].ScopeLayouts is { Count: 2 },
    "a specialized callable signature should carry its return alternatives");
Require(!Ops(call).Any(op => op.Op == OpCode.AdmitScope), "a closed call should not re-admit its typed result");

Module iteration = Lower("""
    let selected = 0;
    for operation in [object { insert: true, value: 3 }, object { value: 4, insert: false }] {
        if operation::insert { selected = selected + operation::value; };
    };
    selected
    """);
Require(iteration.FieldProjections.Single(place => place.Name == "insert").Cases.Count == 2
    && iteration.FieldProjections.Single(place => place.Name == "value").Cases.Count == 2,
    "loop flow must preserve the actual evaluated iterable's constructor domain, not synthetic forecast shapes");

Module entryDomain = Lower("""
    concept Keyed { key: Number };
    fn update(row: Keyed) {
        let first = row::key;
        row = object { key: 29, extra: true };
        first + row::key
    };
    update(object { alpha: 0, key: 11 })
    """);
var update = entryDomain.Functions.Single(function => function.Name == "update" && function.ReturnType == TypeRef.Number);
Require(update.CodeSignature is { Parameters.Count: 1 } updateSignature
    && entryDomain.CodeTypes[updateSignature.Parameters[0]].ScopeLayouts is [var incoming]
    && entryDomain.ScopeLayouts[incoming].Fields.Select(field => field.Name).SequenceEqual(["alpha", "key"]),
    "the structural parameter retains its narrower incoming domain");
Require(update.Instructions.Count(op => op.Op == OpCode.LoadField) == 2
    && !update.Instructions.Any(op => op.Op == OpCode.AdmitScope),
    "entry proof and later legal structural rebind should not re-admit either read");

Module extracted = Lower(joined.Replace("selected::key", "$std::ranges::front([selected])::key", StringComparison.Ordinal));
Require(Ops(extracted).Count(op => op.Op == OpCode.AdmitScope) == 1
    && extracted.FieldProjections.Single(place => place.Name == "key").Cases.Count == 2,
    "a selected opaque native producer must admit its scope once before typed access");

Module captured = Lower(joined.Replace("selected::key", "fn read { selected::key }; read()", StringComparison.Ordinal));
Require(Ops(captured).Any(op => op.Op == OpCode.LoadCell && op.B > 0)
    && captured.Functions.Any(function => function.CodeSignature?.Captures.Any(capture => capture.Location) == true),
    "capture places must carry their fixed storage contract and Location mode");
Require(Ops(captured).Any(op => op.Op == OpCode.AdmitScope),
    "the selected payload read from an opaque captured location must be admitted");

Module nestedCapture = Lower("""
    fn capture(row) {
        fn get { row::key };
        get
    };
    let pick = capture(object { key: 11, extra: true });
    pick()
    """);
var nestedPlaces = nestedCapture.Functions.SelectMany(function => function.CodeSignature!.Captures)
    .Where(capture => capture.Location && nestedCapture.CodeTypes[capture.Type].Kind == TypeKind.Scope)
    .ToArray();
Require(nestedPlaces.Length != 0
    && nestedPlaces.All(capture => nestedCapture.CodeTypes[capture.Type].ScopeLayouts is null or { Count: 0 }),
    "a generic parameter's initial Object layout must not become its mutable capture storage contract");

Module opaque = Lower("""
    let flag = $std::io::read_line() is null;
    let selected = if flag { object { name: "manual", value: "chosen" } }
        else { $std::ranges::front($std::environment::variables()) };
    selected::value
    """);
Require(opaque.FieldProjections.Single(place => place.Name == "value").Cases.Count == 0,
    "a structural view must not supply the opaque producer a construction layout");

Module staticScope = Lower("""
    object Counter { previous: 1, value: 2 };
    Counter::value = 7;
    Counter::previous + Counter::value
    """);
Instruction[] staticInstructions = Ops(staticScope);
Require(staticScope.StaticScopes.Count == 1
    && staticInstructions.Count(op => op.Op == OpCode.JumpIfStaticReady) == 1
    && staticInstructions.Count(op => op.Op == OpCode.PublishStatic) == 1,
    "the declaration should have one selected publication");
Require(staticInstructions.Count(op => op.Op == OpCode.LoadStaticField) == 2
    && staticInstructions.Count(op => op.Op == OpCode.StoreStaticField) == 1
    && !staticInstructions.Any(op => op.Op is OpCode.NewScope or OpCode.LoadStaticScope),
    "a dominating lexical namespace should publish fields without allocating a receiver wrapper");

Module escaped = Lower("""
    object Counter { value: 2 };
    let alias = Counter;
    alias::value = 7;
    Counter::value
    """);
Require(Ops(escaped).Any(op => op.Op == OpCode.LoadStaticScope)
    && !Ops(escaped).Any(op => op.Op is OpCode.LoadStaticField or OpCode.StoreStaticField),
    "a whole-value use must retain the named Object identity");

Module nestedField = Lower("""
    let owner = object { sample: object { group: "", id: 0 }, key: 7 };
    owner::key
    """);
Require(Ops(nestedField).Count(op => op.Op == OpCode.NewScope) == 2,
    "the enclosing constructor implicitly consumes even an unselected nested field");

Module process = Lower("""
    let task = $std::process::spawn(./unselected, []);
    task::stdout
    """);
Require(Ops(process).Any(op => op is { Op: OpCode.NativeCall, A: 1, Text: "process.stdout" }),
    "Process member capabilities should not use the Object place path");

foreach (Module module in new[] { shapeJoin, call, iteration, entryDomain, extracted, captured, nestedCapture, opaque,
    staticScope, escaped, nestedField, process })
{
    Require(module.CodeTypes.Select((type, i) => type.Element < i).All(ordered => ordered),
        "element code types must precede their parent");
    Require(module.Functions.All(function => function.CodeSignature is not null),
        "every finalized function or template identity must carry a code signature");
    Require(Ops(module).Where(op => op.Op is OpCode.NewScope or OpCode.LoadField or OpCode.StoreField
        or OpCode.LoadStaticField or OpCode.StoreStaticField).All(op => op.Text is null),
        "portable field instructions may not carry legacy name payloads");
    using MemoryStream artifact = new();
    Artifact.Write(module, artifact);
    artifact.Position = 0;
    var imported = Artifact.Read(artifact);
    Require(imported.ScopeLayouts.Count == module.ScopeLayouts.Count
        && imported.FieldProjections.Count == module.FieldProjections.Count
        && imported.StaticScopes.Count == module.StaticScopes.Count,
        "portable code layout, projection and static tables must survive their own codec");
}

Console.WriteLine($"PASS {checks} source-free HIR-to-portable place producer contracts");

static Module Lower(string source)
{
    HirModule hir = Compiler.Analyze(source, "portable-places");
    hir = hir with { Sources = hir.Sources.Select(item => item with { Text = null }).ToImmutableArray() };
    using MemoryStream stream = new();
    HirArtifact.Write(hir, stream);
    stream.Position = 0;
    return HirLowerer.Lower(HirArtifact.Read(stream), new(OptimizationLevel: 0)).Module;
}

static Instruction[] Ops(Module module) => module.Functions.SelectMany(function => function.Instructions).ToArray();

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
