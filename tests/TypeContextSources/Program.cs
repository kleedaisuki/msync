using Msync.Compiler;

string prefix = string.Concat(Enumerable.Repeat("# declaration context\n", 30));
int checks = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    const string sourceName = "source-context.msync";
    string declaration = prefix + """
        concept Named { item: Number, };
        let stored: Named = scope { item: 1 };
        fn preserve(value: Named) -> Named { value };
        """;
    CompilerSession session = new(sourceName, level);
    session.Compile(declaration);
    session.AcceptSubmission();
    var analysis = session.AnalyzeEditor("preserve(scope { item: 2 })");
    Check(analysis.Diagnostic is null, analysis.Diagnostic?.Message ?? "A declaration-owned annotation lost its source.");
    const string assignment = "stored = scope { wrong: true };";
    var failure = Reject(session, assignment, "E0214");
    Check(failure.Diagnostic.Source == assignment, "Stored contract failure did not point at the new assignment source.");
    Check(failure.Diagnostic.Source[failure.Span.Start..failure.Span.End] == "scope { wrong: true }", "Stored contract failure lost the actual assigned expression.");
    Check(failure.Diagnostic.Related.Any(location => location.Source == declaration), "The original contract source was not retained.");
    Check(failure.Diagnostic.Related.All(location => location.Span.Start >= 0 && location.Span.End <= location.Source.Length),
        "A related diagnostic paired a source with another document's span.");
    session.Compile("stored = scope { item: 3 };");
    checks++;

    foreach (string function in new[]
    {
        "fn invalid(value: Missing) { value };",
        "fn invalid(value: Number) -> Missing { value };",
    })
    {
        string invalidDeclaration = prefix + function;
        CompilerSession invalid = new(sourceName, level);
        invalid.Compile(invalidDeclaration);
        invalid.AcceptSubmission();
        var unknown = Reject(invalid, "invalid(1)", "E0202");
        Check(unknown.Diagnostic.Source == invalidDeclaration, "An unknown annotation was attributed to its caller instead of its declaration.");
        Check(unknown.Diagnostic.Source[unknown.Span.Start..unknown.Span.End] == "Missing", "Unknown annotation source span is not its actual type name.");
    }
}
Console.WriteLine($"PASS {checks} declaration/use-site source ownership checks");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

static CompileException Reject(CompilerSession session, string source, string code)
{
    try { session.Compile(source); }
    catch (CompileException error) when (error.Diagnostic.Code == code) { return error; }
    throw new InvalidOperationException($"Expected {code} for: {source}");
}
