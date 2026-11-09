using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

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
        ("42 as String", "String:42"),
        ("true as String", "String:true"),
        ("0 as Bool", "Bool:false"),
        ("(-1) as Bool", "Bool:true"),
        ("\" false \" as Bool", "Bool:false"),
        ("\"1.25e2\" as Number", "Number:405f400000000000"),
        ("\"-0\" as Number", "Number:8000000000000000"),
        ("false as Number", "Number:0000000000000000"),
        ("null as Null", "Null:null"),
        ("\"ssh://forge\" as Connection", "Connection:ssh://forge"),
        ("ssh://forge as String", "String:ssh://forge"),
        ("let item = scope { value: \"before\" }; let alias = item as Scope; alias::value = \"after\"; item::value", "String:after"),
        ("let callback = fn { \"captured\" }; (callback as Function)()", "String:captured"),
        ("null as Path?", "Null:null"),
        ("let parsed = \"12\" as Number; if decltype(parsed) is Number { \"typed\" } else { \"wrong\" }", "String:typed"),
        ("let alias = scope { item: \"field\" } as Scope; alias::item", "String:field"),
        ("fn convert(value) { value as Number }; let first = convert(\"12\"); let second = first as String; if decltype(first) is Number and decltype(second) is String { second } else { \"wrong\" }", "String:12"),
        ("let target = Number; let values = $std::ranges::transform([\"12\"], fn(value) { value as Number }); target = String; $std::ranges::first(values)", "Number:4028000000000000"),
        ("let target = if exists ./flag { Number } else { String }; if target is Number { \"number\" } else { \"string\" }", "String:number")
    })
    {
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = SourceCompiler.Compile(source, "cast-contract", level);
            foreach (Module image in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module, new(root, root))) })
                Check(Snapshot(await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root))) == expected, $"O{level}: {source}");
        }
    }
    foreach ((string source, string code) in new[]
    {
        ("null as String", "E0220"),
        ("scope { item: true } as Number", "E0220"),
        ("./file as Process", "E0220"),
        ("let item?: String = null; item as Path", "E0220"),
        ("let target = Number; \"12\" as target", "E0202"),
        ("fn convert(value, target: Type) { value as target }; convert(\"12\", Number)", "E0202")
    })
    {
        try { SourceCompiler.Compile(source); throw new InvalidOperationException("Invalid cast was accepted: " + source); }
        catch (CompileException error) { Check(error.Diagnostic.Code == code, "Unexpected cast diagnostic: " + error.Message); }
    }
    foreach (string source in new[] { "\"not a number\" as Number", "\"some text\" as Bool", "\"forge\" as Connection" })
    {
        foreach (int level in new[] { 0, 2, 3 })
        {
            Module module = SourceCompiler.Compile(source, "cast-failure", level);
            try { await Interpreter.ExecuteAsync(RoundTrip(Binding.Bind(module, new(root, root)))); throw new InvalidOperationException("Invalid scalar text succeeded"); }
            catch (MsyncException error) { Check(error.Message.Contains("cast", StringComparison.OrdinalIgnoreCase), "Runtime cast diagnostic lost the operation"); }
        }
    }
    Module rangeCast = SourceCompiler.Compile("[\"12\", \"13\"] as Range<Number>", "range-cast");
    RangeValue range = (RangeValue)await Interpreter.ExecuteAsync(rangeCast, new(WorkingDirectory: root));
    List<double> items = [];
    await foreach (Value value in range.Items) items.Add(((NumberValue)value).Data);
    Check(items.SequenceEqual([12d, 13d]), "Range element casting did not preserve order");
    Module badRange = SourceCompiler.Compile("[\"invalid\"] as Range<Number>", "lazy-range-cast");
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
        compiler.Compile("let rolled_back = $std::ranges::transform([\"12\"], fn(value) { value as Number }); let bad: String = true;");
        throw new InvalidOperationException("Invalid submission succeeded");
    }
    catch (CompileException) { compiler.ReconcileSubmission(execution.IsLocalInitialized, execution.GetLocalValue); }
    await Submit("target = String;");
    Check(await Submit("12 as String") is StringValue { Data: "12" }, "Rejected submission leaked a static cast contract");
    await Submit("let values = $std::ranges::transform([12], fn(value) { value as String });");
    EditorAnalysis preview = compiler.AnalyzeEditor("target = Number;", execution.GetLocalValue);
    Check(preview.Diagnostic is null, "An unrelated Type-value mutation changed a static cast");
    Check(await Submit("$std::ranges::first(values)") is StringValue { Data: "12" }, "Editor preview changed a static cast result or captured cells");
    Console.WriteLine($"PASS: {assertions} typed cast, metadata, identity, IEEE, null, lazy range and artifact contracts");
}
finally { Directory.Delete(root, recursive: true); }
