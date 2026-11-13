using Msync.Compiler.Semantics;

internal static class AliasEffectChecks
{
    internal static int Run()
    {
        int before = AliasFamilies(0);
        foreach (int calls in new[] { 1, 2, 8 })
            if (AliasFamilies(calls) != before)
                throw new InvalidOperationException("A read-only invocation replayed input aliases as newly allocated effects.");
        return 3;
    }

    private static int AliasFamilies(int calls)
    {
        string source = """
            let a = scope { n: 1 };
            let b = scope { n: 2 };
            fn inspect(view, original) { original::n };
            let view? = $std::ranges::first([a, b]);
            if view is not null {
            """ + string.Join(';', Enumerable.Repeat("inspect(view, a)", calls)) + "; }; 0";
        BoundProgram program = new SourceBinder().Bind(source, "alias-effects.msync");
        SemanticAnalyzer analyzer = new(program);
        _ = analyzer.Analyze();
        return analyzer.FinalState.AliasViews.SelectMany(pair => pair.Value.Append(pair.Key)).Distinct().Count();
    }
}
