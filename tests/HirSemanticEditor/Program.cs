using Msync.Compiler;

int checks = 0;
void Require(bool condition, string reason)
{
    checks++;
    if (!condition) throw new InvalidOperationException(reason);
}

if (args.Contains("--editor-revisions", StringComparer.Ordinal))
{
    EditorRevisionChecks.Run(Require);
    Console.WriteLine($"PASS {checks} editor revision contracts");
    return;
}

if (args.Contains("--declaration-metadata", StringComparer.Ordinal))
{
    DeclarationMetadataChecks.Run(Require);
    Console.WriteLine($"PASS {checks} declaration metadata editor contracts");
    return;
}

if (args.Contains("--origins-and-roles", StringComparer.Ordinal))
{
    OriginAndRoleChecks.Run(Require);
    Console.WriteLine($"PASS {checks} origin and role editor contracts");
    return;
}

const string source = "scope Named { value: 7, }; let object = scope { item: \"Klee\" }; object::item;";
EditorAnalysis analysis = EditorCompiler.Analyze(source, "semantic-editor.msync");
Require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Unexpected error");
int selected = source.LastIndexOf("item", StringComparison.Ordinal);
Require(analysis.Occurrences.Any(item => item.Span.Start == selected && item.Type == "String"), "Typed member occurrence");
Require(analysis.DefinitionsAt(selected).Any(target => target.SourceName == "semantic-editor.msync"
    && target.Source[target.Span.Start..target.Span.End] == "item"), "Member definition source identity");
Require(analysis.At(source.Length).Bindings.Any(item => item.Name == "object" && item.SemanticRole == "variable"
    && item.Members?.Any(field => field.Name == "item" && field.Type == "String") == true), "Anonymous instance members");
Require(analysis.At(source.Length).Bindings.Any(item => item.Name == "Named" && item.SemanticRole == "type"), "Named Scope role");

const string generic = "fn identity(value) { value }; let number = identity(7); let text = identity(\"Klee\");";
EditorAnalysis calls = EditorCompiler.Analyze(generic, "generic-editor.msync");
Require(calls.Diagnostic is null, calls.Diagnostic?.Message ?? "Generic error");
Require(calls.Resolve(generic.Length, "number")?.Type == "Number", "Number call specialization");
Require(calls.Resolve(generic.Length, "text")?.Type == "String", "String call specialization");

const string partial = "fn unfinished(argument) { let local = scope { item: 7 }; local::";
EditorAnalysis editing = EditorCompiler.Analyze(partial, "partial-editor.msync");
Require(editing.At(partial.Length).Bindings.Any(item => item.Name == "argument"), "Uncalled generic parameter remains in lexical scope");
Require(editing.At(partial.Length).Receiver?.Members?.Any(item => item.Name == "item") == true,
    "Incomplete uncalled generic receiver retains known fields");

const string invalid = "let kept = 7; let broken: Missing = true; kept;";
EditorAnalysis recovery = EditorCompiler.Analyze(invalid, "recovery-editor.msync");
Require(recovery.Diagnostic is not null, "Invalid annotation diagnostic");
Require(recovery.Resolve(invalid.Length, "kept")?.Type == "Number", "Semantic error retains earlier bindings");
ObservationChecks.Run(Require);
IndexedObservationChecks.Run(Require);
CallableObservationChecks.Run(Require);
Console.WriteLine($"PASS {checks} typed semantic editor contracts");
