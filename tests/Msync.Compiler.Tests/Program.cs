using Msync.Compiler.Syntax;
if (args.Contains("--branch-joins", StringComparer.Ordinal)) { SemanticChecks.RunBranches(); return; }
string[] examples = [
"fn copy() { ~/.codex -> $wsl::~/.codex }; let replace = fn (file, des) { file -> des };",
"scope ScopeName { item: ~/.codex, ready: true, op: fn SomeOperation { true }, nested: scope { file: ~/.codex, }, }; let ScopeName2 = scope { item: true, };",
"let file = match exists ~/.codex { true => { ~/.codex/config.toml }, _ => { ~ }, };",
"let file = match SomeSymbol { <.+> => {}, _ => {}, };",
"native fn exists(path: Path | String) -> Bool; let file? = if exists ~/.codex { ~/.codex } else { null }; if file is null {} else { file -> $wsl::file }; if file? {} else { file -> $wsl::file };",
"let result = if decltype(~/.codex) is Path { true } else { false }; let remote = ssh://forge; for file in ~/.codex/<.+> { file -> remote::file };",
"fn SomeOperation(file: Path | String, des) { file -> des }; let values: Range<Path> = [~/a, ~/b];",
"let source = C:\\Users\\Klee\\Documents; source -> /tmp/copied/; let f = fn (x: String?) -> Bool { x is null };",
"let _ = match file { null => { true }, Path => { false }, _ => { true } };"
];
foreach (string s in examples) { var d = Parser.Parse(s); Console.WriteLine($"ok {d.Statements.Count}"); }
var pathDoc = Parser.Parse("let x=source=1; source=1 -> output=2; file name/config.toml -> target dir/;");
Console.WriteLine($"paths ok {pathDoc.Statements.Count}");
var rng = new Random(704);
for (int iteration = 0; iteration < 1000; iteration++)
{
    var chars = new char[rng.Next(80)];
    for (int i = 0; i < chars.Length; i++) chars[i] = (char)rng.Next(0x10000);
    string expected = new(chars);
    string quoted = "\"" + expected.Replace("\"", "\"\"") + "\"";
    var parsed = Parser.Parse(quoted);
    if (((StringExpr)((ExpressionStatement)parsed.Statements[0]).Expression).Value != expected) throw new Exception("string roundtrip");
    var formattedLiteral = Parser.Parse(Formatter.Format(quoted));
    if (((StringExpr)((ExpressionStatement)formattedLiteral.Statements[0]).Expression).Value != expected) throw new Exception("formatted string roundtrip");
}
Console.WriteLine("1000 literal roundtrips ok");
foreach (string invalid in new[] { "let a = true", "fn bad( { }", "match true { true {} }", "if true {", "native fn f() -> Bool" })
{
    try { Parser.Parse(invalid); throw new Exception("accepted malformed: " + invalid); }
    catch (SyntaxException) { }
}
Console.WriteLine("malformed diagnostics ok");
foreach (string s in examples.Append("# hello\nlet f = fn(x) {\n# inside\nx\n}; # after\nfile  name -> target dir/;"))
{
    string formatted = Formatter.Format(s);
    try { Parser.Parse(formatted); }
    catch { Console.WriteLine(formatted); throw; }
    if (Formatter.Format(formatted) != formatted) { Console.WriteLine(formatted); Console.WriteLine(Formatter.Format(formatted)); throw new Exception("formatter not idempotent"); }
}
Console.WriteLine("formatter parse/idempotence ok");
var uriDocument = Parser.Parse("let remote = ssh://user@[2001:db8::1]:2222/work/tree; remote::~ -> ./copy;");
if (((PathExpr)((LetStatement)uriDocument.Statements[0]).Value).Path != "ssh://user@[2001:db8::1]:2222/work/tree") throw new Exception("IPv6 connection token split");
var regexDocument = Parser.Parse("let f = match file { <[<>]|a\\>b> => { true }, _ => { false } }; let less = \"a\" < \"b\";");
if (((RegexPattern)((MatchExpr)((LetStatement)regexDocument.Statements[0]).Value).Arms[0].Pattern).Pattern != "[<>]|a\\>b") throw new Exception("Regex token split");
Console.WriteLine("URI and regex shielding ok");
foreach (string invocation in new[] { "~/bin/tool(\"hello\")", "$std::fs::exists(~/file)", "remote::tool(\"hello\")", "(fn(x) { x })(true)" })
{
    if (((ExpressionStatement)Parser.Parse(invocation).Statements[0]).Expression is not CallExpr) throw new Exception("Path/qualified call binds to a component: " + invocation);
}
Console.WriteLine("Path and namespace call precedence ok");

var windowsDocument = Parser.Parse("C:/Users/Klee/config.toml -> C:/Users/Klee/backup.toml;");
var windowsCopy = (BinaryExpr)((ExpressionStatement)windowsDocument.Statements[0]).Expression;
if (((PathExpr)windowsCopy.Left).Path != "C:/Users/Klee/config.toml") throw new Exception("Windows forward-slash drive split");
Console.WriteLine("Windows drive paths ok");
var arithmeticDocument = Parser.Parse("fn countdown(value) { if value == 0 { true } else { countdown(value - 1) } }; countdown(4);");
var arithmeticFunction = ((FunctionDeclaration)arithmeticDocument.Statements[0]).Function;
var arithmeticIf = (IfExpr)((ExpressionStatement)arithmeticFunction.Body.Statements[0]).Expression;
var arithmeticElse = (BlockExpr)arithmeticIf.Else!;
var arithmeticCall = ((CallExpr)((ExpressionStatement)arithmeticElse.Statements[0]).Expression).Arguments[0];
if (arithmeticCall is not BinaryExpr { Operator: "-" }) throw new Exception("Arithmetic recursion operand parsed as a Path");
Console.WriteLine("Arithmetic recursion syntax ok");
SemanticChecks.Run();
SemanticChecks.RunBranches();
