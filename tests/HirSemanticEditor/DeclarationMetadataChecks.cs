using Msync.Compiler;

internal static class DeclarationMetadataChecks
{
    public static void Run(Action<bool, string> require)
    {
        const string source = """
            let with_equality = $std::containers::EqualitySet::with_equality;
            scope Handlers {
                # Preserve this explicitly chosen equivalence policy.
                preserve: fn(policy: $std::concepts::Equality?) -> $std::concepts::Equality? { policy },
            };
            let documented = Handlers::preserve;
            concept Product {
                first?: typename First,
                convert: fn(value: Range<First?>) -> First,
            };
            fn wrap(value: Product?) -> Product? { value };
            fn inspect(policy: Product) { policy::convert };
            let wrapped = wrap;
            let read = $std::io::read_text;
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "declaration-metadata.msync");
        require(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "Declaration metadata analysis failed");
        SemanticMember? equality = analysis.Resolve(source.Length, "with_equality");
        require(equality?.Signature.Contains("equality: $std::concepts::Equality", StringComparison.Ordinal) == true,
            "Source callable signatures retain named structural concept constraints");
        require(equality?.Description.Contains("Equivalence relation", StringComparison.Ordinal) == true
            && equality.Description.Contains("equal:", StringComparison.Ordinal),
            "Constrained callable help includes the actual concept documentation and required operation");
        SemanticMember? documented = analysis.Resolve(source.Length, "documented");
        require(documented?.Signature.Contains("$std::concepts::Equality?", StringComparison.Ordinal) == true,
            "Source aliases retain nullable concept names in their signature");
        require(documented?.Description.Contains("Preserve this explicitly chosen", StringComparison.Ordinal) == true,
            "Callable documentation belongs to the owning field declaration rather than the fn token");
        require(analysis.Resolve(source.Length, "wrapped")?.Signature.Contains("Product?", StringComparison.Ordinal) == true,
            "Local concepts retain their lexical name rather than acquiring a standard namespace");
        int member = source.IndexOf("policy::convert", StringComparison.Ordinal) + "policy::".Length;
        SemanticMember? convert = analysis.At(member).Receiver?.Members?.Single(item => item.Name == "convert");
        require(convert?.Signature.Contains("Range<First?>", StringComparison.Ordinal) == true
            && convert.Signature.EndsWith("-> First", StringComparison.Ordinal),
            "Associated typename binders survive nested nullable Range and callable signature projection");
        SemanticMember? native = analysis.Resolve(source.Length, "read");
        require(native?.Signature.StartsWith("fn read(path:", StringComparison.Ordinal) == true
            && native.Signature.EndsWith("-> String", StringComparison.Ordinal)
            && native.Description.Contains("UTF-8", StringComparison.Ordinal),
            "Native aliases keep their declared parameter names, result and source documentation");
    }
}
