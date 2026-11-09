using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;

int checks = 0;
foreach (var test in new (string Name, string Source, double Expected, string Function, int Instances)[]
{
    ("scalar-code-sharing", "fn add(x) { x + 1 }; add(2); add(7)", 8, "add", 1),
    ("bool-code-sharing", "fn choose(x) { if x { 3 } else { 8 } }; choose(true); choose(false)", 8, "choose", 1),
    ("scope-result-projection", "fn wrap(x) { scope { value: x } }; let a = scope { n: 1 }; let b = scope { n: 9 }; let first = wrap(a); let second = wrap(b); second::value::n", 9, "wrap", 1),
    ("captured-location", "let n = 1; let get = fn { n }; get(); n = 9; get()", 9, "<anonymous>", -1),
    ("alias-sensitive-flow", "fn choose(a, b) { a::item = null; if b::item? { 0 } else { b::item::n } }; let a = scope { item?: scope { n: 1 } }; let b = scope { item?: scope { n: 2 } }; choose(a, b); let c = scope { item?: scope { n: 3 } }; choose(c, c)", 0, "choose", 2),
    ("nullable-field-effects", "fn clear(x) { x::item = null }; let a = scope { item?: scope { n: 1 } }; clear(a); if a::item? { 7 } else { a::item::n }", 7, "clear", 1),
    ("nullable-capture-effects", "let value? = scope { n: 1 }; fn clear() { value = null }; clear(); if value? { 7 } else { value::n }", 7, "clear", 1),
    ("fresh-field-capture-identity", "fn make() { scope { value?: 1, get: fn { value } } }; let object = make(); object::value = null; if object::get()? { 7 } else { 9 }", 7, "make", 1),
    ("fresh-field-capture-separation", "fn make() { scope { value?: 1, get: fn { value } } }; let first = make(); let second = make(); first::value = null; if second::get()? { 9 } else { 7 }", 7, "make", 1),
    ("scalar-origin-projection", "fn identity(x) { x }; identity(false); if identity(true) { 7 } else { null::missing }", 7, "identity", 1),
    ("field-origin-projection", "fn get(x) { x::flag }; let first = scope { flag: false }; let second = scope { flag: true }; get(first); if get(second) { 7 } else { null::missing }", 7, "get", 1),
    ("named-singleton-result", "fn make() { scope State { value?: 1 }; State }; let a = make(); a::value = null; let b = make(); if b::value? { 7 } else { 9 }", 7, "make", 1),
    ("named-singleton-body-write", "fn make() { scope State { value?: 1 }; State::value = null; State }; let a = make(); a::value = 2; let b = make(); if b::value? { 7 } else { 9 }", 7, "make", 1),
    ("indexed-view-effect-projection", "native fn buffer_new(initial: Range) -> Range; native fn buffer_get(items: Range, index: Number) -> Number; fn paint(x) { x::red = true }; let first = scope { red: false }; let second = scope { red: false }; let items = buffer_new([first, second]); let a = buffer_get(items, 0); let b = buffer_get(items, 1); paint(a); if b::red { 9 } else { 7 }", 7, "paint", 1),
    ("indexed-return-origin-alias", "native fn buffer_new(initial: Range) -> Range; native fn buffer_get(items: Range, index: Number) -> Number; fn obtain(items) { buffer_get(items, 0) }; let first = scope { red: false }; let items = buffer_new([first]); let a = obtain(items); a::red = true; if first::red { 7 } else { 9 }", 7, "obtain", 1),
    ("indexed-return-shared-alias", "native fn buffer_new(initial: Range) -> Range; native fn buffer_get(items: Range, index: Number) -> Number; fn obtain(items) { scope { a: buffer_get(items, 0), b: buffer_get(items, 0) } }; let first = scope { red: false }; let items = buffer_new([first]); let views = obtain(items); views::a::red = true; views::b::red = false; if views::a::red { 9 } else { 7 }", 7, "obtain", 1),
    ("returned-lazy-no-admission", "native fn transform(values: Range, operation: Function) -> Range; let state = scope { changed: false }; fn build() { transform([1], fn(v) { state::changed = true; v }) }; let pending = build(); if state::changed { 9 } else { 7 }", 7, "build", 1),
    ("returned-lazy-effects", "native fn transform(values: Range, operation: Function) -> Range; native fn materialize(values: Range) -> Range; let state = scope { changed: false }; fn build() { transform([1], fn(v) { state::changed = true; v }) }; materialize(build()); if state::changed { 7 } else { 9 }", 7, "build", 1),
    ("returned-lazy-cell-effects", "native fn transform(values: Range, operation: Function) -> Range; native fn materialize(values: Range) -> Range; let state? = scope { n: 1 }; fn build() { transform([1], fn(v) { state = null; v }) }; materialize(build()); if state? { 7 } else { 9 }", 7, "build", 1),
    ("scope-callback-domains", "fn use(operations) { operations::get() }; let first = scope { get: fn { 1 } }; let second = scope { get: fn { 2 } }; use(first) * 10 + use(second)", 12, "use", 2),
    ("late-null-carrier-getter", "native fn first(values: Range) -> Function; fn values() { let value? = null; let pending = [fn { value }]; value = 42; pending }; let get = first(values()); let value? = get(); if value? { 0 } else { value }", 42, "values", 1),
    ("returned-cell-storage-domain", "fn make() { let value? = null; value = 42; scope { get: fn { value }, clear: fn { value = null } } }; let object = make(); object::clear(); if object::get()? { 7 } else { 9 }", 7, "make", 1),
    ("fresh-item-admission", "native fn buffer_new(initial: Range) -> Range; native fn materialize(values: Range) -> Range; let state = scope { changed: false }; let pending = [{ state::changed = true; scope { children: buffer_new([1]) } }]; let before = if state::changed { 100 } else { 0 }; materialize(pending); if state::changed { before + 7 } else { 9 }", 7, "unused", 0),
    ("nested-range-no-admission", "native fn transform(values: Range, operation: Function) -> Range; native fn materialize(values: Range) -> Range; let state = scope { changed: false }; let inner = transform([1], fn(v) { state::changed = true; v }); let outer = [inner]; materialize(outer); if state::changed { 9 } else { 7 }", 7, "unused", 0),
    ("scope-write-projection", "fn set(x) { x::n = 7 }; let a = scope { n: 1 }; let b = scope { n: 2 }; set(a); set(b); b::n", 7, "set", 1),
    ("returned-closure-projection", "fn make(x) { fn { x } }; let first = make(2); let second = make(9); first(); second()", 9, "make", 1),
    ("scope-recursive-signature", "scope Math { count: fn(x) { if x == 0 { 0 } else { Math::count(x - 1) } } }; Math::count(3)", 0, "<lambda>", 1),
    ("recursive-inferred-signature", "fn count(x) { if x == 0 { 0 } else { count(x - 1) } }; count(3)", 0, "count", 1),
    ("query-does-not-publish", "fn add(x) { x + 1 }; let t = decltype(add(2)); 3", 3, "add", 0),
    ("recursive-query-promise", "fn count(x: Number) -> Number { if x == 0 { 0 } else { count(x - 1) } }; let t = decltype(count(2)); count(2)", 0, "count", 1)
}.Where(test => args.Length == 0 || args.Contains(test.Name, StringComparer.Ordinal)))
{
    HirModule module = Compiler.Analyze(test.Source, test.Name + ".msync");
    using MemoryStream artifact = new();
    HirArtifact.Write(module, artifact);
    artifact.Position = 0;
    module = HirArtifact.Read(artifact);
    int count = module.Functions.Count(function => function.Name == test.Function);
    Require(test.Instances < 0 || count == test.Instances, $"{test.Name}: {count} instances, expected {test.Instances}");
    Value value = await Interpreter.ExecuteAsync(HirLowerer.Lower(module).Module);
    Require(value is NumberValue number && number.Data == test.Expected, $"{test.Name}: {value}, expected {test.Expected}");
}
foreach (var (source, expected) in new[]
{
    ("fn text(value: String) -> String { value }; text(./sample)", "./sample"),
    ("fn make(value: String) { fn { value } }; let result = make(./sample); result()", "./sample")
})
{
    var module = Compiler.Analyze(source, "coercion.msync");
    var value = await Interpreter.ExecuteAsync(HirLowerer.Lower(module).Module);
    Require(value is StringValue text && text.Data == expected, $"Implicit parameter coercion returned {value}");
}
var nullableCarrier = Compiler.Analyze("let value? = null; value = 42; value = null; value", "nullable-carrier.msync");
Require((await Interpreter.ExecuteAsync(HirLowerer.Lower(nullableCarrier).Module)).Type == TypeRef.Null,
    "Finalizing the Number carrier removed explicit Null admission");
bool fixedCarrier = false;
try { Compiler.Analyze("let value? = null; value = 42; value = \"wrong\"; value", "fixed-carrier.msync"); }
catch (CompileException error) { fixedCarrier = error.Diagnostic.Code == "E0203"; }
Require(fixedCarrier, "Finalizing a nullable Number carrier allowed a later String assignment");
Console.WriteLine($"PASS {checks} generic instance/provenance source journeys");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
