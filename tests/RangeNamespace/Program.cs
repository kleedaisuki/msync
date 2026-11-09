using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Frontend = Msync.Compiler.Compiler;

foreach (string alias in new[] { "view", "views", "ranges" })
{
    Reject("$" + alias);
    Reject("$" + alias + "::front([true]);");
    Reject(alias + "::front([true]);");
    Require(await Interpreter.ExecuteAsync(Frontend.Compile($"let {alias} = $std::ranges; {alias}::front([true])")) is BoolValue { Data: true },
        "An explicit lexical binding stopped being an ordinary value.");

    Module historical = new([], [new("historical", 0, 0,
        [new(OpCode.LoadBuiltin, Text: alias), new(OpCode.GetMember, Text: "front"),
         new(OpCode.True), new(OpCode.MakeRange, 1), new(OpCode.Call, 1), new(OpCode.Return)], TypeRef.Any)]);
    using MemoryStream bytes = new();
    Artifact.Write(historical, bytes);
    bytes.Position = 0;
    Require(await Interpreter.ExecuteAsync(Artifact.Read(bytes)) is BoolValue { Data: true },
        $"An existing compiled '{alias}' builtin identity stopped executing.");
}

const string source = "let values = $std::ranges::filter([true, false, true], fn(value) { value }); $std::ranges::front($std::ranges::take(values, 1))";
Require(await Interpreter.ExecuteAsync(Frontend.Compile(source)) is BoolValue { Data: true }, "The canonical source range API failed.");
Require(EditorCompiler.BuiltinBindings.Select(binding => binding.Name).Order().SequenceEqual(
    new[] { "$std", "$local", "$wsl", "$pwd", "$file", "$env", "$path" }.Order()), "The public builtin catalog still contains range aliases.");
Require(EditorCompiler.BuiltinBindings.Single(binding => binding.Name == "$std").Members!.Single(member => member.Name == "ranges").Members!
    .Any(member => member.Name == "filter"), "The canonical range module is missing editor members.");
Console.WriteLine("PASS: canonical ranges, source alias migration, lexical bindings and historical object execution.");

static void Reject(string source)
{
    try { _ = Frontend.Compile(source); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Code == "E0210" && error.Diagnostic.Help!.Contains("$std::ranges", StringComparison.Ordinal),
            "Removed aliases need a source-owned canonical migration diagnostic.");
        return;
    }
    throw new InvalidOperationException("A removed predefined range alias was accepted: " + source);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
