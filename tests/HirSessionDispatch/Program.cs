using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    CompilerSession compiler = new(optimization: level);
    await using ExecutionSession execution = new();
    async Task<Value> Submit(string text)
    {
        Module module = compiler.Compile(text);
        if (args.Contains("--trace", StringComparer.Ordinal))
        {
            Console.WriteLine($"O{level} submission: {text}");
            foreach (var dispatch in module.SessionDispatches ?? [])
                Console.WriteLine($"  signature {dispatch.Slot}: {string.Join(", ", dispatch.Targets.Select(pair => $"{pair.Key}->{pair.Value}"))}");
        }
        try
        {
            Value value = await execution.ExecuteAsync(module);
            compiler.AcceptSubmission(execution.GetLocalValue);
            return value;
        }
        catch
        {
            compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue);
            throw;
        }
    }

    if (args.Contains("--factories-only", StringComparer.Ordinal)) goto Factories;
    await Submit("let op = fn { [1] }; let pending = op();");
    await Submit("op = fn { [2] };");
    Require(await Submit("$std::ranges::first(pending)") is NumberValue { Data: 2 },
        $"O{level}: a prior deferred call reads a future definition");
    if (args.Contains("--basic", StringComparer.Ordinal)) continue;

    await Submit("let captured = scope { value: 17 }; op = fn { [captured::value] };");
    await Submit("let captured_pending = op(); captured::value = 23;");
    Require(await Submit("$std::ranges::first(captured_pending)") is NumberValue { Data: 23 },
        $"O{level}: future definitions retain their actual captured locations");

    await Submit("let identity = fn(value) { [value] }; let numbers = identity(7); let texts = identity(\"seven\");");
    await Submit("identity = fn(value) { [value] };");
    Require(await Submit("$std::ranges::first(numbers)") is NumberValue { Data: 7 },
        $"O{level}: one future callable has a Number specialization");
    Require(await Submit("$std::ranges::first(texts)") is StringValue { Data: "seven" },
        $"O{level}: a second signature does not reuse the Number specialization");

    await Submit("let holder = scope { operation: fn { [1] } }; let alias = holder; let from_field = holder::operation();");
    await Submit("alias::operation = fn { [4] };");
    Require(await Submit("$std::ranges::first(from_field)") is NumberValue { Data: 4 },
        $"O{level}: member aliases update an existing deferred call");

    bool rejected = false;
    try { _ = compiler.Compile("op = fn { true };"); }
    catch (CompileException) { rejected = true; }
    Require(rejected, $"O{level}: an incompatible future result is rejected before execution");
    Require(await Submit("$std::ranges::first(pending)") is NumberValue { Data: 2 },
        $"O{level}: failed compilation does not publish a registry generation");
    if (args.Contains("--before-effects", StringComparer.Ordinal)) continue;

    await Submit("let state = scope { changed: false }; let update = fn { [1] }; let work = update();");
    await Submit("update = fn { state::changed = true; [2] };");
    Require(await Submit("$std::ranges::first(work); if state::changed { 7 } else { 9 }") is NumberValue { Data: 7 },
        $"O{level}: consumption observes the new target's memory effects");

    await Submit("let mapper = fn(value) { value + 1 }; let transformed = $std::ranges::transform([2], mapper);");
    await Submit("mapper = fn(value) { value + 10 };");
    Require(await Submit("$std::ranges::first(transformed)") is NumberValue { Data: 12 },
        $"O{level}: native callbacks adapt through the current signature table");
    await Submit("let loose = fn { [1] };");
    try { await Submit("let broken = \"bad\" as Number; let ghost = loose();"); }
    catch (MsyncException) { }
    Require(!compiler.BindingNames.Contains("broken") && !compiler.BindingNames.Contains("ghost"),
        $"O{level}: a runtime failure does not export uninitialized bindings");
    bool partialContract = false;
    try { _ = compiler.Compile("loose = fn { true };"); }
    catch (CompileException) { partialContract = true; }
    Require(partialContract, $"O{level}: static signature obligations survive a runtime failure");
    if (args.Contains("--before-factories", StringComparer.Ordinal)) continue;

Factories:
    await Submit("""
        fn Factory(seed: Number) {
            let item = scope { operation: fn { [seed] } };
            scope { item: item, values: item::operation() }
        };
        let first = Factory(1);
        let second = Factory(9);
        """);
    await Submit("first::item::operation = fn { [3] };");
    Require(await Submit("$std::ranges::first(first::values)") is NumberValue { Data: 3 },
        $"O{level}: a shared factory projects a field signature onto its first object");
    Require(await Submit("$std::ranges::first(second::values)") is NumberValue { Data: 9 },
        $"O{level}: code sharing does not replace the other factory object's callable");

    await Submit("""
        fn PrivateFactory() {
            let operation = fn { [1] };
            scope {
                values: operation(),
                replace: fn(value) { operation = value; },
            }
        };
        let private = PrivateFactory();
        """);
    await Submit("private::replace(fn { [5] });");
    Require(await Submit("$std::ranges::first(private::values)") is NumberValue { Data: 5 },
        $"O{level}: captured setters carry obligations to the actual private cell");
    await Submit("let replacement = scope { operation: fn { [1] } }; let relocated = replacement::operation();");
    await Submit("replacement = scope { operation: fn { [8] } };");
    Require(await Submit("$std::ranges::first(relocated)") is NumberValue { Data: 8 },
        $"O{level}: a deferred member path follows replacement of its root location");
    await Submit("let hidden = fn { [4] }; let before_shadow = hidden();");
    await Submit("let hidden = fn { [9] };");
    Require(await Submit("$std::ranges::first(before_shadow)") is NumberValue { Data: 4 },
        $"O{level}: shadowing creates another cell instead of retargeting the old promise");
}
Console.WriteLine($"PASS {checks} open-session source journeys");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
