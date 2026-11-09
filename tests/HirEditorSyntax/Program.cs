using Msync.Compiler;
using Msync.Compiler.Syntax;

int checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

string[] sources =
[
    "concept Product { first?: typename First, second?: typename Second, copy: fn(value: First) -> First, };",
    "scope Tools { copy: fn(source: Path, destination: Path) -> Bool { source -> destination }, }; let plain = scope { item: true };",
    "let named = scope Named { item: ~/.codex }; native fn copy(source: Path | String, destination: Path) -> Bool;",
    "fn preserve(value?: $std::concepts::Option) -> $std::concepts::Option? { value };",
    "fn unfinished(argument) { let local = scope { item: 7 }; local::"
];
foreach (string source in sources)
{
    Document document = Parser.ParseForEditor(source, "syntax-contract.msync").Document;
    EditorSyntaxIndex index = EditorSyntaxIndex.Create(document, "syntax-contract.msync", source);
    foreach (SemanticOccurrence occurrence in index.Occurrences)
    {
        Require(occurrence.Span.Start >= 0 && occurrence.Span.End <= source.Length, "Occurrence source ownership");
        Require(occurrence.Signature.Length != 0, "A syntax signature is useful before semantic inference");
    }
    foreach (var reference in index.AssociatedDefinitions)
    {
        Require(reference.Target.SourceName == "syntax-contract.msync", "Associated definition source");
        Require(source[reference.Target.Span.Start..reference.Target.Span.End]
            == source[reference.Span.Start..reference.Span.End], "Associated type target");
    }
    foreach (EditorSymbol symbol in index.Symbols)
        Require(symbol.Span.Start <= symbol.SelectionSpan.Start && symbol.SelectionSpan.End <= symbol.Span.End,
            "Outline selection belongs to its declaration");
}

EditorSyntaxIndex product = EditorSyntaxIndex.Create(Parser.Parse(sources[0]), "product.msync", sources[0]);
Require(product.Occurrences.Any(item => item.Signature == "typename First" && item.SemanticRole == "type"), "Inline associated binder role");
Require(product.AssociatedTypes.Single().Types.Count == 2, "Associated names available inside requirements");
EditorSyntaxIndex tools = EditorSyntaxIndex.Create(Parser.Parse(sources[1]), "tools.msync", sources[1]);
Require(tools.Occurrences.Any(item => item.Signature == "Tools: Scope" && item.SemanticRole == "type"), "Named Scope role");
Require(tools.Occurrences.Any(item => item.Signature == "plain: Scope" && item.SemanticRole == "variable"), "Anonymous Scope binding role");
Require(tools.Occurrences.Any(item => item.Signature.StartsWith("fn copy(source: Path, destination: Path)", StringComparison.Ordinal)), "Function parameter names and types");
EditorSyntaxIndex qualified = EditorSyntaxIndex.Create(Parser.Parse(sources[3]), "qualified.msync", sources[3]);
foreach (string name in new[] { "std", "concepts", "Option" })
    Require(qualified.Occurrences.Any(item => sources[3][item.Span.Start..item.Span.End] == name && item.SemanticRole == "type"), "Qualified constraint segment: " + name);
Require(qualified.TypeSpans.Count >= 2, "Parameter and return annotations expose type-completion positions");
Console.WriteLine($"PASS {checks} syntax-only editor contracts");
