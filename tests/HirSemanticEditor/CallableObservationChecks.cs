using Msync.Compiler;

internal static class CallableObservationChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string source = """
            let selected = if exists ./condition {
                fn(value: Number) -> Number { value }
            } else {
                fn(text: String) -> String { text }
            };
            selected;
            let optional? = if exists ./other { selected } else { null };
            if not optional? { optional; };
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "callable-choices.msync");
        require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Callable analysis failed");
        SemanticMember? selected = analysis.Resolve(source.IndexOf("selected;", StringComparison.Ordinal), "selected");
        require(selected is { Type: "Function", SemanticRole: "function" },
            "Finite callable choices retain function presentation");
        require(selected?.Signature.Contains("fn selected(value: Number) -> Number", StringComparison.Ordinal) == true
            && selected.Signature.Contains("fn selected(text: String) -> String", StringComparison.Ordinal),
            "Both source callable signatures survive the semantic join");
        SemanticMember? narrowed = analysis.Resolve(source.LastIndexOf("optional;", StringComparison.Ordinal), "optional");
        require(narrowed?.Type == "Function"
            && narrowed.Signature.Contains("value: Number", StringComparison.Ordinal)
            && narrowed.Signature.Contains("text: String", StringComparison.Ordinal),
            "Null narrowing preserves all finite callable candidates");

        const string native = """
            let reader = if exists ./condition { $std::io::read_text } else { $std::io::read_lines };
            reader;
            """;
        EditorAnalysis natives = EditorCompiler.Analyze(native, "native-choices.msync");
        require(natives.Diagnostic is null, natives.Diagnostic?.Message ?? "Native choices failed");
        SemanticMember? reader = natives.Resolve(native.Length, "reader");
        string[] signatures = reader?.Signature.Split('\n') ?? [];
        require(signatures.Length == 2 && signatures.All(signature =>
                signature.StartsWith("fn reader(path: ", StringComparison.Ordinal)
                && signature.Split(')')[0].Contains("Path", StringComparison.Ordinal)
                && signature.Split(')')[0].Contains("String", StringComparison.Ordinal))
            && signatures.Any(signature => signature.EndsWith("-> String", StringComparison.Ordinal))
            && signatures.Any(signature => signature.EndsWith("-> Range<String>", StringComparison.Ordinal)),
            "Native callable choices retain parameter names and distinct result signatures: " + reader?.Signature);
    }
}
