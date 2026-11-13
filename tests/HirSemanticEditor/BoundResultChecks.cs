using Msync.Compiler;

internal static class BoundResultChecks
{
    public static void Run(Action<bool, string> require)
    {
        foreach (string result in new[]
        {
            "scope { item: 7 }",
            "if parameter { scope { item: 7 } } else { scope { item: 8 } }"
        })
        {
            string source = "fn factory(parameter) { " + result
                + " }; fn draft(argument) { let object = factory(argument); object::item; object::";
            EditorAnalysis analysis = EditorCompiler.Analyze(source, "bound-result.msync");
            SemanticMember? field = analysis.At(source.Length).Receiver?.Members?.SingleOrDefault(item => item.Name == "item");
            require(field?.Type == "Number", "An uncalled return retains field completion: " + result);
            int selected = source.LastIndexOf("object::item", StringComparison.Ordinal) + "object::".Length;
            int[] expected = Enumerable.Range(0, source.Length)
                .Where(index => source.AsSpan(index).StartsWith("item:", StringComparison.Ordinal)).ToArray();
            int[] actual = analysis.DefinitionsAt(selected).Select(target => target.Span.Start).Order().ToArray();
            require(actual.SequenceEqual(expected), "All uncalled return alternatives retain exact field origins: " + result);
            require(analysis.At(source.Length).Bindings.Any(item => item.Name == "argument" && item.Type == "unknown"),
                "Querying an uncalled return does not invent a concrete invocation");
        }

        foreach ((string declaration, string callee) in new[]
        {
            ("fn factory() { scope { item: 7 } };", "factory"),
            ("let factory = fn { scope { item: 7 } };", "factory"),
            ("let original = fn { scope { item: 7 } }; let factory = original;", "factory"),
            ("let Factory = scope { make: fn { scope { item: 7 } } };", "Factory::make")
        })
        {
            string source = declaration + " fn draft(argument) { let object = " + callee + "(); object::item; object::";
            EditorAnalysis analysis = EditorCompiler.Analyze(source, "bound-callee.msync");
            require(analysis.At(source.Length).Receiver?.Members?.SingleOrDefault(item => item.Name == "item")?.Type == "Number",
                "Uncalled factory completion follows the actual callee identity: " + declaration);
            int selected = source.LastIndexOf("object::item", StringComparison.Ordinal) + "object::".Length;
            require(analysis.DefinitionsAt(selected).Single().Span.Start == source.IndexOf("item:", StringComparison.Ordinal),
                "An aliased factory return keeps its original field declaration");
        }

        const string values = "fn identity(value) { value }; fn draft(argument) { "
            + "let first = identity(\"first\"); let second = identity(\"second\"); first; second;";
        EditorAnalysis actuals = EditorCompiler.Analyze(values, "bound-actuals.msync");
        require(actuals.Resolve(values.Length, "first")?.KnownText == "first"
            && actuals.Resolve(values.Length, "second")?.KnownText == "second",
            "A cached type scheme cannot replace either actual call's literal payload");

        const string returned = "fn factory() { return scope { item: 7 }; }; fn draft(argument) { factory()::";
        require(EditorCompiler.Analyze(returned, "bound-explicit-return.msync").At(returned.Length)
            .Receiver?.Members?.SingleOrDefault(item => item.Name == "item")?.Type == "Number",
            "A terminating body contributes its explicit return rather than an invented fallthrough value");

        foreach (string expression in new[] { "folder", "folder/\"nested\"", "(ssh://forge @ folder)" })
        {
            string source = "let folder = ./kept; fn draft(argument) { " + expression + "/";
            var receiver = EditorCompiler.Analyze(source, "bound-path.msync").At(source.Length).Receiver;
            require(receiver?.KnownPath is { } known && known.Raw.Contains("kept", StringComparison.Ordinal),
                "Uncalled path metadata follows the shared partial transfer: " + expression);
        }
        const string mutable = "let folder = ./kept; folder = ./changed; fn draft(argument) { folder/";
        require(EditorCompiler.Analyze(mutable, "bound-mutable-path.msync").At(mutable.Length).Receiver?.KnownPath is null,
            "An uncalled mutable capture does not invent its initializer's physical path");

        const string path = "let folder = ssh://forge @ ./kept; fn draft(argument) { folder/";
        EditorAnalysis endpoint = EditorCompiler.Analyze(path, "bound-path.msync");
        require(endpoint.At(path.Length).Receiver?.KnownPath is { Raw: "./kept", Connection.Address: "forge" },
            "Uncalled path completion retains raw spelling and endpoint without opening a connection");
    }
}
