using Msync.Compiler;

internal static class ObservationChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string aliases = """
            let inner = scope { value: "before" };
            let outer = scope { child: inner };
            let alias = outer;
            alias;
            inner::value = "after";
            alias;
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(aliases, "alias-points.msync");
        require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Alias analysis failed");
        int before = aliases.IndexOf("alias;", StringComparison.Ordinal);
        int after = aliases.LastIndexOf("alias;", StringComparison.Ordinal);
        require(Payload(analysis.Resolve(before, "alias"))?.KnownText == "before",
            "Earlier semantic points retain their nested object value");
        require(Payload(analysis.Resolve(after, "alias"))?.KnownText == "after",
            "A nested region write refreshes every referencing Scope alias");

        const string shadows = "let value = ./original; { let value = \"nested\"; value; }; value;";
        EditorAnalysis shadow = EditorCompiler.Analyze(shadows, "shadow-points.msync");
        int inner = shadows.IndexOf("value;", StringComparison.Ordinal);
        require(shadow.Resolve(inner, "value") is { Type: "String", KnownText: "nested" },
            "An inner lexical name joins its own location facts");
        require(shadow.Resolve(shadows.LastIndexOf("value;", StringComparison.Ordinal), "value")?.Type == "Path",
            "Leaving an inner lexical scope restores the outer declaration identity");

        Parallel.For(0, 64, _ =>
        {
            if (Payload(analysis.At(after).Bindings.Single(item => item.Name == "alias"))?.KnownText != "after"
                || Payload(analysis.Resolve(before, "alias"))?.KnownText != "before")
                throw new InvalidOperationException("Concurrent queries mutated shared presentation state.");
        });
        require(true, "Concurrent completion and resolve queries use independent presentation state");
    }

    private static SemanticMember? Payload(SemanticMember? value) =>
        value?.Members?.Single(item => item.Name == "child").Members?.Single(item => item.Name == "value");
}
