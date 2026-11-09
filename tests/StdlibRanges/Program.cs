using System.Runtime.CompilerServices;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task<string> Snapshot(Value value)
{
    if (value is RangeValue range)
    {
        List<string> items = [];
        await foreach (Value item in range.Items) items.Add(await Snapshot(item));
        return "[" + string.Join(',', items) + "]";
    }
    if (value is ScopeValue scope)
    {
        List<string> members = [];
        foreach ((string name, Value member) in scope.Members) members.Add(name + ":" + await Snapshot(member));
        return "{" + string.Join(',', members) + "}";
    }
    return value.ToString() ?? "null";
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "stdlib-ranges-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--nullable"))
    {
        foreach ((string name, string source, string expected) in new[]
        {
            ("single-null", "$std::ranges::single(null)", "[null]"),
            ("contains-null", "$std::ranges::contains([null, 1], null)", "true"),
            ("contains-missing-null", "$std::ranges::contains([1, 2], null)", "false"),
            ("find-null", "$std::ranges::find([1, null], null)", "null"),
            ("front-null", "$std::ranges::front([null, 1])", "null"),
            ("back-null", "$std::ranges::back([1, null])", "null"),
            ("first-null", "$std::ranges::first([null])", "null"),
            ("last-null", "$std::ranges::last([null])", "null"),
            ("find-if-null", "$std::ranges::find_if([1, null], fn(value?) { value is null })", "null"),
            ("single-type", "$std::ranges::single(Path)", "[Path]"),
            ("contains-type", "$std::ranges::contains([Path, String], Path)", "true"),
            ("find-type", "$std::ranges::find([Path, String], String)", "String"),
            ("front-type", "$std::ranges::front([Path])", "Path"),
            ("back-type", "$std::ranges::back([Path, String])", "String"),
            ("first-type", "$std::ranges::first([Path])", "Path"),
            ("last-type", "$std::ranges::last([Path, String])", "String"),
            ("find-if-type", "$std::ranges::find_if([Path, String], fn(value) { true })", "Path")
        })
        {
            foreach (int level in new[] { 0, 2, 3 })
            {
                Module portable = Compiler.Compile(source, name, level);
                foreach (Module module in new[] { portable, Binding.Bind(portable, new(root, root)) })
                {
                    using MemoryStream bytes = new();
                    Artifact.Write(module, bytes);
                    bytes.Position = 0;
                    string actual = await Snapshot(await Interpreter.ExecuteAsync(Artifact.Read(bytes), new(WorkingDirectory: root)));
                    Check(actual == expected, name + $" O{level}: expected {expected}, got {actual}");
                }
            }
        }
        Console.WriteLine($"PASS: {assertions} public nullable and Type Range contracts across serialized portable/executable stages");
        return;
    }

    foreach ((string name, string source, string expected) in new[]
    {
        ("concat", "$std::ranges::concat([1, 2], [3])", "[1,2,3]"),
        ("flatten", "$std::ranges::flatten([[1, 2], [], [3]])", "[1,2,3]"),
        ("zip", "$std::ranges::zip([1, 2, 3], [\"a\", \"b\"])", "[{first:1,second:a},{first:2,second:b}]"),
        ("enumerate", "$std::ranges::enumerate([\"a\", \"b\"])", "[{index:0,value:a},{index:1,value:b}]"),
        ("chunk", "$std::ranges::chunk([1, 2, 3, 4, 5], 2)", "[[1,2],[3,4],[5]]"),
        ("slide", "$std::ranges::slide([1, 2, 3, 4], 3)", "[[1,2,3],[2,3,4]]"),
        ("oversized-slide", "$std::ranges::slide([1, 2], 2147483648)", "[]"),
        ("oversized-chunk", "$std::ranges::chunk([1, 2], 2147483648)", "[[1,2]]"),
        ("sequence", "$std::ranges::sequence(3, 0, -1)", "[3,2,1]"),
        ("fractional-sequence", "$std::ranges::sequence(0, 1, $std::cast(\"0.25\", Number))", "[0,0.25,0.5,0.75]"),
        ("repeat", "$std::ranges::repeat(null, 3)", "[null,null,null]"),
        ("sum", "$std::ranges::sum([1, 2, 3, 4])", "10"),
        ("fold", "$std::ranges::fold([1, 2, 3], 10, fn(accumulator, value) { accumulator + value })", "16"),
        ("nullable-fold", "$std::ranges::fold([1, 2, 3], null, fn(accumulator?, value) { if accumulator is null { value } else { accumulator + value } })", "6"),
        ("size", "$std::ranges::size([1, 2, 3])", "3"),
        ("count", "$std::ranges::count([1, 2, 1], 1)", "2"),
        ("count-null", "$std::ranges::count([null, 1, null], null)", "2"),
        ("find-or", "$std::ranges::find_or([1, 2, 3], 0, fn(value) { value > 1 })", "2"),
        ("missing-or", "$std::ranges::find_or([1, 2, 3], null, fn(value) { value > 4 })", "null"),
        ("at", "$std::ranges::at([1, 2, 3], 1)", "2"),
        ("at-or", "$std::ranges::at_or([1, 2], 2, null)", "null"),
        ("index", "$std::ranges::index_of([4, 5, 6], 5)", "1"),
        ("index-missing", "$std::ranges::index_of([4, 5, 6], 7)", "null"),
        ("sort", "$std::ranges::sort([4, 2, 3, 1])", "[1,2,3,4]"),
        ("sort-with", "$std::ranges::sort_with([1, 3, 2], fn(left, right) { left > right })", "[3,2,1]"),
        ("scan", "$std::ranges::scan([1, 2, 3], 0, fn(accumulator, value) { accumulator + value })", "[1,3,6]"),
        ("scan-repeat", "let values = $std::ranges::scan([1, 2, 3], 0, fn(accumulator, value) { accumulator + value }); [$std::ranges::last(values), $std::ranges::last(values)]", "[6,6]"),
        ("generate", "$std::ranges::generate(3, fn(index) { index * 2 })", "[0,2,4]"),
        ("min", "$std::ranges::min([4, 2, 3])", "2"),
        ("max", "$std::ranges::max([4, 2, 3])", "4"),
        ("equal", "$std::ranges::equal([1, 2], [1, 2])", "true"),
        ("unequal", "$std::ranges::equal([1, 2], [1, 2, 3])", "false"),
        ("prefix", "$std::ranges::starts_with([1, 2, 3], [1, 2])", "true"),
        ("short-prefix", "$std::ranges::starts_with([1], [1, 2])", "false"),
        ("suffix", "$std::ranges::ends_with([1, 2, 3], [2, 3])", "true"),
        ("lexical", "$std::ranges::lexicographical_compare([1, 2], [1, 3])", "true"),
        ("sort-scope", "let first = scope { rank: 1, name: \"first\" }; let second = scope { rank: 1, name: \"second\" }; let ordered = $std::ranges::sort_with([second, first], fn(left, right) { left::rank < right::rank }); [$std::ranges::first(ordered)::name, $std::ranges::last(ordered)::name]", "[second,first]"),
        ("scope-identity", "let first = scope { name: \"same\" }; let second = scope { name: \"same\" }; $std::ranges::count([first, second], first)", "1"),
        ("repeat-alias", "let object = scope { name: \"before\" }; let values = $std::ranges::repeat(object, 2); $std::ranges::first(values)::name = \"after\"; $std::ranges::last(values)::name", "after"),
        ("lazy", "let count = 0; let values = $std::ranges::generate(3, fn(index) { count = count + 1; index }); let before = count; let zero = $std::ranges::size($std::ranges::take(values, 0)); let after_zero = count; let length = $std::ranges::size(values); [before, zero, after_zero, length, count]", "[0,0,0,3,3]"),
        ("cached", "let count = 0; let values = $std::ranges::cache($std::ranges::generate(3, fn(index) { count = count + 1; index })); let first = $std::ranges::size(values); let second = $std::ranges::size(values); [first, second, count]", "[3,3,3]"),
        ("live-cell", "let object = scope { name: \"before\" }; let values = $std::ranges::materialize([object]); $std::ranges::size(values); object::name = \"after\"; $std::ranges::first(values)::name", "after")
    })
    {
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module portable = Compiler.Compile(source, name, level);
            Module executable = Binding.Bind(portable, new(root, root));
            foreach (Module module in new[] { portable, executable })
            {
                Value result = await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root));
                Check(await Snapshot(result) == expected, name + $" O{level}: expected {expected}, got {await Snapshot(result)}");
                using MemoryStream bytes = new();
                Artifact.Write(module, bytes); bytes.Position = 0;
                Check(await Snapshot(await Interpreter.ExecuteAsync(Artifact.Read(bytes), new(WorkingDirectory: root))) == expected,
                    name + $" O{level} binary round-trip changed the result");
            }
        }
    }

    Directory.CreateDirectory(Path.Combine(root, "input"));
    await File.WriteAllBytesAsync(Path.Combine(root, "input", "a"), [0, 255, 13, 10, 42]);
    await File.WriteAllBytesAsync(Path.Combine(root, "input", "b"), [9, 8, 7]);
    const string copy = "let paths = $std::ranges::filter([./input/a, ./input/b], fn(path) { $std::fs::filename(path) == \"a\" }); $std::ranges::flatten($std::ranges::chunk(paths, 1)) -> ./out;";
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module portable = Compiler.Compile(copy, "range-copy", level);
        foreach (Module candidate in new[] { portable, Binding.Bind(portable, new(root, root)) })
        {
            string output = Path.Combine(root, "out");
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.CreateDirectory(output);
            using MemoryStream bytes = new(); Artifact.Write(candidate, bytes); bytes.Position = 0;
            await Interpreter.ExecuteAsync(Artifact.Read(bytes), new(WorkingDirectory: root));
            Check((await File.ReadAllBytesAsync(Path.Combine(output, "a"))).AsSpan().SequenceEqual(new byte[] { 0, 255, 13, 10, 42 }),
                "Composed Range transfer changed actual file bytes");
            Check(!File.Exists(Path.Combine(output, "b")), "Filtered Range unexpectedly transferred an unselected file");
        }
    }

    ValueTask<Value> NoInvoke(Value value, IReadOnlyList<Value> arguments, CancellationToken token) =>
        throw new InvalidOperationException("Unexpected callback");
    RuntimeContext context = new(new(WorkingDirectory: root));
    RangeValue single = new([new NumberValue(1)], TypeRef.Number);
    foreach ((string operation, Value[] arguments) in new[]
    {
        ("chunk", new Value[] { single, new NumberValue(0) }),
        ("slide", new Value[] { single, new NumberValue(1.5) }),
        ("repeat", new Value[] { Value.Null, new NumberValue(-1) }),
        ("sequence", new Value[] { new NumberValue(0), new NumberValue(2), new NumberValue(0) })
    })
    {
        try { await NativeRegistry.InvokeAsync("ranges." + operation, arguments, context, NoInvoke); throw new InvalidOperationException(operation + " accepted invalid count/step"); }
        catch (MsyncException error) { Check(error.Message.Contains("ranges." + operation, StringComparison.Ordinal), "Range error lost its source operation"); }
    }
    using CancellationTokenSource cancellation = new();
    cancellation.Cancel();
    RangeValue sequence = (RangeValue)await NativeRegistry.InvokeAsync("ranges.sequence", [new NumberValue(0), new NumberValue(10), new NumberValue(1)], context, NoInvoke);
    try { await foreach (Value value in sequence.Items.WithCancellation(cancellation.Token)) { } throw new InvalidOperationException("Cancelled Range continued"); }
    catch (OperationCanceledException) { assertions++; }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"PASS: {assertions} typed Range source, bytecode, lifetime, laziness, count and cancellation contracts");
