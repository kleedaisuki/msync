using Msync.Cli;
using Msync.Compiler;

int checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

const string source = "fn summarize(operations: $std::concepts::Monoid) { operations::identity };";
EditorAnalysis analysis = EditorCompiler.Analyze(source, "standard-concepts.msync");
Require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Unexpected diagnostic.");
int monoid = source.IndexOf("Monoid", StringComparison.Ordinal);
Require(analysis.IsTypePosition(monoid), "Qualified concept is not a type position.");
Require(analysis.At(monoid).Bindings.Any(member => member.Name == "$std::concepts::Monoid" && member.IsConstraint),
    "Standard concept is absent from type-only bindings.");
Require(!analysis.At(source.IndexOf("identity", StringComparison.Ordinal)).Bindings.Any(member => member.Name == "$std::concepts::Monoid"),
    "A compile-time concept leaked into runtime completion.");
Require(analysis.Occurrences.Any(item => item.Span.Start == monoid && item.SemanticRole == "type"
    && item.Signature.Contains("Monoid", StringComparison.Ordinal) && item.Description.Contains("identity", StringComparison.Ordinal)),
    "Qualified concept hover is missing required members.");
Require(analysis.DefinitionsAt(monoid).Any(location => location.SourceName.EndsWith("/concepts.msync", StringComparison.Ordinal)
    && location.Source[location.Span.Start..location.Span.End] == "Monoid"), "Concept definition does not navigate to embedded source.");
foreach (string name in new[] { "std", "concepts", "Monoid" })
{
    int start = source.IndexOf(name, StringComparison.Ordinal);
    Require(analysis.Occurrences.Any(item => item.Span.Start == start && item.Span.Length == name.Length && item.SemanticRole == "type"),
        "Qualified type segment lost type coloring: " + name);
}

foreach ((string fragment, string expected) in new[]
{
    ("$", "$std::"), ("$std::", "concepts::"), ("$std::concepts::", "Monoid"),
    ("$std::concepts::Mono", "Monoid"), ("$std :: concepts :: Mono", "Monoid")
})
{
    string text = "fn summarize(operations: " + fragment + ") { operations };";
    int caret = text.IndexOf(')');
    EditorAnalysis incomplete = EditorCompiler.Analyze(text, "editing-concept.msync");
    EditorContext context = incomplete.At(caret);
    Require(context.IsTypePosition, "Incomplete qualified annotation lost its type context: " + fragment);
    TypeCompletionContext selected = TypeCompletion.Find(text, caret);
    var candidates = TypeCompletion.Candidates(selected, context);
    Require(candidates.Any(candidate => candidate.Name == expected), "Missing concept completion: " + fragment);
    var candidate = candidates.Single(item => item.Name == expected);
    string completed = text[..selected.Span.Start] + candidate.Name + text[selected.Span.End..];
    Require(!completed.Contains("$$", StringComparison.Ordinal) && !completed.Contains("concepts::concepts", StringComparison.Ordinal),
        "Completion duplicated a qualifier: " + completed);
    if (expected == "Monoid")
        Require(EditorCompiler.Analyze(completed, "completed-concept.msync").Diagnostic is null, "Applied concept completion does not typecheck.");
}

const string derived = "concept Named { $std::concepts::Monoid, name: String, };";
EditorAnalysis composed = EditorCompiler.Analyze(derived, "derived-standard-concept.msync");
Require(composed.Diagnostic is null, composed.Diagnostic?.Message ?? "Qualified concept composition failed.");
Require(composed.At(derived.IndexOf("Monoid", StringComparison.Ordinal)).IsTypePosition, "Qualified concept base has no type context.");

const string optional = "fn preserve(value: $std::concepts::Option?) -> $std::concepts::Option? { value };";
EditorAnalysis option = EditorCompiler.Analyze(optional, "optional-standard-concept.msync");
Require(option.Diagnostic is null, option.Diagnostic?.Message ?? "Nullable standard Option annotation failed.");
int optionType = optional.IndexOf("Option", StringComparison.Ordinal);
Require(option.Occurrences.Any(item => item.Span.Start == optionType && item.Signature == "concept Option"
    && item.Description.Contains("Null", StringComparison.Ordinal)), "Option hover is missing its native Null semantics.");
Require(option.DefinitionsAt(optionType).Any(location => location.SourceName.EndsWith("/concepts.msync", StringComparison.Ordinal)
    && location.Source[location.Span.Start..location.Span.End] == "Option"), "Option constraint cannot navigate to its definition.");
Require(option.At(optional.LastIndexOf("value", StringComparison.Ordinal)).Bindings.Single(member => member.Name == "value")
    .Members?.Any(member => member.Name == "value") == true, "Uncalled Option-constrained function lost its payload field.");

const string shadowed = "let std = scope { }; fn inspect(value: Number) { value };";
EditorAnalysis lexical = EditorCompiler.Analyze(shadowed, "shadowed-standard.msync");
Require(!lexical.At(shadowed.IndexOf("Number", StringComparison.Ordinal)).Bindings.Any(member => member.Name.StartsWith("$std::", StringComparison.Ordinal)),
    "Standard concept completion ignored lexical std shadowing.");

const string inline = "concept Pair { first?: typename First, second?: typename Second, convert: fn(value: First) -> Second, };";
EditorAnalysis pair = EditorCompiler.Analyze(inline, "inline-associated.msync");
Require(pair.Diagnostic is null, pair.Diagnostic?.Message ?? "Inline associated declaration failed.");
int firstDefinition = inline.IndexOf("First", StringComparison.Ordinal);
int firstUse = inline.LastIndexOf("First", StringComparison.Ordinal);
Require(pair.Occurrences.Any(item => item.Span.Start == firstDefinition && item.Signature == "typename First" && item.SemanticRole == "type"),
    "Inline binder lost its canonical type signature.");
Require(pair.DefinitionsAt(firstUse).Single().Span.Start == firstDefinition, "Associated type use does not navigate to the inline binder.");
Require(pair.At(firstUse).Bindings.Any(member => member.Name == "First" && member.Signature == "typename First"),
    "Inline associated type is absent from concept-local completion.");
const string nested = "concept Sequence { items: Range<Range<typename name>>, visit: fn(item: name) -> name, };";
EditorAnalysis sequence = EditorCompiler.Analyze(nested, "nested-associated.msync");
Require(sequence.Diagnostic is null, sequence.Diagnostic?.Message ?? "Nested inline binder failed.");
int nestedBinder = nested.IndexOf("name>>", StringComparison.Ordinal);
Require(sequence.Occurrences.Any(item => item.Span.Start == nestedBinder && item.Signature == "typename name"),
    "A lowercase binder matched the suffix of the typename keyword.");
Require(sequence.DefinitionsAt(nested.LastIndexOf("name", StringComparison.Ordinal)).Single().Span.Start == nestedBinder,
    "Nested Range binder use does not navigate to its declaration.");
const string declaring = "concept Record { field: typ };";
int declarationCaret = declaring.IndexOf("typ", StringComparison.Ordinal) + 3;
EditorContext declarationContext = EditorCompiler.Analyze(declaring, "declaring-associated.msync").At(declarationCaret);
Require(TypeCompletion.Candidates(TypeCompletion.Find(declaring, declarationCaret), declarationContext)
    .Any(member => member.Name == "typename" && member.SemanticRole == "keyword"), "Concept field completion does not offer typename.");
Require(!TypeCompletion.Candidates(TypeCompletion.Find(optional, optionType), option.At(optionType)).Any(member => member.Name == "typename"),
    "typename was offered outside a concept requirement.");
Console.WriteLine($"PASS {checks} standard concept editor contracts");
