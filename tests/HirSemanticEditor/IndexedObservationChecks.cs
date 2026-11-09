using Msync.Compiler;

internal static class IndexedObservationChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string source = """
            let items = $std::storage::Buffer::new([]);
            let alias = items;
            let holder = scope { items: alias };
            let frozen = $std::storage::Buffer::snapshot(items);
            items;
            $std::storage::Buffer::push(alias, 42);
            items;
            alias;
            holder;
            frozen;
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "indexed-observations.msync");
        require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Indexed analysis failed");
        int before = source.IndexOf("items;", source.IndexOf("let frozen", StringComparison.Ordinal), StringComparison.Ordinal);
        int after = source.LastIndexOf("items;", StringComparison.Ordinal);
        string? original = analysis.Resolve(before, "items")?.Type;
        require(analysis.Resolve(after, "items")?.Type == "Range<Number>",
            "A Buffer first write refreshes its live element type");
        require(analysis.Resolve(after, "items")?.Signature == "items: Range<Number>",
            "The current indexed element type is reflected in the hover signature");
        require(analysis.Resolve(after, "alias")?.Type == "Range<Number>",
            "An ordinary Buffer alias observes the current resource carrier");
        require(analysis.Resolve(after, "holder")?.Members?.Single(member => member.Name == "items").Type == "Range<Number>",
            "A Buffer reachable through a Scope is refreshed without rewriting the Scope row");
        require(analysis.Resolve(before, "items")?.Type == original && original != "Range<Number>",
            "A later indexed write does not alter earlier source-point facts");
        require(analysis.Resolve(source.Length, "frozen")?.Type == original,
            "A membership snapshot retains its earlier element fact");

        const string alternatives = """
            let left = $std::storage::Buffer::new([]);
            let right = $std::storage::Buffer::new([]);
            let selected = if exists ./condition { left } else { right };
            $std::storage::Buffer::push(selected, 42);
            selected;
            """;
        EditorAnalysis joined = EditorCompiler.Analyze(alternatives, "indexed-alternatives.msync");
        require(joined.Diagnostic is null, joined.Diagnostic?.Message ?? "Indexed alternatives failed");
        require(joined.Resolve(alternatives.Length, "selected")?.Type == "Range<Number>",
            "A joined Buffer value derives its element from every reachable indexed resource");

        const string function = """
            fn build() {
                let buffer = $std::storage::Buffer::new([]);
                $std::storage::Buffer::push(buffer, 42);
                buffer
            };
            let returned = build();
            returned;
            """;
        EditorAnalysis callable = EditorCompiler.Analyze(function, "indexed-function.msync");
        require(callable.Diagnostic is null, callable.Diagnostic?.Message ?? "Indexed function failed");
        require(callable.Resolve(function.Length, "returned")?.Type == "Range<Number>",
            "A Buffer returned from a source function retains current indexed metadata");
    }
}
