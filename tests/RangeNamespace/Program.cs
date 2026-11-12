using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

foreach (string alias in new[] { "view", "views", "ranges" })
{
    Reject("$" + alias, "E0210", "Use '$std::ranges'");
    Reject("$" + alias + "::front([true]);", "E0210", "Use '$std::ranges'");
    Reject(alias + "::front([true]);", "E0206", "from Path");
    var portable = Frontend.Compile($"let {alias} = $std::ranges; {alias}::front([true])");
    string cwd = Environment.CurrentDirectory;
    foreach (var image in new[] { portable, RoundTrip(portable), RoundTrip(Binding.Bind(portable, new(cwd, cwd))) })
        Require(await Interpreter.ExecuteAsync(image) is BoolValue { Data: true },
            "An explicit lexical binding stopped being an ordinary value after lowering or serialization.");
}

const string source = "let values = $std::ranges::filter([true, false, true], fn(value) { value }); $std::ranges::front($std::ranges::take(values, 1))";
Require(await Interpreter.ExecuteAsync(Frontend.Compile(source)) is BoolValue { Data: true }, "The canonical source range API failed.");
Require(EditorCompiler.BuiltinBindings.Select(binding => binding.Name).Order().SequenceEqual(
    new[] { "$std", "$local", "$wsl", "$pwd", "$file", "$env", "$path" }.Order()), "The public builtin catalog still contains range aliases.");
var editor = EditorCompiler.Analyze(source, "canonical-ranges.msync");
Require(editor.Diagnostic is null
    && editor.OccurrenceAt(source.IndexOf("filter", StringComparison.Ordinal)) is { Signature: var signature }
    && signature.Contains("filter", StringComparison.Ordinal), "The canonical range member is missing from actual editor analysis.");
Console.WriteLine("PASS: canonical ranges, removed source aliases and ordinary lexical bindings in source and artifacts.");

static Module RoundTrip(Module image)
{
    using MemoryStream bytes = new();
    Artifact.Write(image, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}

static void Reject(string source, string code, string detail)
{
    try { _ = Frontend.Compile(source); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Code == code && error.Diagnostic.Message.Contains(detail, StringComparison.Ordinal),
            "Explicit removed references need migration guidance; unbound bare names remain Paths.");
        return;
    }
    throw new InvalidOperationException("A removed predefined range alias was accepted: " + source);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
