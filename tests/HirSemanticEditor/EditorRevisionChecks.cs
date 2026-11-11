using Msync.Compiler;

internal static class EditorRevisionChecks
{
    internal static void Run(Action<bool, string> require)
    {
        const string name = "file:///editor-revisions.msync";
        const string prefix = "fn wrap(value) { scope { item: value } };let item = wrap(7);";
        EditorAnalysisSession session = new(name);
        string text = prefix + "let selected = item::item;";
        _ = session.Analyze(text);
        var cosmetic = "  \r\n" + prefix.Replace(";let", ";\n\tlet", StringComparison.Ordinal)
            + "\n  let selected = item::item;\n";
        var rebased = session.Analyze(cosmetic);
        Compare(rebased, cosmetic);
        int between = cosmetic.IndexOf("\t", StringComparison.Ordinal);
        require(rebased.At(between).Bindings.Any(member => member.Name == "wrap"), "Gap after the declaration retains its binding");
        int member = cosmetic.LastIndexOf("item", StringComparison.Ordinal);
        require(rebased.DefinitionsAt(member).Any(location => location.Source == cosmetic
            && cosmetic.AsSpan(location.Span.Start, location.Span.Length).SequenceEqual("item")), "Rebased member definition uses current source");

        // The same lexical cut serves several real type-checked suffix edits.
        _ = session.Analyze(text);
        string changed = prefix + "let selected = item::item + 1;";
        var first = session.Analyze(changed);
        Compare(first, changed);
        string next = prefix + "let selected = item::item + 2;";
        var second = session.Analyze(next);
        Compare(second, next);
        require(ReferenceEquals(first.Occurrences[0], second.Occurrences[0]),
            "A real suffix edit reuses the accepted prefix projection");
        require(second.DefinitionsAt(next.LastIndexOf("item", StringComparison.Ordinal))
            .Any(location => location.Source == next), "A suffix use navigates to the exact current document");

        string specialization = prefix + "let selected = wrap(\"text\")::item;";
        var specialized = session.Analyze(specialization);
        Compare(specialized, specialization);
        require(specialized.Resolve(specialization.Length, "selected")?.Type == "String",
            "A newly needed prefix specialization invalidates reuse");
        string mutation = prefix + "item::item = \"wrong\";";
        var rejected = session.Analyze(mutation);
        Compare(rejected, mutation);
        require(rejected.Diagnostic is not null, "Mutating suffix uses normal fixed-storage analysis");

        string incomplete = prefix + "let selected = item::";
        EditorAnalysis active = session.Analyze(incomplete);
        Compare(active, incomplete);
        EditorAnalysisSession path = new(name);
        _ = path.Analyze("let file = first name;");
        const string widePath = "let file = first  name;";
        var widened = path.Analyze(widePath);
        require(widened.Resolve(widePath.Length, "file")?.KnownPath?.Raw == "first  name",
            "Whitespace inside a bare Path is semantic data, not a reusable cosmetic change");

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        try
        {
            _ = session.Analyze("let unrelated = true;", cancelled.Token);
            require(false, "A superseded analysis must not complete");
        }
        catch (OperationCanceledException)
        {
            require(ReferenceEquals(active, session.Analyze(incomplete)),
                "Cancellation does not replace current source state");
        }

        void Compare(EditorAnalysis actual, string source)
        {
            EditorAnalysis fresh = EditorCompiler.Analyze(source, name);
            require(actual.Diagnostic?.Message == fresh.Diagnostic?.Message, "Revision and full analysis agree on diagnostics");
            require(actual.Occurrences.Select(Key).SequenceEqual(fresh.Occurrences.Select(Key)),
                "Revision and full analysis agree on source roles and signatures");
            require(actual.TokenRoles().OrderBy(RoleKey).SequenceEqual(fresh.TokenRoles().OrderBy(RoleKey)),
                "Revision and full analysis agree on highlighting");
            var expectedRoles = actual.Occurrences.Where(value => value.SemanticRole is "function" or "type" || value.Type is "Scope" or "Scope?")
                .Select(value => new EditorTokenRole(value.Span, value.SemanticRole)).OrderBy(RoleKey);
            require(actual.TokenRoles().OrderBy(RoleKey).SequenceEqual(expectedRoles),
                "Lightweight highlighting agrees with detailed presentation");
            require(actual.At(source.Length).Bindings.Select(member => (member.Name, member.Type, member.Signature)).Order()
                .SequenceEqual(fresh.At(source.Length).Bindings.Select(member => (member.Name, member.Type, member.Signature)).Order()),
                "Revision and full analysis expose identical EOF completion");
        }

        static (int, int, string, string, string) Key(SemanticOccurrence value) =>
            (value.Span.Start, value.Span.Length, value.Type, value.SemanticRole, value.Signature);
        static (int, int, string) RoleKey(EditorTokenRole role) => (role.Span.Start, role.Span.Length, role.Role);
    }
}
