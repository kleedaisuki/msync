using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}
foreach (int level in new[] { 0, 2, 3 })
{
    foreach ((string name, string source, string expected) in new[]
    {
        ("named-identity", "fn factory(value: String) { scope Saved { item: value }; Saved }; let first = factory(\"first\"); first::item = \"changed\"; let second = factory(\"second\"); second::item", "changed"),
        ("named-expression", "fn factory(value: String) { let object = scope Shared { item: value }; object }; let first = factory(\"first\"); first::item = \"changed\"; factory(\"ignored\")::item", "changed"),
        ("anonymous-fresh", "fn factory(value: String) { scope { item: value } }; let first = factory(\"first\"); first::item = \"changed\"; factory(\"second\")::item", "second"),
        ("initializer-effects", "let count = 0; fn factory() { scope Saved { item: { count = count + 1; \"value\" } }; Saved }; factory(); factory(); if count == 1 { \"once\" } else { \"repeated\" }", "once"),
        ("captured-cell", "fn factory(value: String) { scope Saved { item: value, read: fn { item }, write: fn(value: String) { item = value; } }; Saved }; let first = factory(\"first\"); let second = factory(\"second\"); second::write(\"shared\"); first::read()", "shared"),
        ("recursive-initializer", "fn factory(depth: Number) { scope Saved { item: if depth == 0 { \"inner\" } else { factory(depth - 1); \"outer\" } }; Saved }; let first = factory(1); first::item", "inner"),
        ("initializer-flow-facts", "let flag = false; fn factory() { scope Saved { item: { flag = true; \"value\" } }; Saved }; factory(); flag = false; factory(); if flag { \"stale\" } else { \"skipped\" }", "skipped"),
        ("specialized-capture", "fn factory(value: Scope) { scope Saved { read: fn { value::item } }; Saved }; if false { factory(scope { item: \"unselected\" }); }; factory(scope { item: \"selected\" })::read()", "selected"),
        ("selected-first", "fn factory(value: String) { scope Saved { item: value }; Saved }; if false { factory(\"unselected\"); }; factory(\"selected\")::item", "selected"),
        ("self-forward-member", "scope family { make: fn(value:String) { family::later(value) }, later: fn(value:String) { scope {item:value} } }; family::make(\"forward\")::item", "forward"),
        ("self-expression", "let value = scope Family { make: fn { Family::later() }, later: fn { \"expression\" } }; value::make()", "expression"),
        ("self-shared-mutation", "scope Family { item: \"before\", read: fn { Family::item } }; let alias = Family; alias::item = \"after\"; Family::read()", "after"),
        ("self-binding-mutation", "scope Family { read: fn { Family::item }, item: \"before\" }; let original = Family; Family = scope {item: \"replacement\"}; original::read()", "replacement"),
        ("self-recursive-operation", "scope Family { depth: fn(value:Number)->Number { if value == 0 { 0 } else { Family::depth(value - 1) + 1 } } }; if Family::depth(4) == 4 { \"recursive\" } else { \"wrong\" }", "recursive"),
        ("self-singleton-first-capture", "fn factory(value:String) { scope Saved { read: fn { Saved::item }, item:value }; Saved }; factory(\"first\"); factory(\"ignored\")::read()", "first"),
    })
    {
        Module raw = Compiler.Compile(source, name, level);
        foreach (Module module in new[] { raw, RoundTrip(raw), RoundTrip(Binding.Bind(raw, new(Environment.CurrentDirectory, Environment.CurrentDirectory))) })
        {
            try { Check(await Interpreter.ExecuteAsync(module) is StringValue value && value.Data == expected, $"{name} O{level} {module.Stage}"); }
            catch (Exception error) { throw new InvalidOperationException($"{name} O{level} {module.Stage}", error); }
        }
    }
    CompilerSession compiler = new(optimization: level);
    ExecutionSession session = new();
    async Task<Value> Submit(string source)
    {
        var module = compiler.Compile(source);
        Value result = await session.ExecuteAsync(module);
        compiler.AcceptSubmission(session.GetLocalValue);
        return result;
    }
    await Submit("fn factory(value: String) { scope Saved { item: value }; Saved }; let object = factory(\"before\");");
    await Submit("object::item = \"after\";");
    Check(await Submit("factory(\"ignored\")::item") is StringValue { Data: "after" }, $"REPL singleton persisted O{level}");
    await Submit("scope Other { item: \"different\" };");
    Check(await Submit("Other::item") is StringValue { Data: "different" }, $"REPL declaration identity O{level}");
    await Submit("scope SelfFamily { read: fn { SelfFamily::item }, item: \"before\" };");
    await Submit("SelfFamily::item = \"after\";");
    Check(await Submit("SelfFamily::read()") is StringValue { Data: "after" }, $"REPL self capture O{level}");
    try
    {
        _ = Compiler.Compile("fn factory(value: Scope) { scope Saved { item: value }; Saved }; factory(scope { field: \"text\" }); factory(scope { field: 1 })", "fixed-singleton-schema", level);
        throw new InvalidOperationException("Named Scope changed its nested field schema across specializations");
    }
    catch (CompileException error)
    {
        Check(error.Message.Contains("storage schema", StringComparison.Ordinal), $"Fixed singleton schema diagnostic O{level}");
    }
    foreach (string pending in new[]
    {
        "scope Pending { item: Pending };",
        "scope Pending { read: fn { Pending::value }, value: 1, item: Pending::read() };"
    })
    {
        try
        {
            _ = Compiler.Compile(pending, "pending-self", level);
            throw new InvalidOperationException("A Scope read its unpublished self location");
        }
        catch (CompileException error)
        {
            Check(error.Diagnostic.Code == "E0211" && error.Message.Contains("still being initialized", StringComparison.Ordinal),
                $"Uninitialized self diagnostic O{level}");
        }
    }
    ExecutionSession independent = new();
    Module independentModule = Compiler.Compile("scope Other { item: \"independent\" }; Other::item", "isolated", level);
    Check(await independent.ExecuteAsync(independentModule) is StringValue { Data: "independent" }, $"Session isolation O{level}");
}
Console.WriteLine($"PASS: {assertions} named singleton/anonymous freshness/source/mso/mexe/session contracts");
