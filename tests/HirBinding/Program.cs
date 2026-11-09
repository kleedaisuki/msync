using Msync.Compiler;
using Msync.Compiler.Semantics;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

if (args.Contains("--singletons", StringComparer.Ordinal))
{
    await SingletonChecks.RunAsync();
    return;
}

if (args.Contains("--aggregates", StringComparer.Ordinal))
{
    await AggregateChecks.RunAsync();
    return;
}

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
BoundProgram Bind(string text, bool editor = false) => new SourceBinder(observations: editor, editorMode: editor)
    .Bind(text, "binding.msync", editor ? Parser.ParseForEditor(text, "binding.msync").Document : null);
HirLocationId Original(BoundProgram program, HirLocationId location)
{
    while (program.Declaration(location).CapturedFrom is { } parent) location = parent;
    return location;
}

var shadow = Bind("let value = 1; let before = value; { let value = value; value }; value", true);
var values = shadow.Declarations.Where(item => item.Name == "value").ToArray();
Check(values.Length == 2 && values[0].Location != values[1].Location, "Shadowing creates a distinct declaration identity");
var reads = shadow.References.Where(item => !item.Declaration && shadow.Declaration(item.Location).Name == "value").ToArray();
Check(reads.Length == 4, "All value references have semantic source entries");
Check(reads[0].Location == values[0].Location && reads[1].Location == values[0].Location,
    "The shadowing initializer sees the previous declaration");
Check(reads[2].Location == values[1].Location && reads[3].Location == values[0].Location,
    "Inner and outer reads keep their own identities");

var fallback = Bind("not_declared; $wsl; ssh://forge");
Check(fallback.Hir.Literals.Any(item => item is { Kind: HirLiteralKind.Path, Text: "not_declared" }),
    "An unbound bare name remains a Path");
Check(fallback.Hir.Nodes.Any(item => item is { Kind: HirKind.Builtin, Text: "wsl" }), "The dollar endpoint is explicit");
Check(fallback.Hir.Literals.Any(item => item is { Kind: HirLiteralKind.Connection, ConnectionKind: ConnectionKind.Ssh, Text: "forge" }),
    "An SSH URL is a Connection literal, not a local Path");

var lexical = Bind("let value = 1; fn outer() { fn inner() { value }; inner }; let value = 2; outer");
var first = lexical.Declarations.First(item => item.Name == "value");
var inner = lexical.Templates.Single(item => item.Name == "inner");
var outer = lexical.Templates.Single(item => item.Name == "outer");
Check(inner.Captures.Length == 1 && outer.Captures.Length == 1,
    "A nested closure propagates the ancestor capture through its creator");
Check(Original(lexical, inner.Captures[0]) == first.Location, "A closure binds definition-time ancestry");
Check(lexical.Hir.Templates[inner.Id.Value].Captures.All(item => item.Mode == HirCaptureMode.Location),
    "Source closures explicitly retain locations");

var recursive = Bind("fn recurse(value) { if value { recurse(false) } else { 0 } }; recurse");
var own = recursive.Declarations.Single(item => item.Name == "recurse" && item.Kind == DeclarationKind.Function);
Check(recursive.Templates[0].Captures.Any(item => Original(recursive, item) == own.Location),
    "A function's own declaration is reserved before its body");

var imported = Bind("let state = 1; fn write() { global state; state = 2; }; write", true);
var state = imported.Declarations.Single(item => item.Name == "state" && item.Kind == DeclarationKind.Local);
var stored = imported.Hir.Nodes.Single(item => item.Kind == HirKind.Assign);
Check(Original(imported, new(stored.Data)) == state.Location, "global assignment addresses the lexical ancestor");
Check(imported.GlobalImports.Count == 1, "The editor receives exact global-import source metadata");
foreach (string invalid in new[] { "global missing;", "fn bad() { global missing; };", "let x=1; fn bad(x) { global x; };" })
{
    try { _ = Bind(invalid); throw new InvalidOperationException("Invalid global was accepted"); }
    catch (CompileException error) { Check(error.Diagnostic.Code == "E0214", "Global failure has a source-owned diagnostic"); }
}

var scopes = Bind("scope Family { read: fn { Family::later }, later: 4 }; let local = scope { value: 1 }; let other = scope Named { value: 2 };", true);
Check(scopes.Hir.Scopes.Count == 3, "Each source Scope has its own construction descriptor");
Check(scopes.Hir.Scopes[0].Self is not null && scopes.Hir.Scopes[0].Singleton >= 0,
    "A named declaration reserves self and singleton identity");
Check(scopes.Hir.Scopes[1].Singleton < 0 && scopes.Hir.Scopes[2].Singleton >= 0,
    "Anonymous Scopes stay fresh and named expressions stay singleton declarations");
Check(scopes.MemberUses.Single().Name == "later", "Member access retains its own name source span");

var ranges = Bind("let value=1; [value, value + 1, ~/.codex]");
var range = ranges.Hir.Nodes.Single(item => item.Kind == HirKind.LazyRange);
Check(ranges.Hir.Ranges[range.Data].Deferred.SequenceEqual(new[] { false, true, false }),
    "Range membership preserves eager values versus deferred expressions");
var barriers = Bind("acquire; acquire null; release; release null");
Check(barriers.Hir.Nodes.Where(item => item.Kind == HirKind.Barrier).Select(item => item.Children.Count)
    .SequenceEqual(new[] { 0, 1, 0, 1 }), "A bare barrier is distinct from an explicit Null operand");
try { _ = Bind("loop { let callback = fn { break }; }"); throw new InvalidOperationException("Cross-function break was accepted"); }
catch (CompileException error) { Check(error.Diagnostic.Code == "E0207", "A closure cannot break an outer loop"); }
try { _ = Bind("loop { let values = [break]; }"); throw new InvalidOperationException("Cross-thunk break was accepted"); }
catch (CompileException error) { Check(error.Diagnostic.Code == "E0207", "A deferred Range item cannot break its caller"); }

var patterns = Bind("let (first, second) = [1, 2]; match first { -1 => { 0 }, _ => { second } }");
Check(patterns.Hir.Calls.Count(item => item.NativeName == "ranges.materialize") == 1,
    "Destructuring materializes only one selected prefix");
Check(patterns.Hir.Calls.Count(item => item.NativeName == "ranges.at") == 2,
    "Destructuring indexes the one prefix snapshot");
Check(patterns.Hir.Literals.Any(item => item is { Kind: HirLiteralKind.Number, Number: -1 }),
    "A signed match literal is stored as a constant");

var standard = Bind("fn first() { $std::ranges::size([1]) }; fn second() { $std::ranges::size([2]) }; $std::ranges::size([])");
Check(standard.StandardLocations.Keys.SequenceEqual(new[] { "ranges" }), "A direct module use does not bind every standard module");
Check(standard.StandardModules.Keys.SequenceEqual(new[] { "ranges" }), "Source binding seals only the demanded module before semantic analysis");
var source = standard.StandardLocations["ranges"];
Check(standard.Templates.Where(template => template.Name is "first" or "second").All(template => template.Captures.Any(location => Original(standard, location) == source)),
    "Functions capture the same shared module cell");

var transitive = Bind("$std::files::File::read_text");
Check(transitive.StandardLocations.Where(pair => pair.Key.Length != 0).All(pair => transitive.StandardModules.ContainsKey(pair.Key)),
    "Every transitive standard dependency is bound before semantic analysis");
Check(transitive.StandardModules.ContainsKey("io") && !transitive.StandardModules.ContainsKey("commands"),
    "Transitive source dependencies retain selected demand rather than binding the whole library");
var firstClass = Bind("let standard = $std; standard");
Check(StandardSourceCatalog.Read().Keys.Where(name => !name.StartsWith('_')).All(firstClass.StandardModules.ContainsKey),
    "A first-class standard root binds all its public module shapes");

var inline = Bind("concept Product { first?: typename First, second?: typename Second }; fn take(value: Product) { value };", true);
Check(inline.Hir.Concepts.Count == 1, "An inline associated-type concept is retained as semantic metadata");
Check(inline.Concepts[0].AssociatedTypes.Count == 2 && inline.TypeUses.Count >= 2,
    "Associated type uses have stable type identities and source spans");
var incomplete = Bind("fn unfinished(argument) { let local = scope { item: 7 }; local::", true);
Check(incomplete.Declarations.Any(item => item is { Name: "argument", Kind: DeclarationKind.Parameter }),
    "An incomplete generic body retains its parameter rather than treating it as Path");
Check(incomplete.Visibility.Any(item => item.Bindings.ContainsKey("local") && item.IncludesEnd),
    "Incomplete body visibility reaches the cursor");
Check(incomplete.MemberUses.Single().Name is null, "An incomplete member access retains a receiver site");

var written = Bind("let x=1; fn update(){x=2}; let y=3; fn ignore(y){let y=4; y=5};");
var changed = written.Declarations.First(item => item.Name == "x");
var unchanged = written.Declarations.First(item => item.Name == "y");
Check(!written.IsStable(changed.Location) && written.IsStable(unchanged.Location),
    "Actual captured writes invalidate only their declaration identity");
var open = new SourceBinder(isSession: true).Bind("let value=1; fn local(){let inner=2;inner}", "<repl>");
Check(!open.IsStable(open.Declarations.Single(item => item.Name == "value").Location)
    && open.IsStable(open.Declarations.Single(item => item.Name == "inner").Location),
    "Open session roots and closed private lifetimes have different future write domains");

var accepted = new SourceBinder(isSession: true).Bind("let value=1; fn read(){value};", "<repl>");
var retained = accepted.Hir.Freeze(accepted.Entry, isSession: true);
var next = new SourceBinder(accepted).Bind("let alias=read; value=2; alias()", "<repl>");
Check(next.Entry == accepted.Entry && next.Exports["value"] == accepted.Exports["value"],
    "A subsequent submission preserves entry and declaration identities");
Check(next.Hir.Sources.Count == 2 && next.Templates.Count == accepted.Templates.Count,
    "Only the new submission is bound; prior template bodies are retained");
Check(retained.Sources.Length == 1 && retained.Exports.Length == 2,
    "Appending to a new binding generation does not mutate the accepted immutable HIR");

Console.WriteLine($"PASS {checks} source binding contracts");
