using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class SessionDescriptionChecks
{
    internal static async Task Run()
    {
        int checks = 0;
        CompilerSession compiler = new();
        await using ExecutionSession execution = new();
        await Submit("""
            concept HasPath { path: Path, };
            scope Named {
                path: ./named,
                make: fn { scope { field?: null } },
                get: fn(item: Path | String) -> Path { item as Path },
            };
            let object = scope { text: "before", path: ./before, remote: ssh://forge };
            let optional?: String = "before";
            fn identity(item) { item };
            let values = $std::storage::Buffer::new([1]);
            """);
        Compare("initial named/anonymous/callable/concept/indexed descriptions");
        await Submit("object::text = \"after\"; object::path = ./after; optional = null; $std::storage::Buffer::push(values, 2);");
        Compare("runtime field/path/null/indexed feedback");
        await Submit("let object = scope { selected: true }; identity(1); identity(\"text\");");
        Compare("shadowed selected exports and generic signatures");
        string before = Fingerprint(compiler.DescribeBindings());
        EditorAnalysis rejected = compiler.AnalyzeEditor("let query_only: Number = true;", execution.GetLocalValue);
        Check(rejected.Diagnostic is not null, "The rejected query did not produce its type diagnostic.");
        Check(Fingerprint(compiler.DescribeBindings()) == before,
            "An editor query changed the cached accepted description.");
        Check(!compiler.BindingNames.Contains("query_only"), "An editor-only binding leaked into session exports.");
        Console.WriteLine($"PASS {checks} direct accepted-description equivalence checks");

        async Task Submit(string source)
        {
            Module module = compiler.Compile(source);
            await execution.ExecuteAsync(module);
            compiler.AcceptSubmission(execution.GetLocalValue);
        }

        void Compare(string label)
        {
            EditorAnalysis analyzed = compiler.AnalyzeEditor("");
            Check(analyzed.Diagnostic is null, label + ": empty query failed");
            Check(Fingerprint(compiler.DescribeBindings()) == Fingerprint(analyzed.At(0).Bindings),
                label + ": direct accepted roots differ from the existing editor query");
            const string typePosition = "let probe: ";
            var concepts = compiler.DescribeConcepts();
            var queriedConcepts = compiler.AnalyzeEditor(typePosition).At(typePosition.Length).Bindings
                .Where(member => member.IsConstraint && concepts.Any(concept => concept.Name == member.Name)).ToArray();
            Check(Fingerprint(concepts) == Fingerprint(queriedConcepts),
                label + ": visible concept descriptions differ");
        }

        void Check(bool valid, string label)
        {
            if (!valid) throw new InvalidOperationException(label);
            checks++;
        }
    }

    private static string Fingerprint(IReadOnlyList<SemanticMember> members) => string.Join('\n',
        members.OrderBy(member => member.Name, StringComparer.Ordinal).Select(member =>
            string.Join("|", member.Name, member.Type, member.Signature, member.Description,
                member.SemanticRole, member.IsConstraint, member.Endpoint?.Kind, member.Endpoint?.Address,
                member.KnownText, member.KnownPath?.Raw, member.KnownPath?.WorkingDirectory,
                member.KnownPath?.HomeDirectory, member.KnownPath?.LocalSpelling,
                member.KnownPath?.Connection?.Kind, member.KnownPath?.Connection?.Address)
            + "\n{" + Fingerprint(member.Members ?? []) + "}"));
}
