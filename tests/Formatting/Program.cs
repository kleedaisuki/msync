using Msync.Compiler.Syntax;

string Format(string source)
{
    string result = Formatter.Format(source);
    _ = Parser.Parse(result);
    if (Formatter.Format(result) != result)
        throw new Exception($"Non-idempotent formatting:\n{result}\nSecond:\n{Formatter.Format(result)}");
    return result;
}
void Expect(string source, params string[] fragments)
{
    string result = Format(source);
    foreach (string fragment in fragments)
        if (!result.Contains(fragment, StringComparison.Ordinal)) throw new Exception($"Missing {fragment}:\n{result}");
}

Expect("let n=1.5+2e3; let x=-0; let value=3 % 2; let path=~/.codex/AGENTS.md;",
    "1.5 + 2e3", "-0", "3 % 2", "~/.codex/AGENTS.md");
Expect("let power=-2**2; let inverse=2**-2;", "-2 ** 2", "2 ** -2");
Expect("let value=2; (-value) as String; let copy-config=2; -copy-config; let flag=--flag; let path=./-filename;",
    "(-value) as String", "-copy-config;", "--flag", "./-filename");
Expect("let value=./config @ ssh://forge; let text=value as String;", "./config @ ssh://forge", "value as String");
string namedScopeSource = "let x = scope NamedScope { item:1 };";
foreach (string text in new[] { namedScopeSource, Format(namedScopeSource) })
{
    var binding = (LetStatement)Parser.Parse(text).Statements[0];
    var scope = (ScopeExpr)binding.Value;
    if (scope.Name != "NamedScope" || scope.NameSpan is not { } nameSpan || text[nameSpan.Start..nameSpan.End] != "NamedScope")
        throw new Exception($"Named Scope expression identity was lost:\n{text}");
}
Expect("scope SomeClass { new: fn -> Scope { scope { item: ~/.codex/AGENTS.md } }, copy: fn(where: Connection, object: Scope) { object::item @ where -> ./ } };",
    "scope SomeClass {\n    new: fn -> Scope {", "object::item @ where -> ./");
Expect("concept Monoid { type Self; identity: Self, combine: fn(left: Self, right: Self) -> Self, };", "concept Monoid {", "identity: Self,\n", "combine: fn(");
Expect("concept Product { first?: typename First, second?: typename Second, };",
    "first?: typename First,", "second?: typename Second,");
Expect("concept Sequence { items:Range<Range<typename Item>>, visit:fn(item:Item)->Range<Item>, };",
    "items: Range<Range<typename Item>>", "item: Item", "-> Range<Item>");
Expect("fn read(values:Range<Range<String>>)->Range<String>{ values }; let n=1; n<2; n>0;",
    "values: Range<Range<String>>", "-> Range<String>", "n < 2", "n > 0");
Expect("let text = \"  first\n  last  \"; # retain this comment\nlet pattern=match ./abc { <[a-z]{3}> => { true }, _ => { false } };", "\"  first\n  last  \"", "# retain this comment", "<[a-z]{3}>");
string logical = string.Join(" and ", Enumerable.Repeat("true == true", 12));
string chain = Format($"let accepted = {logical};");
if (chain.Split('\n').Count(line => line.TrimStart().StartsWith("and ", StringComparison.Ordinal)) < 10)
    throw new Exception($"Long chains must expose each operand:\n{chain}");
foreach (int depth in new[] { 0, 3, 8, 12 })
{
    string source = "let value = " + string.Concat(Enumerable.Repeat("scope { child: ", depth))
        + "[" + string.Join(", ", Enumerable.Range(0, 24)) + "]"
        + string.Concat(Enumerable.Repeat(" }", depth)) + ";";
    string result = Format(source);
    if (result.Split('\n').Any(line => line.Length > 80)) throw new Exception(result);
}
Expect("fn one() { true }; fn two() { false }; let value=true;", "};\n\nfn two", "};\n\nlet value");
Expect("let first=true;\n\n# A distinct paragraph.\nlet second=false;", "true;\n\n# A distinct paragraph.");
Console.WriteLine("PASS formatter new syntax, logical chains, nested hanging layout, comments and literal preservation, parse/idempotence");

Random random = new(704);
for (int iteration = 0; iteration < 1_000; iteration++)
{
    string payload = new(Enumerable.Range(0, random.Next(80)).Select(_ => (char)random.Next(0x10000)).ToArray());
    string literal = "\"" + payload.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    string result = Format(literal);
    var expression = (ExpressionStatement)Parser.Parse(result).Statements[0];
    if (((StringExpr)expression.Expression).Value != payload) throw new Exception("String content changed");
}
Expect("fn first() { true } fn second() { false }", "}\n\nfn second");
Expect("# Paragraph one.\n\n# Paragraph two.\nlet x=true;", "# Paragraph one.\n\n# Paragraph two.");
Expect("fn numeric(value) { value / 2 + 3 * -1 }", "value / 2 + 3 * -1");
Console.WriteLine("PASS 1000 arbitrary UTF16 literals and declaration/comment paragraphs");
