using Msync.Compiler;

internal static class EditorLookupChecks
{
    public static void Run()
    {
        string[] fixtures =
        [
            "let ready = true; §",
            "let source = ./first; { let source = ./second; § }; source;",
            "scope Config { file: ./input, remote: ssh://forge, run: fn(x) { x } }; Config::§",
            "fn create() { scope { item: ./input } }; create()::§",
            "fn inspect(file: Path) { let local = scope { useful: true }; §",
            "fn inspect(value) { value::§ }; inspect(scope { common: true, onlyA: ./ }); inspect(scope { common: false, onlyB: ./ });",
            "fn inspect(file: Path) { file/§",
            "fn inspect(process: Process) { process::§"
        ];
        int checkedMembers = 0;
        foreach (string fixture in fixtures)
        {
            int offset = fixture.IndexOf('§');
            string source = fixture.Remove(offset, 1);
            EditorAnalysis analysis = EditorCompiler.Analyze(source, "lookup.msync");
            EditorContext context = analysis.At(offset);
            IReadOnlyList<SemanticMember> members = context.Receiver is not null && context.ReceiverKind == EditorReceiverKind.Member
                ? context.Receiver.Members ?? [] : context.Bindings;
            foreach (SemanticMember member in members)
            {
                Compare(member, analysis.Resolve(offset, member.Name));
                checkedMembers++;
            }
            if (analysis.Resolve(offset, "__missing_binding__") is not null)
                throw new InvalidOperationException("Resolution invented an absent binding or member.");
        }
        if (checkedMembers < 15) throw new InvalidOperationException("The lookup fixtures did not exercise their visible values.");
        Console.WriteLine($"selected semantic lookup matches full lexical/member results ({checkedMembers} values)");
    }

    private static void Compare(SemanticMember expected, SemanticMember? actual)
    {
        if (actual is null || actual.Name != expected.Name || actual.Type != expected.Type || actual.Signature != expected.Signature
            || actual.Description != expected.Description || actual.Endpoint != expected.Endpoint || actual.SemanticRole != expected.SemanticRole
            || actual.KnownPath != expected.KnownPath || actual.KnownText != expected.KnownText)
            throw new InvalidOperationException($"Selected lookup differs for '{expected.Name}'.");
        var expectedMembers = expected.Members ?? [];
        var actualMembers = actual.Members ?? [];
        if (expectedMembers.Count != actualMembers.Count) throw new InvalidOperationException("Selected lookup changed Scope members.");
        foreach (var member in expectedMembers)
            Compare(member, actualMembers.Single(candidate => candidate.Name == member.Name));
    }
}
