using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.Contains("--lexical-locations", StringComparer.Ordinal))
{
    Console.WriteLine($"PASS {await LexicalLocationChecks.RunAsync()} lexical capture location contracts");
    return;
}

var cases = new (string Name, string Source, Value Expected)[]
{
    ("nested-apply", """
        fn apply(operation: Function, value: Number) { operation(value) };
        apply(fn(first: Number) {
            apply(fn(second: Number) { first + second }, 2)
        }, 3)
        """, new NumberValue(5)),
    ("three-active-specializations", """
        fn apply(operation: Function, value: Number) { operation(value) };
        apply(fn(first: Number) {
            let middle = apply(fn(second: Number) {
                apply(fn(third: Number) { first + second + third }, 3)
            }, 2);
            apply(fn(last: Number) { middle + last }, 4)
        }, 1)
        """, new NumberValue(10)),
    ("nested-result-fold", """
        fn ok(value?) {
            scope { fold: fn(on_ok: Function, on_error: Function) { on_ok(value) } }
        };
        fn err(error?) {
            scope { fold: fn(on_ok: Function, on_error: Function) { on_error(error) } }
        };
        fn fold(result: Scope, on_ok: Function, on_error: Function) {
            result::fold(on_ok, on_error)
        };
        fn zip(left: Scope, right: Scope) {
            fold(left, fn(first?) {
                fold(right, fn(second?) { ok(scope { first?: first, second?: second }) }, err)
            }, err)
        };
        let zipped = zip(ok("joined"), ok(5));
        fold(zipped, fn(pair: Scope) {
            $std::string::concat(pair::first, pair::second as String)
        }, fn(error: String) { error })
        """, new StringValue("joined5")),
    ("nested-result-error", """
        fn ok(value?) { scope { fold: fn(on_ok: Function, on_error: Function) { on_ok(value) } } };
        fn err(error?) { scope { fold: fn(on_ok: Function, on_error: Function) { on_error(error) } } };
        fn fold(value: Scope, on_ok: Function, on_error: Function) { value::fold(on_ok, on_error) };
        fn zip(left: Scope, right: Scope) {
            fold(left, fn(first?) {
                fold(right, fn(second?) { ok(scope { first?: first, second?: second }) }, err)
            }, err)
        };
        fold(zip(ok(4), err("right error")), fn(pair: Scope) { "wrong" }, fn(error: String) { error })
        """, new StringValue("right error")),
    ("recursive-fresh-scope", """
        fn count(state: Scope) {
            if state::remaining <= 0 { 0 }
            else { count(scope { remaining: state::remaining - 1 }) + 1 }
        };
        count(scope { remaining: 8 })
        """, new NumberValue(8)),
    ("recursive-callable-argument", """
        fn repeat(operation: Function, count: Number, value: Number) {
            if count <= 0 { value }
            else { repeat(operation, count - 1, operation(value)) }
        };
        repeat(fn(value: Number) { value + 2 }, 6, 1)
        """, new NumberValue(13)),
    ("recursive-captured-location", """
        let base = 3;
        fn count(remaining: Number) {
            if remaining <= 0 { base }
            else { count(remaining - 1) + 1 }
        };
        base = 4;
        count(5)
        """, new NumberValue(9)),
    ("private-nullable-result", """
        fn selected() {
            let first? = 1;
            let marker = 0;
            first
        };
        $std::string::join($std::ranges::map([1, 2], fn(value: Number) {
            (selected() + value) as String
        }), "|")
        """, new StringValue("2|3")),
    ("private-union-parameter-result", """
        fn lines(input: Path | String | Range<String>) {
            match input {
                Path => { $std::io::read_lines(input) },
                String => { $std::string::lines(input) },
                Range<String> => { input },
            }
        };
        $std::string::join($std::ranges::unique(lines(["b", "b", "a"])), "|")
        """, new StringValue("b|a")),
    ("external-nullable-result-contract", """
        let current?: Number = 1;
        fn read() { let marker = 0; current };
        let before? = read();
        current = null;
        let after? = read();
        if before is null { 0 } else { if after is null { before } else { 0 } }
        """, new NumberValue(1)),
    ("captured-parameter-result-contract", """
        fn owner(value?: Number) {
            scope {
                read: fn { let marker = 0; value },
                clear: fn { value = null; },
            }
        };
        let state = owner(1);
        let before? = state::read();
        state::clear();
        let after? = state::read();
        if before is null { 0 } else { if after is null { before } else { 0 } }
        """, new NumberValue(1)),
    ("deferred-external-live-cell", """
        let current = "before";
        fn lines() { $std::string::lines(current) };
        let pending = lines();
        current = "after
        mutation";
        $std::string::join(pending, "|")
        """, new StringValue("after|mutation")),
    ("deferred-private-live-cell", """
        fn lines(input: String) {
            let pending = $std::string::lines(input);
            input = "after
        mutation";
            pending
        };
        $std::string::join(lines("before"), "|")
        """, new StringValue("after|mutation")),
    ("returned-scope-alias", """
        fn create() { let local = scope { value: 1 }; local };
        let first = create();
        let second = first;
        second::value = 3;
        first::value
        """, new NumberValue(3)),
    ("deferred-live-union-dispatch", """
        fn lines(input: String | Range<String>) {
            match input {
                String => { $std::string::lines(input) },
                Range<String> => { input },
            }
        };
        let current: String | Range<String> = ["before"];
        let pending = lines(current);
        current = "after
        mutation";
        $std::string::join(pending, "|")
        """, new StringValue("after|mutation")),
    ("unwritten-parameter-shadowing", """
        fn reverse(values: Range<String>) { $std::ranges::reverse(values) };
        fn lines(input: String | Range<String>) {
            let writer = fn(input: Number) { input = input + 1; input };
            writer(0);
            { let input = 0; input = 1; };
            for input in [1, 2] { input = input + 1; };
            let state = scope { input: 0, set: fn { input = 1; } };
            state::set();
            match input {
                String => { $std::string::lines(input) },
                Range<String> => { reverse(input) },
            }
        };
        $std::string::join(lines(["before", "after"]), "|")
        """, new StringValue("after|before")),
    ("unwritten-nullable-parameter", """
        fn reverse(values: Range<String>) { $std::ranges::reverse(values) };
        fn lines(input?: Range<String>) {
            if input is null { ["none"] } else { reverse(input) }
        };
        $std::string::join($std::ranges::concat(lines(null), lines(["a", "b"])), "|")
        """, new StringValue("none|b|a")),
    ("captured-formal-coercion", """
        fn owner(value: String) { fn { value } };
        let read = owner(./relative-value);
        $std::ranges::first($std::ranges::map([0], fn(index: Number) { read() }))
        """, new StringValue("./relative-value")),
};
var rejected = new (string Name, string Source)[]
{
    ("deferred-union-identity", """
        fn identity(value: String | Range<String>) { value };
        let current: String | Range<String> = ["before"];
        let pending = identity(current);
        current = "after";
        $std::string::join(pending, "|")
        """),
    ("deferred-written-parameter", """
        fn reverse(value: Range<String>) { $std::ranges::reverse(value) };
        fn lines(input: String | Range<String>) {
            let pending = reverse(input);
            input = "after";
            pending
        };
        $std::string::join(lines(["before"]), "|")
        """),
    ("deferred-global-writer", """
        fn reverse(value: Range<String>) { $std::ranges::reverse(value) };
        fn lines(input: String | Range<String>) {
            let write = fn(index: Number) { global input; input = "after"; };
            let pending = reverse(input);
            write(0);
            pending
        };
        $std::string::join(lines(["before"]), "|")
        """),
    ("deferred-mutable-scope-payload", """
        fn reverse(value: Range<String>) { $std::ranges::reverse(value) };
        fn lines(state: Scope) {
            if state::value is null {
                ["none"]
            } else {
                let pending = reverse(state::value);
                state::value = null;
                pending
            }
        };
        $std::string::join(lines(scope { value?: ["before"] }), "|")
        """),
};
int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
    foreach (var test in cases)
    {
        Module source = Compiler.Compile(test.Source, test.Name + ".msync", level);
        Module relocatable = RoundTrip(source);
        Module executable = RoundTrip(Binding.Bind(relocatable,
            new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
        foreach (Module image in new[] { source, relocatable, executable })
        {
            Value actual = await Interpreter.ExecuteAsync(image);
            if (actual != test.Expected)
                throw new InvalidOperationException($"{test.Name} O{level}: expected {test.Expected}, got {actual}.");
            checks++;
        }
    }
foreach (var test in cases)
{
    string source = "let checked = {\n" + test.Source + "\n}; checked;";
    EditorAnalysis analysis = EditorCompiler.Analyze(source, test.Name + "-editing.msync");
    if (analysis.Diagnostic is { } error) throw new InvalidOperationException(test.Name + ": " + error.Message);
    SemanticMember? result = analysis.Resolve(source.LastIndexOf("checked", StringComparison.Ordinal), "checked");
    if (result?.Type != test.Expected.Type.ToString())
        throw new InvalidOperationException($"{test.Name}: editor inferred {result?.Type} instead of {test.Expected.Type}.");
    checks++;
}
foreach (int level in new[] { 0, 2, 3 })
    foreach (var test in rejected)
    {
        try { Compiler.Compile(test.Source, test.Name + ".msync", level); }
        catch (CompileException error) when (error.Diagnostic.Code == "E0203") { checks++; continue; }
        throw new InvalidOperationException(test.Name + " accepted an unsafe deferred type refinement.");
    }
Console.WriteLine($"PASS {checks} higher-order, recursive and editor specialization contracts");

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}
