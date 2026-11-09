using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

const string declaration = "native fn cast(value?, target: Type) -> Bool | String | Path | Range | Function | Scope | Process | Connection | Type | Number | Null;\n";
string root = Path.Combine(Environment.CurrentDirectory, ".temp", "stdlib-cast-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
string Snapshot(Value value) => value switch
{
    NumberValue number => "Number:" + BitConverter.DoubleToInt64Bits(number.Data).ToString("x16", CultureInfo.InvariantCulture),
    ScopeValue scope => "Scope:" + string.Join(',', scope.Members.Select(item => item.Key + ":" + Snapshot(item.Value))),
    _ => value.Type + ":" + value
};
Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes); bytes.Position = 0;
    return Artifact.Read(bytes);
}
try
{
    await File.WriteAllTextAsync(Path.Combine(root, "flag"), "present");
    foreach ((string source, string expected) in new[]
    {
        ("cast(42, String)", "String:42"),
        ("cast(true, String)", "String:true"),
        ("cast(0, Bool)", "Bool:false"),
        ("cast(- 1, Bool)", "Bool:true"),
        ("cast(\" false \", Bool)", "Bool:false"),
        ("cast(\"1.25e2\", Number)", "Number:405f400000000000"),
        ("cast(\"-0\", Number)", "Number:8000000000000000"),
        ("cast(false, Number)", "Number:0000000000000000"),
        ("cast(null, Null)", "Null:null"),
        ("cast(\"ssh://forge\", Connection)", "Connection:ssh://forge"),
        ("cast(ssh://forge, String)", "String:ssh://forge"),
        ("let item = scope { value: \"before\" }; let alias = cast(item, Scope); alias::value = \"after\"; item::value", "String:after"),
        ("let callback = fn { \"captured\" }; cast(callback, Function)()", "String:captured"),
        ("let nullable?: Path = null; cast(null, decltype(nullable))", "Null:null"),
        ("let parsed = cast(\"12\", Number); if decltype(parsed) is Number { \"typed\" } else { \"wrong\" }", "String:typed"),
        ("let alias = cast(scope { item: \"field\" }, Scope); alias::item", "String:field"),
        ("fn convert(value?, target: Type) { cast(value, target) }; let first = convert(\"12\", Number); let second = convert(\"12\", String); if decltype(first) is Number and decltype(second) is String { second } else { \"wrong\" }", "String:12"),
        ("$std::cast(\"root\", String)", "String:root"),
        ("let target = Number; let values = $std::ranges::transform([\"12\"], fn(value) { cast(value, target) }); target = Number; $std::ranges::first(values)", "Number:4028000000000000"),
        ("let target = Number; let copy = target; let values = $std::ranges::transform([\"12\"], fn(value) { cast(value, copy) }); target = String; $std::ranges::first(values)", "Number:4028000000000000"),
        ("let target = if exists ./flag { Number } else { String }; if target is Number { \"number\" } else { \"string\" }", "String:number")
    })
    {
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = SourceCompiler.Compile(declaration + source, "cast-contract", level);
            foreach (Module image in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module, new(root, root))) })
                Check(Snapshot(await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root))) == expected, $"O{level}: {source}");
        }
    }
    foreach (string source in new[] { "cast(null, String)", "cast(scope { item: true }, Number)", "cast(./file, Process)", "let item?: String = null; cast(item, Path)",
        "let target = Number; let values = $std::ranges::transform([\"12\"], fn(value) { cast(value, target) }); target = String; $std::ranges::first(values)",
        "let holder = scope { target: Number }; let alias = holder; let values = $std::ranges::transform([\"12\"], fn(value) { cast(value, holder::target) }); alias::target = String; $std::ranges::first(values)",
        "let target = Number; fn change() { target = String; }; change(); target = Number; let values = $std::ranges::transform([\"12\"], fn(value) { cast(value, target) }); change(); $std::ranges::first(values)",
        "let target = Number; let changes = $std::ranges::transform([true], fn(value) { target = String; value }); let values = $std::ranges::transform([\"12\"], fn(value) { cast(value, target) }); $std::ranges::size(changes); $std::ranges::first(values)" })
    {
        try { SourceCompiler.Compile(declaration + source); throw new InvalidOperationException("Invalid cast was accepted: " + source); }
        catch (CompileException error) { Check(error.Message.Contains("cast", StringComparison.OrdinalIgnoreCase), "Cast diagnostic lost its operation"); }
    }
    foreach (string source in new[] { "cast(\"not a number\", Number)", "cast(\"some text\", Bool)", "cast(\"forge\", Connection)" })
    {
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = SourceCompiler.Compile(declaration + source, "cast-failure", level);
            try { await Interpreter.ExecuteAsync(RoundTrip(Binding.Bind(module, new(root, root)))); throw new InvalidOperationException("Invalid scalar text succeeded"); }
            catch (MsyncException error) { Check(error.Message.Contains("cast", StringComparison.OrdinalIgnoreCase), "Runtime cast diagnostic lost the operation"); }
        }
    }
    Module rangeCast = SourceCompiler.Compile(declaration + "cast([\"12\", \"13\"], decltype([0]))", "range-cast");
    RangeValue range = (RangeValue)await Interpreter.ExecuteAsync(rangeCast, new(WorkingDirectory: root));
    List<double> items = [];
    await foreach (Value value in range.Items) items.Add(((NumberValue)value).Data);
    Check(items.SequenceEqual([12d, 13d]), "Range element casting did not preserve order");
    Module badRange = SourceCompiler.Compile(declaration + "cast([\"invalid\"], decltype([0]))", "lazy-range-cast");
    RangeValue lazy = (RangeValue)await Interpreter.ExecuteAsync(badRange, new(WorkingDirectory: root));
    Check(lazy.ItemType == TypeRef.Number, "Range cast result has no target item type");
    try
    {
        await foreach (Value _ in lazy.Items) { }
        throw new InvalidOperationException("Lazy range cast accepted invalid text");
    }
    catch (MsyncException) { assertions++; }
    CompilerSession compiler = new();
    ExecutionSession execution = new(new(WorkingDirectory: root));
    async Task<Value> Submit(string source)
    {
        Value result = await execution.ExecuteAsync(compiler.Compile(source));
        compiler.AcceptSubmission(execution.GetLocalValue);
        return result;
    }
    await Submit("let target = Number;");
    try
    {
        compiler.Compile("let rolled_back = $std::ranges::transform([\"12\"], fn(value) { $std::cast(value, target) }); let bad: String = true;");
        throw new InvalidOperationException("Invalid submission succeeded");
    }
    catch (CompileException) { compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue); }
    await Submit("target = String;");
    Check(await Submit("$std::cast(12, target)") is StringValue { Data: "12" }, "Rejected submission leaked a dependent cast target contract");
    await Submit("let values = $std::ranges::transform([12], fn(value) { $std::cast(value, target) });");
    EditorAnalysis preview = compiler.AnalyzeEditor("target = Number;", execution.GetLocalValue);
    Check(preview.Diagnostic?.Code == "E0213", "Editor preview lost a deferred cast type dependency");
    Check(await Submit("$std::ranges::first(values)") is StringValue { Data: "12" }, "Editor preview changed accepted type-value metadata or cells");
    Console.WriteLine($"PASS: {assertions} typed cast, metadata, identity, IEEE, null, lazy range and artifact contracts");
}
finally { Directory.Delete(root, recursive: true); }
