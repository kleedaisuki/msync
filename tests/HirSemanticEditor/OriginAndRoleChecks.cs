using Msync.Compiler;

internal static class OriginAndRoleChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string header = """
            scope SomeClass {
                new: fn -> Scope { scope { item: ~/.codex/AGENTS.md } },
                copy: fn(where: Connection, object: Scope) { where @ object::item -> ./ }
            };
            """;
        foreach (bool rebuilt in new[] { false, true })
        {
            string tail = rebuilt ? "scope { item: some::item }" : "some";
            string source = header + "let object? = { let some = SomeClass::new(); if exists some::item { "
                + tail + " } }; if not object? { SomeClass::copy($wsl, object) };";
            EditorAnalysis analysis = EditorCompiler.Analyze(source, "nullable-origins.msync");
            require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Nullable origin analysis failed");
            int use = source.IndexOf("object::item", StringComparison.Ordinal) + "object::".Length;
            int definition = rebuilt ? source.LastIndexOf("item:", StringComparison.Ordinal)
                : source.IndexOf("item:", StringComparison.Ordinal);
            require(analysis.DefinitionsAt(use).Any(target => target.SourceName == "nullable-origins.msync"
                && target.Span.Start == definition && target.Source[target.Span.Start..target.Span.End] == "item"),
                "A nullable constructed Scope retains the actual field declaration through formal inputs");
        }

        const string distinct = """
            fn identity(object: Scope) -> Scope { object };
            let first = scope { item: ./first };
            let second = scope { item: ./second };
            let left = identity(first);
            let right = identity(second);
            left::item;
            right::item;
            """;
        EditorAnalysis origins = EditorCompiler.Analyze(distinct, "distinct-origins.msync");
        require(origins.Diagnostic is null, origins.Diagnostic?.Message ?? "Distinct origin analysis failed");
        foreach ((string binding, int definition) in new[]
        {
            ("left", distinct.IndexOf("item:", StringComparison.Ordinal)),
            ("right", distinct.LastIndexOf("item:", StringComparison.Ordinal))
        })
        {
            int use = distinct.IndexOf(binding + "::item", StringComparison.Ordinal) + binding.Length + 2;
            require(origins.DefinitionsAt(use).Single().Span.Start == definition,
                "A reused same-shape specialization substitutes each actual caller's field origin");
        }

        const string queries = """
            let arithmetic = fn(value: Number) -> Number { value + 1 };
            let option = $std::data::Option::some("Klee");
            let inferred = $std::meta::invoke_result(arithmetic, Number);
            let capable = $std::meta::is_invocable(arithmetic, Number);
            """;
        EditorAnalysis roles = EditorCompiler.Analyze(queries, "pure-query-roles.msync");
        require(roles.Diagnostic is null, roles.Diagnostic?.Message ?? "Pure-query role analysis failed");
        foreach (string module in new[] { "data", "meta" })
        {
            for (int use = queries.IndexOf("$std::" + module, StringComparison.Ordinal); use >= 0;
                use = queries.IndexOf("$std::" + module, use + 1, StringComparison.Ordinal))
            {
                require(roles.Occurrences.Any(item => item.Span.Start == use + 1 && item.Span.Length == 3
                    && item.SemanticRole == "type"), "An optimized standard reference retains its root type role");
                require(roles.Occurrences.Any(item => item.Span.Start == use + "$std::".Length
                    && item.Span.Length == module.Length && item.SemanticRole == "type"),
                    "Named standard modules remain types even when a pure query skips executable callee analysis");
            }
        }
        foreach (string function in new[] { "invoke_result", "is_invocable" })
            require(roles.Occurrences.Any(item => item.Span.Start == queries.IndexOf(function, StringComparison.Ordinal)
                && item.SemanticRole == "function"), "A pure metadata query retains native function presentation");
        var query = roles.Occurrences.Single(item => item.Span.Start == queries.IndexOf("is_invocable", StringComparison.Ordinal));
        require(query.Signature.Contains("...arguments", StringComparison.Ordinal)
            && query.Description.Contains("without executing", StringComparison.Ordinal),
            "Pure-query hover comes from the native declaration's actual signature and documentation");
    }
}
