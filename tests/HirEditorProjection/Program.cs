using System.Collections.Immutable;
using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

int checks = 0;
HirBuilder builder = new();
builder.Sources.Add(new("file:///edited.msync", new string(' ', 100)));
builder.Sources.Add(new("msync-stdlib:///0.8.0/concepts.msync", "😀 definition"));
HirTypeId number = builder.Type(TypeRef.Number);
HirTypeId text = builder.Type(TypeRef.String);
HirTypeId boolean = builder.Type(TypeRef.Bool);
HirTypeId scope = builder.Type(TypeRef.Scope);
HirTypeId function = builder.Type(TypeRef.Function);
HirTypeId unknown = builder.Type(new HirType(TypeRef.Any, TypeParameter: 0));
HirTypeId nullablePath = builder.Type(TypeRef.Path.WithNull);
HirTypeId type = builder.Type(TypeRef.Type);
HirTypeId process = builder.Type(TypeRef.Process);
HirSourceRange origin = new(new(0), 0, 1);
HirNodeId body = builder.Add(HirKind.Block, boolean, origin);
builder.Locations.Add(new("path", nullablePath, origin, new(0), HirLocationKind.Parameter, AdmitsNull: true));
builder.Locations.Add(new("value", unknown, origin, new(0), HirLocationKind.Parameter));
builder.Functions.Add(new("original", [new(0)], [], boolean, body));
builder.Templates.Add(new("generic", ["T"], [new(1)], HirNodeId.None, origin));
builder.ScopeLayouts.Add(new([new("child", new(builder.Types.Count))]));
HirTypeId recursiveType = builder.Type(new HirType(TypeRef.Scope, Shape: 0));
Require(recursiveType.Value == builder.ScopeLayouts[0].Fields[0].StorageType.Value, "Recursive type construction");
builder.ScopeLayouts.Add(new([new("optional", number, AdmitsNull: true)]));
HirTypeId optionalType = builder.Type(new HirType(TypeRef.Scope, Shape: 1));

List<SemanticValueView> values = [];
SemanticValueId Add(SemanticValueView value)
{
    SemanticValueId id = new(values.Count);
    values.Add(value);
    return id;
}
SemanticValueId num = Add(new(number));
SemanticValueId str = Add(new(text));
SemanticValueId flag = Add(new(boolean));
SemanticValueId nestedA = Add(new(scope, Members: [new("shared", num), new("only_a", flag)]));
SemanticValueId nestedB = Add(new(scope, Members: [new("shared", str), new("only_b", flag)]));
SemanticValueId first = Add(new(scope, Members: [new("nested", nestedA), new("first", num)]));
SemanticValueId second = Add(new(scope, Members: [new("nested", nestedB), new("second", str)]));
SemanticValueId named = Add(new(scope, IsNamedScope: true, Members: []));
SemanticValueId anonymous = Add(new(scope, Members: []));
SemanticValueId optionalContract = Add(new(scope, Members: [new("value", num, AdmitsNull: true)]));
SemanticValueId callable = Add(new(function, Function: new(0)));
SemanticValueId template = Add(new(function, Template: new(0)));
SemanticValueId missing = Add(new(unknown, IsUnknown: true));
SemanticValueId typeValue = Add(new(type, TypeValue: nullablePath));
SemanticValueId shape = Add(new(recursiveType));
SemanticValueId optional = Add(new(optionalType));
SemanticValueId childProcess = Add(new(process));
SemanticValueId cycleA = new(values.Count);
values.Add(new(scope, Members: [new("self", cycleA), new("leaf", num)]));
SemanticValueId cycleB = new(values.Count);
values.Add(new(scope, Members: [new("self", cycleB), new("leaf", str)]));
SemanticValueId alias = Add(new(function, NativeName: "storage.buffer_get",
    Signature: "native fn buffer_get(items: Range, index: Number) -> Number", Documentation: "Indexed element lookup."));
SemanticValueId native = Add(new(function, NativeName: "meta.is_invocable"));
SemanticValueId path = Add(new(builder.Type(TypeRef.Path), Path: new("./file", "/origin", new(ConnectionKind.Ssh, "forge"))));

ImmutableDictionary<string, SemanticBindingView> Bind(string name, SemanticValueId value, bool parameter = false) =>
    ImmutableDictionary<string, SemanticBindingView>.Empty.Add(name, new(name, new(0), value, parameter));
SemanticObservationView view = new(builder.Freeze(new(0)), new(0), values.ToImmutableArray(),
    [new(new(new(0), 2, 3), named), new(new(new(0), 2, 3), anonymous, Concrete: false)],
    [
        new(new(0, 100), true, 0, Bind("outer", num), ImmutableDictionary<string, SemanticConceptView>.Empty),
        new(new(20, 30), false, 25, Bind("parameter", named, true), ImmutableDictionary<string, SemanticConceptView>.Empty)
    ],
    [new(new(60, 3), first), new(new(60, 3), second)],
    [new(new(70, 3), new(ConnectionKind.Ssh, "forge")), new(new(75, 3), new(ConnectionKind.Local)), new(new(75, 3), new(ConnectionKind.Ssh, "forge"))],
    [], [new(new(80, 2), [new(new(1), 3, 10)])]);
int nativeQueries = 0;
NativePresentation? Native(string identity)
{
    nativeQueries++;
    Require(identity == "meta.is_invocable", "Only requested native metadata is loaded");
    return new("native fn is_invocable(callee, ...arguments) -> Bool", "Infer callable compatibility without executing it.");
}
SemanticValuePresenter presenter = new(view, Native);
Require(nativeQueries == 0, "Presenter construction does not load native declarations");
Require(presenter.Describe("Named", [named]).SemanticRole == "type", "Named Scope has type role");
Require(presenter.Describe("instance", [anonymous]).SemanticRole == "variable", "Anonymous Scope has variable role");
Require(presenter.Describe("alias", [callable]).Signature == "fn alias(path?: Path) -> Bool", "Function names, parameter names and nullable spelling");
Require(presenter.Describe("alias", [callable]).SemanticRole == "function", "Callable role");
Require(presenter.Describe("generic", [template]).Signature.Contains("inferred at call", StringComparison.Ordinal), "Open template has a useful signature without a body");
Require(presenter.Describe("missing", [missing]).Type == "unknown", "Error/unknown type is not a Path or full Any listing");
Require(presenter.Describe("T", [typeValue]).Signature == "T: Type (Path?)", "Denoted type survives presentation");
Require(presenter.Describe("empty", []).Type == "unknown", "Missing expression has an unknown descriptor");
SemanticMember merged = presenter.Describe("value", [first, second]);
Require(merged.Members is { Count: 1 }, "Only common outer fields are offered");
SemanticMember nested = merged.Members!.Single();
Require(nested.Name == "nested" && nested.Members is { Count: 1 }, "Common nested capabilities survive merging");
Require(nested.Members!.Single().Name == "shared" && nested.Members!.Single().Type == "String | Number", "Nested union is typed and excludes branch-specific fields");
Require(presenter.Describe("recursive", [shape]).Members!.Single().Members is null, "Recursive type shape terminates by identity");
Require(presenter.Describe("cycle", [cycleA]).Members!.First().Members is null, "Recursive value shape terminates by identity");
SemanticMember cycles = presenter.Describe("cycles", [cycleA, cycleB]);
Require(cycles.Members!.Single(item => item.Name == "self").Members is null, "Recursive variant intersection terminates by identity pair");
Require(cycles.Members!.Single(item => item.Name == "leaf").Type == "String | Number", "Recursive intersection retains ordinary fields");
Require(presenter.Describe("record", [optional]).Members!.Single().Type == "Number?", "Shape-only nullable field admission");
Require(presenter.Describe("contract", [optionalContract]).Members!.Single().Type == "Number?", "Constraint-only nullable field admission");
Require(presenter.Describe("process", [childProcess]).Members!.Select(item => item.Name).SequenceEqual(["stdout", "stderr", "exit_code", "success"]), "Process fields require no extra HIR type entries");
Require(presenter.Describe("get", [alias]).Signature.StartsWith("native fn get(items:", StringComparison.Ordinal), "Native aliases retain actual parameter names");
Require(nativeQueries == 0, "Provided native documentation does not reload source");
Require(presenter.Describe("check", [native]).Signature == "native fn check(callee, ...arguments) -> Bool", "Metadata-backed native alias signature");
Require(presenter.Describe("check", [native]).Description.Contains("without executing", StringComparison.Ordinal), "Native query description");
Require(nativeQueries == 1, "Repeated native queries reuse one frozen descriptor");
Require(presenter.Describe("file", [path]).KnownPath is { Raw: "./file", WorkingDirectory: "/origin", Connection.Address: "forge" }, "Path projection retains source and endpoint provenance");

EditorSemanticIndex index = new(view);
Require(index.Occurrences().Single().SemanticRole == "type", "Concrete observations beat generic recovery alternatives");
Require(index.At(24).Bindings.Single().Name == "outer", "Future local declaration is not visible");
Require(index.At(26).Bindings.Single() is { Name: "parameter", SemanticRole: "variable" }, "Parameter does not inherit a named Scope type role");
Require(index.At(55).Bindings.Single().Name == "outer", "Leaving lexical owner restores outer scope");
Require(index.At(61).Receiver!.Members!.Single().Members!.Single().Name == "shared", "Receiver completion preserves nested generic intersection");
Require(index.At(71) is { HasPhysicalOwner: true, PhysicalEndpoint.Address: "forge" }, "Physical owner is preserved");
Require(index.At(76) is { HasPhysicalOwner: true, PhysicalEndpoint: null }, "Ambiguous endpoint does not select an old branch");
EditorLocation definition = index.DefinitionsAt(81).Single();
Require(definition.SourceName.StartsWith("msync-stdlib:", StringComparison.Ordinal) && definition.Source == "😀 definition", "Definitions retain their own source document");
Require(definition.Span.Start == 3, "Definition offset remains UTF-16 after a surrogate pair");
Require(index.DefinitionsAt(82).Count == 1, "Definition lookup permits token end caret");
Require(index.DefinitionsAt(90).Count == 0, "Unrelated source has no stale definition");
ProjectionRetention.Run(Require);
Console.WriteLine($"PASS {checks} immutable HIR editor projection contracts");

void Require(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException(name);
}
