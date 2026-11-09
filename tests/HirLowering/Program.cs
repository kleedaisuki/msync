using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;

int checks = 0;
foreach (var (name, make, expected) in new (string, Func<HirModule>, double)[]
{
    ("arithmetic", Arithmetic, 42), ("captured-location", Captured, 7),
    ("loop-value", LoopValue, 6), ("for-break", ForBreak, 6),
    ("match", Match, 7), ("scope-cell", ScopeCell, 9),
    ("callee-before-args", NativeOrder, 2), ("named-self-cell", ScopeSelf, 9),
    ("template-identity", TemplateIdentity, 7), ("forward-module-cell", ForwardModuleCell, 7),
    ("dynamic-type", DynamicType, 0), ("dynamic-type-order", DynamicTypeOrder, 2)
})
{
    HirModule hir = make();
    using MemoryStream exported = new();
    HirArtifact.Write(hir, exported);
    exported.Position = 0;
    HirModule loaded = HirArtifact.Read(exported);
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module code = Optimizer.Optimize(HirLowerer.Lower(loaded,
            new(OptimizationLevel: level)).Module, level);
        foreach (Module image in new[] { code, Read(code), Read(Binding.Bind(code,
            new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
        {
            Value value = await Interpreter.ExecuteAsync(image);
            Require(value is NumberValue number && number.Data == expected,
                $"{name} O{level} {image.Stage}: expected {expected}, found {value}");
        }
    }
}
HirBuilder canonical = new();
HirTypeId variable = canonical.Type(new HirType(TypeRef.Any, TypeParameter: 0));
HirTypeId textType = canonical.Type(TypeRef.String);
Require(canonical.Type(new HirType(TypeRef.Any, Alternatives: [variable, textType]))
    == canonical.Type(new HirType(TypeRef.Any, Alternatives: [variable, textType])),
    "Equivalent symbolic types did not retain one canonical identity");
Example persistent = New();
var persistentX = persistent.Location("x", persistent.NumberType);
persistent.Hir.SetExports([new("x", persistentX)]);
persistent.Hir.Functions[0] = persistent.Hir.Functions[0] with { Body = persistent.Declare(persistentX, persistent.Number(3)) };
HirLoweringSession backendSession = new();
await using ExecutionSession runtimeSession = new();
var firstGeneration = backendSession.Lower(persistent.Hir.Freeze(new(0), isSession: true));
await runtimeSession.ExecuteAsync(firstGeneration.Module);
int firstSlot = firstGeneration.Layout.Slot(new(0), persistentX);
var dependent = persistent.Hir.Freeze(new(0), isSession: true) with { UnresolvedImports = [persistentX] };
foreach (HirLoweringSession? session in new HirLoweringSession?[] { null, new() })
{
    bool rejected = false;
    try { HirLowerer.Lower(dependent, session: session); }
    catch (InvalidOperationException error) { rejected = error.Message.Contains("x", StringComparison.Ordinal); }
    Require(rejected, "Unbound imported location did not report its session dependency");
}
Require(HirLowerer.Lower(dependent, session: backendSession).Layout.Slot(new(0), persistentX) == firstSlot,
    "An existing session failed to resolve an explicit location import");
var persistentY = persistent.Location("y", persistent.NumberType);
persistent.Hir.SetExports([.. persistent.Hir.Exports, new("y", persistentY)]);
persistent.Hir.Functions[0] = persistent.Hir.Functions[0] with
{
    Body = persistent.Block(persistent.Assign(persistentX, persistent.Number(5)),
        persistent.Declare(persistentY, persistent.Binary(HirBinaryOperator.Add, persistent.Read(persistentX), persistent.Number(2))),
        persistent.Read(persistentY))
};
var secondGeneration = backendSession.Lower(persistent.Hir.Freeze(new(0), isSession: true));
Require(secondGeneration.Layout.Slot(new(0), persistentX) == firstSlot, "Persistent logical location changed its runtime slot");
Require(await runtimeSession.ExecuteAsync(secondGeneration.Module) is NumberValue { Data: 7 }, "Persistent HIR session lost its values");
Require(secondGeneration.Module.SessionRoots!.Count == 2, "Session exports were not mapped explicitly");
Example failing = New();
var broken = failing.Location("broken", failing.NumberType);
failing.Hir.SetExports([new("broken", broken)]);
var badText = failing.Hir.Literal(new(HirLiteralKind.String, Text: "not a number"),
    failing.Hir.Type(TypeRef.String), failing.Source);
failing.Hir.SetFunction(new(0), failing.Hir.Functions[0] with
{ Body = failing.Declare(broken, failing.Node(HirKind.Convert, failing.NumberType, [badText], failing.NumberType.Value)) });
var failedImage = HirLowerer.Lower(failing.Hir.Freeze(new(0), isSession: true));
await using ExecutionSession failedSession = new();
bool failed = false;
try { await failedSession.ExecuteAsync(failedImage.Module); }
catch (MsyncException) { failed = true; }
Require(failed, "The invalid Number initializer did not fail");
Require(!failedSession.IsLocalInitialized(failedImage.Layout.Slot(new(0), broken)),
    "A failed root initializer published a placeholder Null");
Example sites = New();
var firstObject = sites.Location("FirstSite", sites.ScopeType);
var firstValue = sites.Location("value", sites.NumberType, kind: HirLocationKind.Field);
sites.Hir.ScopeLayouts.Add(new([new("value", sites.NumberType)]));
int firstConstruction = sites.Hir.AddScope(new(0, [firstValue], Singleton: 0));
sites.Hir.SetExports([new("FirstSite", firstObject)]);
sites.Hir.SetFunction(new(0), sites.Hir.Functions[0] with { Result = sites.NullType,
    Body = sites.Declare(firstObject, sites.Node(HirKind.Scope, sites.ScopeType,
        [sites.Declare(firstValue, sites.Number(1))], firstConstruction)) });
HirLoweringSession siteLayout = new();
await using ExecutionSession siteRuntime = new();
await siteRuntime.ExecuteAsync(siteLayout.Lower(HirSealer.Seal(sites.Hir.Freeze(new(0), isSession: true))).Module);
var secondObject = sites.Location("SecondSite", sites.ScopeType);
var stringType = sites.Hir.Type(TypeRef.String);
var secondValue = sites.Location("value", stringType, kind: HirLocationKind.Field);
sites.Hir.ScopeLayouts.Add(new([new("value", stringType)]));
int secondConstruction = sites.Hir.AddScope(new(1, [secondValue], Singleton: 1));
sites.Hir.SetExports([.. sites.Hir.Exports, new("SecondSite", secondObject)]);
var secondText = sites.Hir.Literal(new(HirLiteralKind.String, Text: "second"), stringType, sites.Source);
sites.Hir.SetFunction(new(0), sites.Hir.Functions[0] with { Body = sites.Declare(secondObject,
    sites.Node(HirKind.Scope, sites.ScopeType, [sites.Declare(secondValue, secondText)], secondConstruction)) });
var compactSites = HirSealer.Seal(sites.Hir.Freeze(new(0), isSession: true));
Require(compactSites.Scopes.Length == 1 && compactSites.Scopes[0].Site == sites.Hir.Scopes[secondConstruction].Site,
    "Sealing lost a constructor's logical allocation identity");
var siteImage = siteLayout.Lower(compactSites);
await siteRuntime.ExecuteAsync(siteImage.Module);
Require(siteRuntime.GetLocalValue(siteImage.Layout.Slot(new(0), firstObject)) is ScopeValue oldScope
    && oldScope["value"] is NumberValue { Data: 1 }, "A later compacted constructor replaced an earlier singleton");
Require(siteRuntime.GetLocalValue(siteImage.Layout.Slot(new(0), secondObject)) is ScopeValue newScope
    && newScope["value"] is StringValue { Data: "second" }, "A compacted constructor reused another allocation site");
Console.WriteLine($"PASS {checks} pure HIR lowering/source-free execution contracts");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
static Module Read(Module module)
{
    using MemoryStream stream = new(); Artifact.Write(module, stream); stream.Position = 0; return Artifact.Read(stream);
}
static Example New() => new();
static HirModule Arithmetic()
{
    Example x = New(); return x.Done(x.Binary(HirBinaryOperator.Add, x.Number(40), x.Number(2)));
}
static HirModule Captured()
{
    Example x = New(); var captured = x.Location("captured", x.NumberType);
    var fn = x.Location("fn", x.FunctionType);
    var target = x.Location("captured", x.NumberType, 1);
    var parameter = x.Location("value", x.NumberType, 1, HirLocationKind.Parameter);
    HirCapture capture = new(target, HirCaptureMode.Location, captured, HirNodeId.None);
    x.Hir.Functions.Add(new("add", [parameter], [capture], x.NumberType,
        x.Binary(HirBinaryOperator.Add, x.Read(target), x.Read(parameter))));
    int closure = x.Hir.Closures.Count; x.Hir.Closures.Add(new(new(1), [capture]));
    var value = x.Node(HirKind.Closure, x.FunctionType, data: closure);
    int call = x.Hir.Calls.Count; x.Hir.Calls.Add(new(HirCallKind.Function, new(1)));
    return x.Done(x.Block(x.Declare(captured, x.Number(1)), x.Declare(fn, value),
        x.Assign(captured, x.Number(3)), x.Node(HirKind.Call, x.NumberType, [x.Read(fn), x.Number(4)], call)));
}
static HirModule LoopValue()
{
    Example x = New(); var total = x.Location("total", x.NumberType);
    var loopId = new HirLoopId(0);
    var body = x.Block(x.Assign(total, x.Binary(HirBinaryOperator.Add, x.Read(total), x.Number(1))),
        x.Node(HirKind.If, x.NullType,
            [x.Binary(HirBinaryOperator.GreaterEqual, x.Read(total), x.Number(6)),
             x.Node(HirKind.Break, x.NeverType, [x.Read(total)], loopId.Value), x.Null()]));
    return x.Done(x.Block(x.Declare(total, x.Number(0)), x.Node(HirKind.Loop, x.NumberType, [body], loopId.Value)));
}
static HirModule ForBreak()
{
    Example x = New(); var total = x.Location("total", x.NumberType);
    var item = x.Location("item", x.NumberType);
    x.Hir.Ranges.Add(new([]));
    var values = x.Node(HirKind.Range, x.Hir.Type(new TypeRef(TypeKind.Range, TypeRef.Number)),
        [x.Number(1), x.Number(2), x.Number(3), x.Number(4)]);
    var body = x.Block(x.Assign(total, x.Binary(HirBinaryOperator.Add, x.Read(total), x.Read(item))),
        x.Node(HirKind.If, x.NullType,
            [x.Binary(HirBinaryOperator.Equal, x.Read(item), x.Number(3)),
             x.Node(HirKind.Break, x.NeverType, data: 0), x.Null()]));
    x.Hir.Fors.Add(new(new(0), item));
    return x.Done(x.Block(x.Declare(total, x.Number(0)), x.Node(HirKind.For, x.NullType, [values, body]), x.Read(total)));
}
static HirModule Match()
{
    Example x = New(); int literal = x.Hir.Literals.Count;
    x.Hir.Literals.Add(new(HirLiteralKind.Bool, Boolean: true));
    x.Hir.Matches.Add(new([new(new(HirPatternKind.Literal, literal), x.Number(7)),
        new(new(HirPatternKind.Any), x.Number(9))]));
    return x.Done(x.Node(HirKind.Match, x.NumberType,
        [x.Hir.Literal(new(HirLiteralKind.Bool, Boolean: true), x.BoolType, x.Source)]));
}
static HirModule ScopeCell()
{
    Example x = New(); var field = x.Location("value", x.NumberType, kind: HirLocationKind.Field);
    var view = x.Location("scope", x.ScopeType);
    x.Hir.ScopeLayouts.Add(new([new("value", x.NumberType)]));
    x.Hir.Scopes.Add(new(0, [field]));
    var scope = x.Node(HirKind.Scope, x.ScopeType, [x.Declare(field, x.Number(3))]);
    return x.Done(x.Block(x.Declare(view, scope),
        x.Node(HirKind.MemberAssign, x.NullType, [x.Read(view), x.Number(9)], text: "value"),
        x.Node(HirKind.MemberRead, x.NumberType, [x.Read(view)], text: "value")));
}
static HirModule NativeOrder()
{
    Example x = New(); var trace = x.Location("trace", x.NumberType);
    var native = x.Node(HirKind.NativeFunction, x.FunctionType, text: "math.abs");
    var callee = x.Block(x.Assign(trace, x.Number(1)), native);
    var argument = x.Block(x.Assign(trace, x.Number(2)), x.Number(-4));
    x.Hir.Calls.Add(new(HirCallKind.Native, new(-1), "math.abs"));
    return x.Done(x.Block(x.Declare(trace, x.Number(0)),
        x.Node(HirKind.Call, x.NumberType, [callee, argument]), x.Read(trace)));
}

static HirModule ScopeSelf()
{
    Example x = New(); var self = x.Location("Family", x.ScopeType);
    var target = x.Location("Family", x.ScopeType, 1);
    var reader = x.Location("read", x.FunctionType, kind: HirLocationKind.Field);
    var field = x.Location("item", x.NumberType, kind: HirLocationKind.Field);
    var original = x.Location("original", x.ScopeType);
    var replacement = x.Location("item", x.NumberType, kind: HirLocationKind.Field);
    HirCapture capture = new(target, HirCaptureMode.Location, self, HirNodeId.None);
    x.Hir.Functions.Add(new("read", [], [capture], x.NumberType,
        x.Node(HirKind.MemberRead, x.NumberType, [x.Read(target)], text: "item")));
    x.Hir.Closures.Add(new(new(1), [capture]));
    x.Hir.ScopeLayouts.Add(new([new("read", x.FunctionType), new("item", x.NumberType)]));
    x.Hir.Scopes.Add(new(0, [reader, field], Singleton: 0, Self: self));
    var initial = x.Node(HirKind.Scope, x.ScopeType,
        [x.Declare(reader, x.Node(HirKind.Closure, x.FunctionType)), x.Declare(field, x.Number(3))]);
    x.Hir.ScopeLayouts.Add(new([new("item", x.NumberType)]));
    x.Hir.Scopes.Add(new(1, [replacement]));
    var next = x.Node(HirKind.Scope, x.ScopeType, [x.Declare(replacement, x.Number(9))], 1);
    x.Hir.Calls.Add(new(HirCallKind.Function, new(1)));
    return x.Done(x.Block(x.Declare(self, initial), x.Declare(original, x.Read(self)), x.Assign(self, next),
        x.Node(HirKind.Call, x.NumberType,
            [x.Node(HirKind.MemberRead, x.FunctionType, [x.Read(original)], text: "read")])));
}
static HirModule TemplateIdentity()
{
    Example x = New(); var captured = x.Location("captured", x.NumberType);
    var value = x.Location("fn", x.FunctionType);
    var genericType = x.Hir.Type(new HirType(TypeRef.Any, TypeParameter: 0));
    HirLocationId genericCapture = new(x.Hir.Locations.Count);
    x.Hir.Locations.Add(new("captured", x.NumberType, x.Source, new(-1), HirLocationKind.Import, TemplateOwner: new(0)));
    HirLocationId genericParameter = new(x.Hir.Locations.Count);
    x.Hir.Locations.Add(new("value", genericType, x.Source, new(-1), HirLocationKind.Parameter, TemplateOwner: new(0)));
    HirCapture templateCapture = new(genericCapture, HirCaptureMode.Location, captured, HirNodeId.None);
    x.Hir.Templates.Add(new("generic_add", ["T"], [genericParameter],
        x.Binary(HirBinaryOperator.Add, x.Read(genericCapture), x.Read(genericParameter)), x.Source,
        Captures: [templateCapture], TypeParameterTypes: [genericType]));
    var closedCapture = x.Location("captured", x.NumberType, 1);
    var closedParameter = x.Location("value", x.NumberType, 1, HirLocationKind.Parameter);
    HirCapture instanceCapture = new(closedCapture, HirCaptureMode.Location, captured, HirNodeId.None);
    x.Hir.Functions.Add(new("generic_add<Number>", [closedParameter], [instanceCapture], x.NumberType,
        x.Binary(HirBinaryOperator.Add, x.Read(closedCapture), x.Read(closedParameter)), new(0)));
    x.Hir.TemplateClosures.Add(new(new(0), [templateCapture]));
    x.Hir.Calls.Add(new(HirCallKind.Dynamic, new(-1), Targets: [new(HirCallableId.From(new HirTemplateId(0)), new(1))]));
    return x.Done(x.Block(x.Declare(captured, x.Number(3)),
        x.Declare(value, x.Node(HirKind.TemplateClosure, x.FunctionType)),
        x.Node(HirKind.Call, x.NumberType, [x.Read(value), x.Number(4)])));
}

static HirModule ForwardModuleCell()
{
    Example x = New();
    var module = x.Location("module", x.ScopeType, kind: HirLocationKind.Import);
    var capture = x.Location("module", x.ScopeType, 1, HirLocationKind.Import);
    var getter = x.Location("get", x.FunctionType);
    var field = x.Location("value", x.NumberType, kind: HirLocationKind.Field);
    HirCapture shared = new(capture, HirCaptureMode.Location, module, HirNodeId.None);
    x.Hir.Functions.Add(new("get", [], [shared], x.NumberType,
        x.Node(HirKind.MemberRead, x.NumberType, [x.Read(capture)], text: "value")));
    x.Hir.Closures.Add(new(new(1), [shared]));
    x.Hir.ScopeLayouts.Add(new([new("value", x.NumberType)]));
    x.Hir.AddScope(new(0, [field]));
    x.Hir.Calls.Add(new(HirCallKind.Function, new(1)));
    return x.Done(x.Block(x.Declare(getter, x.Node(HirKind.Closure, x.FunctionType)),
        x.Declare(module, x.Node(HirKind.Scope, x.ScopeType, [x.Declare(field, x.Number(7))])),
        x.Node(HirKind.Call, x.NumberType, [x.Read(getter)])));
}
static HirModule DynamicType()
{
    Example x = New(); var typeType = x.Hir.Type(TypeRef.Type);
    var target = x.Location("target", typeType);
    var number = x.Hir.Literal(new(HirLiteralKind.Type, Type: TypeRef.Number), typeType, x.Source);
    var text = x.Hir.Literal(new(HirLiteralKind.Type, Type: TypeRef.String), typeType, x.Source);
    var check = x.Node(HirKind.IsType, x.BoolType, [x.Number(3), x.Read(target)], data: -1);
    return x.Done(x.Block(x.Declare(target, number), x.Assign(target, text),
        x.Node(HirKind.If, x.NumberType, [check, x.Number(1), x.Number(0)])));
}
static HirModule DynamicTypeOrder()
{
    Example x = New(); var trace = x.Location("trace", x.NumberType);
    var typeType = x.Hir.Type(TypeRef.Type);
    var number = x.Hir.Literal(new(HirLiteralKind.Type, Type: TypeRef.Number), typeType, x.Source);
    var left = x.Block(x.Assign(trace, x.Number(1)), x.Number(3));
    var right = x.Block(x.Assign(trace, x.Number(2)), number);
    return x.Done(x.Block(x.Declare(trace, x.Number(0)),
        x.Node(HirKind.IsType, x.BoolType, [left, right], data: -1), x.Read(trace)));
}

internal sealed class Example
{
    internal HirBuilder Hir { get; } = new();
    internal HirSourceRange Source { get; } = new(new(0), 0, 0);
    internal HirTypeId NumberType { get; }
    internal HirTypeId NullType { get; }
    internal HirTypeId BoolType { get; }
    internal HirTypeId FunctionType { get; }
    internal HirTypeId ScopeType { get; }
    internal HirTypeId NeverType { get; }
    internal Example()
    {
        Hir.Sources.Add(new("<manual-semantic-hir>", "This is not parseable source and must never be executed."));
        NumberType = Hir.Type(TypeRef.Number); NullType = Hir.Type(TypeRef.Null);
        BoolType = Hir.Type(TypeRef.Bool); FunctionType = Hir.Type(TypeRef.Function);
        ScopeType = Hir.Type(TypeRef.Scope); NeverType = Hir.Type(new TypeRef(TypeKind.Never));
        Hir.Functions.Add(new("<main>", [], [], NumberType, HirNodeId.None));
    }
    internal HirLocationId Location(string name, HirTypeId type, int owner = 0, HirLocationKind kind = HirLocationKind.Local)
    { HirLocationId id = new(Hir.Locations.Count); Hir.Locations.Add(new(name, type, Source, new(owner), kind)); return id; }
    internal HirNodeId Node(HirKind kind, HirTypeId type, ReadOnlySpan<HirNodeId> children = default,
        int data = 0, string? text = null) => Hir.Add(kind, type, Source, children, data, text);
    internal HirNodeId Number(double value) => Hir.Literal(new(HirLiteralKind.Number, Number: value), NumberType, Source);
    internal HirNodeId Null() => Hir.Literal(new(HirLiteralKind.Null), NullType, Source);
    internal HirNodeId Read(HirLocationId location) => Node(HirKind.ReadLocation, Hir.Locations[location.Value].StorageType, data: location.Value);
    internal HirNodeId Declare(HirLocationId location, HirNodeId value) => Node(HirKind.Declare, NullType, [value], location.Value);
    internal HirNodeId Assign(HirLocationId location, HirNodeId value) => Node(HirKind.Assign, NullType, [value], location.Value);
    internal HirNodeId Block(params HirNodeId[] values) => Node(HirKind.Block, Hir.Nodes[values[^1].Value].Type, values);
    internal HirNodeId Binary(HirBinaryOperator operation, HirNodeId left, HirNodeId right) =>
        Node(HirKind.Binary, operation >= HirBinaryOperator.Equal ? BoolType : NumberType, [left, right], (int)operation);
    internal HirModule Done(HirNodeId body)
    { Hir.Functions[0] = Hir.Functions[0] with { Body = body }; return Hir.Freeze(new(0)); }
}
