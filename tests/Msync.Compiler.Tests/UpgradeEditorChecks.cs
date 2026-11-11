using Msync.Compiler;
using Msync.Core;

internal static class UpgradeEditorChecks
{
    public static void Run()
    {
        const string source = "concept Named { label: String, run: fn(value: Number) -> Number, }; fn use(value: Named) -> Named { value }; let number = 1.5e2 as String; $std::math::sqrt(4);";
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "upgrade-editor.msync");
        Require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Unexpected diagnostic.");
        int declaration = source.IndexOf("Named", StringComparison.Ordinal);
        int annotation = source.IndexOf("Named)", StringComparison.Ordinal);
        Require(analysis.Occurrences.Any(item => item.Span.Start == declaration && item.SemanticRole == "type"), "Concept declaration lost type highlighting.");
        Require(analysis.Occurrences.Any(item => item.Span.Start == annotation && item.SemanticRole == "type"), "Concept annotation lost type highlighting.");
        Require(analysis.At(annotation).Bindings.Any(item => item.Name == "Named" && item.Type == "Type"), "Concept is absent from type completion.");
        Require(analysis.Symbols.Any(item => item.Name == "Named" && item.Children.Any(child => child.Name == "run" && child.Kind == EditorSymbolKind.Function)), "Concept outline lost callable requirements.");
        Require(analysis.IsTypePosition(source.IndexOf("String;", StringComparison.Ordinal)), "Cast target is not a type-completion position.");
        int module = source.IndexOf("math::", StringComparison.Ordinal);
        Require(analysis.Occurrences.Any(item => item.Span.Start == source.IndexOf("$std", StringComparison.Ordinal)
            && item.SemanticRole == "type"), "Selective standard loading lost the root namespace occurrence.");
        Require(analysis.At(module).Receiver!.Members!.Where(item => item.Type == "Scope").All(item => item.SemanticRole == "type"), "Unloaded named standard modules lost type highlighting.");
        Require(analysis.Occurrences.Any(item => item.Span.Start == module && item.SemanticRole == "type"), "Loaded standard module lost type highlighting.");
        Require(analysis.DefinitionsAt(annotation).Any(item => item.Span.Start == declaration), "Concept annotation cannot navigate to its declaration.");
        string path = "let remote = ssh://forge; remote @ ./input/child;";
        EditorAnalysis physical = EditorCompiler.Analyze(path, "qualified-editor.msync");
        Require(physical.Diagnostic is null, physical.Diagnostic?.Message ?? "Unexpected qualification diagnostic.");
        EditorContext context = physical.At(path.IndexOf("child", StringComparison.Ordinal));
        Require(context.HasPhysicalOwner && context.PhysicalEndpoint?.Kind == ConnectionKind.Ssh, "@ did not propagate the selected endpoint into path completion.");
        const string namedPath = "scope Config { root: ./actual }; Config::root/fi";
        EditorAnalysis named = EditorCompiler.Analyze(namedPath, "named-path-editor.msync");
        Require(named.At(namedPath.Length).Receiver?.KnownPath?.Raw == "./actual", "A named Scope field lost its initial path completion value.");
        const string namedEndpoint = "scope Endpoints { host: ssh://forge }; Endpoints::host @ (./remote-dir/)";
        EditorAnalysis selectedEndpoint = EditorCompiler.Analyze(namedEndpoint, "named-endpoint-editor.msync");
        EditorContext selected = selectedEndpoint.At(namedEndpoint.IndexOf("remote-dir/", StringComparison.Ordinal) + "remote-dir/".Length);
        Require(selected.HasPhysicalOwner && selected.PhysicalEndpoint?.Kind == ConnectionKind.Ssh,
            "A named Scope field lost its initial endpoint completion value.");
        const string generic = "concept Named { label: String, run: fn(value: Number) -> Number, }; fn use(value: Named) { value::label; value::run };";
        EditorAnalysis probe = EditorCompiler.Analyze(generic, "generic-editor.msync");
        int field = generic.LastIndexOf("label", StringComparison.Ordinal);
        Require(probe.At(field).Receiver?.Members?.Any(item => item.Name == "label" && item.Type == "String") == true, "An uncalled concept-constrained function lost structural member completion.");
        int operation = generic.LastIndexOf("run", StringComparison.Ordinal);
        Require(probe.Occurrences.Any(item => item.Span.Start == operation && item.Signature.Contains("Number", StringComparison.Ordinal)),
            "A constrained operation hover lost its required signature: " + string.Join("; ", probe.Occurrences.Where(item => item.Span.Start == operation).Select(item => item.Signature)));
        CompilerSession session = new();
        session.Compile("concept Persisted { item: Path, };");
        session.AcceptSubmission();
        Require(session.DescribeConcepts().Any(item => item.Name == "Persisted" && item.IsConstraint && item.Members!.Any(member => member.Name == "item")),
            "Accepted concepts are absent from persistent REPL display metadata.");
        Require(!session.BindingNames.Contains("Persisted", StringComparer.Ordinal), "A concept leaked into executable binding names.");
        const string specialized = "concept Named { item: Path, }; scope Config { item: ./actual }; fn inspect(value: Named) { value::item }; inspect(Config);";
        EditorAnalysis parameter = EditorCompiler.Analyze(specialized, "parameter-editor.msync");
        int parameterUse = specialized.IndexOf("value::", StringComparison.Ordinal);
        Require(parameter.Occurrences.Any(item => item.Span.Start == parameterUse && item.SemanticRole == "variable"),
            "A runtime Scope parameter inherited its named argument's type colour.");
        Require(parameter.At(parameterUse).Bindings.Any(item => item.Name == "value" && item.SemanticRole == "variable"),
            "Scope parameter completion metadata inherited its named argument's type colour.");
        const string associated = "concept Algebra { type Self; identity: Self, };";
        EditorAnalysis algebra = EditorCompiler.Analyze(associated, "associated-editor.msync");
        Require(algebra.At(associated.LastIndexOf("Self", StringComparison.Ordinal)).Bindings.Any(item => item.Name == "Self" && item.IsConstraint),
            "An associated type is absent from concept-local type completion.");
        const string scopeExpression = "let object = scope Instance { item: ./actual }; let anonymous = scope { item: ./actual };";
        EditorAnalysis namedExpression = EditorCompiler.Analyze(scopeExpression, "named-expression-editor.msync");
        int expressionName = scopeExpression.IndexOf("Instance", StringComparison.Ordinal);
        Require(namedExpression.Occurrences.Any(item => item.Span.Start == expressionName && item.SemanticRole == "type"),
            "A named Scope expression lost its declaration's type colour.");
        Require(namedExpression.Symbols.Single(item => item.Name == "object").Children.Any(item => item.Name == "Instance" && item.Kind == EditorSymbolKind.Scope),
            "A named Scope expression lost its outline identity.");
        int anonymousName = scopeExpression.IndexOf("anonymous", StringComparison.Ordinal);
        Require(namedExpression.Occurrences.Any(item => item.Span.Start == anonymousName && item.SemanticRole == "variable"),
            "An anonymous Scope binding acquired named-Scope type colour.");
        Console.WriteLine("concept, cast, Number, standard namespace and @ editor workflows passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
