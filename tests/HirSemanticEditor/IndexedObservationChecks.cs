using Msync.Compiler;

internal static class IndexedObservationChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string source = """
            let items = $std::storage::vector_empty(Number);
            let alias = items;
            let holder = object { items: alias };
            let frozen = $std::storage::vector_snapshot(items);
            items;
            $std::storage::vector_push(alias, 42);
            items;
            alias;
            holder;
            frozen;
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "indexed-observations.msync");
        require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Indexed analysis failed");
        int before = source.IndexOf("items;", source.IndexOf("let frozen", StringComparison.Ordinal), StringComparison.Ordinal);
        int after = source.LastIndexOf("items;", StringComparison.Ordinal);
        string? original = analysis.OccurrenceAt(before)?.Type;
        require(analysis.Resolve(after, "items")?.Type == "Storage<Number>",
            "A vector write retains its fixed carrier");
        require(analysis.Resolve(after, "items")?.Signature == "items: Storage<Number>",
            "The hover signature distinguishes an owner from a Range");
        require(analysis.Resolve(after, "alias")?.Type == "Storage<Number>",
            "An owner alias retains the resource carrier");
        require(analysis.Resolve(after, "holder")?.Members?.Single(member => member.Name == "items").Type == "Storage<Number>",
            "A vector nested in an Object retains its carrier");
        require(original == "Storage<Number>",
            $"An empty vector has its carrier before its first write, found '{original}'");
        require(analysis.Resolve(source.Length, "frozen")?.Type == "Range<Number>",
            "A membership snapshot is a Range with the owner's element type");

        const string alternatives = """
            let left = $std::storage::vector_empty(Number);
            let right = $std::storage::vector_empty(Number);
            let selected = if exists ./condition { left } else { right };
            $std::storage::vector_push(selected, 42);
            selected;
            """;
        EditorAnalysis joined = EditorCompiler.Analyze(alternatives, "indexed-alternatives.msync");
        require(joined.Diagnostic is null, joined.Diagnostic?.Message ?? "Indexed alternatives failed");
        require(joined.Resolve(alternatives.Length, "selected")?.Type == "Storage<Number>",
            "A joined owner derives its element from every reachable indexed resource");

        const string differentCarriers = """
            let left = $std::storage::vector_empty(Number);
            let right = $std::storage::vector_empty(String);
            let selected = if exists ./condition { left } else { right };
            selected;
            """;
        EditorAnalysis mixed = EditorCompiler.Analyze(differentCarriers, "indexed-carrier-union.msync");
        require(mixed.Diagnostic is null, mixed.Diagnostic?.Message ?? "Mixed indexed carriers failed");
        string? joinedCarrier = mixed.Resolve(differentCarriers.Length, "selected")?.Type;
        require(joinedCarrier is not null && joinedCarrier.StartsWith("Storage<", StringComparison.Ordinal)
            && joinedCarrier.Contains("Number", StringComparison.Ordinal)
            && joinedCarrier.Contains("String", StringComparison.Ordinal),
            $"Every fixed carrier contributes to the empty-owner union, found '{joinedCarrier}'");

        const string nullableCarrier = """
            let values = $std::storage::vector_empty($std::meta::add_null(Number));
            $std::storage::vector_push(values, 42);
            values;
            """;
        EditorAnalysis nullable = EditorCompiler.Analyze(nullableCarrier, "indexed-nullable-carrier.msync");
        require(nullable.Diagnostic is null, nullable.Diagnostic?.Message ?? "Nullable indexed carrier failed");
        require(nullable.Resolve(nullableCarrier.Length, "values")?.Type == "Storage<Number?>",
            "A non-null write must not erase Null admission from the owner's fixed carrier");

        const string function = """
            fn build() {
                let owner = $std::storage::vector_empty(Number);
                $std::storage::vector_push(owner, 42);
                owner
            };
            let returned = build();
            returned;
            """;
        EditorAnalysis callable = EditorCompiler.Analyze(function, "indexed-function.msync");
        require(callable.Diagnostic is null, callable.Diagnostic?.Message ?? "Indexed function failed");
        require(callable.Resolve(function.Length, "returned")?.Type == "Storage<Number>",
            "A vector returned from a source function retains its indexed metadata");
    }
}
