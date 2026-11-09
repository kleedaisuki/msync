using Msync.Optimization;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;

int checks = 0;
string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "control-header-paths");
Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory, "entry.txt"), "entry");
foreach (string name in new[] { "and", "or", "is", "as" }) File.WriteAllText(Path.Combine(directory, name), name);

foreach (string path in new[] { "./", "../", "~/", "/", "C:/", "./nested/", "./ @ $local" })
{
    Check(Expression($"if exists {path} {{ true }} else {{ false }}") is IfExpr
        { Condition: UnaryExpr { Operator: "exists" }, Then.Statements.Count: 1, Else: BlockExpr },
        "if owns its body after " + path);
    Check(Expression($"match {path} {{ _ => {{ true }}, }}") is MatchExpr { Arms.Count: 1 },
        "match owns its arms after " + path);
    Check(Expression($"for item in {path} {{ item; }}") is ForExpr { Body.Statements.Count: 1 },
        "for owns its body after " + path);
}

Check(Expression("if exists ./ { true } else { false }") is IfExpr
    { Condition: UnaryExpr { Operand: BinaryExpr { Operator: "/", Right: PathExpr { Path: "" } } } },
    "trailing separator has an empty child, not the body block");
Check(Expression("if exists / { true } else { false }") is IfExpr
    { Condition: UnaryExpr { Operand: PathExpr { Path: "/" } } }, "root slash ends at header body");

foreach (string name in new[] { "and", "or", "is", "as" })
    Check(Expression("./" + name) is BinaryExpr { Operator: "/", Right: NameExpr child } && child.Name == name,
        "adjacent keyword remains a filename: " + name);
Check(Expression("./ and flag") is BinaryExpr { Operator: "and", Left: BinaryExpr { Operator: "/", Right: PathExpr { Path: "" } } },
    "separated operator resumes the surrounding expression");

foreach (string source in new[]
{
    "if not exists ./missing/ { true } else { false }",
    "if exists ./ and exists ./ { true } else { false }",
    "if exists ./ or exists ./missing/ { true } else { false }",
    "if exists / and exists ./ { true } else { false }",
    "if ./ is Path { true } else { false }",
    """if ./ as String == "./" { true } else { false }""",
    "if exists ./and and exists ./or and exists ./is and exists ./as { true } else { false }",
    """if exists ./"and" and exists ./"or" { true } else { false }""",
    "if exists ./({ \".\" }) { true } else { false }",
    "if exists (./{ \".\" }) { true } else { false }",
    "if exists (fn { ./{ \".\" } })() { true } else { false }",
    "if { exists ./ } { true } else { false }",
    "if exists ./ { if exists ./ { true } else { false } } else { false }",
    "match ./ { _ => { true }, }",
    "let count = 0; for file in $std::fs::directory_iterator(./) { count = count + 1; }; count > 0",
    "fn current() { return ./{ \".\" }; }; exists current()",
    "fn returned() { return { ./{ \".\" } }; }; exists returned()",
    "let current = loop { break { ./{ \".\" } }; }; exists current",
    "let joined = ./{ \".\" }; exists joined",
    "let selected = [./{ \".\" }]; $std::ranges::all_of(selected, fn(path: Path) { exists path })",
})
{
    string formatted;
    try { formatted = Formatter.Format(source); }
    catch (Exception error) { throw new InvalidOperationException(source, error); }
    try { Check(Formatter.Format(formatted) == formatted, "formatter idempotence: " + source); }
    catch (Exception error) { throw new InvalidOperationException(source + "\nFORMATTED:\n" + formatted, error); }
    Check(Parser.ParseForEditor(source).Error is null, "editor header grammar: " + source);
    foreach (int level in new[] { 0, 2, 3 })
        foreach (string text in new[] { source, formatted })
        {
            Module module = Compiler.Compile(text, Path.Combine(directory, "fixture.msync"), level);
            foreach (Module image in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module, new(directory, directory))) })
            {
                Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: directory));
                Check(result is BoolValue { Data: true }, $"O{level} {image.Stage}: {source}\n{result}");
            }
        }
}

Console.WriteLine($"PASS {checks} control-header path contracts");

static Expr Expression(string source) => ((ExpressionStatement)Parser.Parse(source).Statements.Single()).Expression;

static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    return Artifact.Read(bytes);
}

void Check(bool value, string description)
{
    checks++;
    if (!value) throw new InvalidOperationException(description);
}
