using Msync.Compiler;

int checks = 0;
void Require(bool condition, string reason)
{
    checks++;
    if (!condition) throw new InvalidOperationException(reason);
}

SourceDiagnostic Diagnose(string source, string name)
{
    try { _ = Compiler.Analyze(source, name); }
    catch (CompileException error) { return error.Diagnostic; }
    throw new InvalidOperationException($"Expected a semantic error: {name}");
}

foreach (var (name, source, code, help) in new[]
{
    ("nullable", "let value? = if exists ./input { ./file } else { null }; value -> ./out;", "E0203", "Null"),
    ("fixed-storage", "let value = 1; value = true;", "E0203", "Number"),
    ("fixed-field", "let object = scope { item: 1 }; object::item = true;", "E0203", "Number"),
    ("missing-member", "let value = scope { present: 1 }; value::missing;", "E0205", "present"),
    ("unknown-type", "let value: Missing = 1;", "E0202", "concept"),
    ("shadowed-standard", "let std = scope {}; fn inspect(value: $std::concepts::Monoid) { value };", "E0202", "Rename"),
    ("arity", "fn pair(first, second) { first }; pair(1);", "E0204", "first, second"),
    ("match", "match exists ./input { true => { ./a } };", "E0209", "_ =>"),
    ("concept", "concept Named { value: String }; fn take(value: Named) { value }; take(scope { other: 1 });", "E0214", "return types")
})
{
    SourceDiagnostic diagnostic = Diagnose(source, name + ".msync");
    Require(diagnostic.Code == code, $"{name}: {diagnostic.Code}: {diagnostic.Message}");
    Require(!string.IsNullOrEmpty(diagnostic.Label), name + " has a source label");
    Require(diagnostic.Help?.Contains(help, StringComparison.Ordinal) == true,
        $"{name}: useful help absent: {diagnostic.EditorMessage}");
    Require(diagnostic.Source == source && diagnostic.SourceName == name + ".msync"
        && diagnostic.Span.Start + diagnostic.Span.Length <= source.Length, name + " source ownership");
    if (name == "fixed-storage")
    {
        Require(diagnostic.Related.Any(location => location.SourceName == "fixed-storage.msync"
            && location.Source[location.Span.Start..location.Span.End] == "value"
            && location.Message.Contains("fixed storage type Number", StringComparison.Ordinal)), "Fixed storage declaration origin");
        Require(source[diagnostic.Span.Start..diagnostic.Span.End] == "true", "Assignment mismatch points at the assigned value");
    }
    if (name == "concept")
        Require(diagnostic.Related.Any(location => location.Message.Contains("concept 'Named'", StringComparison.Ordinal)), "Concept declaration origin");
    if (name == "fixed-field")
    {
        Require(diagnostic.Related.Any(location => location.Source[location.Span.Start..location.Span.End] == "item"
            && location.Message.Contains("fixed storage type Number", StringComparison.Ordinal)), "Field storage declaration origin");
        Require(source[diagnostic.Span.Start..diagnostic.Span.End] == "true", "Field mismatch points at the assigned value");
    }
    if (name == "shadowed-standard")
        Require(diagnostic.Related.Any(location => location.Source[location.Span.Start..location.Span.End] == "std"), "Shadowing binding declaration origin");
}

const string nullable = "let value = null;";
SourceDiagnostic admission = Diagnose(nullable, "admission.msync");
Require(admission.Fix is { Replacement: "?" }, "Null admission offers a precise fix");
DiagnosticFix fix = admission.Fix!;
string repaired = nullable[..fix.Span.Start] + fix.Replacement + nullable[fix.Span.End..];
_ = Compiler.Analyze(repaired, "repaired.msync");
Require(repaired == "let value? = null;", "Null admission fix edits only the binding name");
const string operand = "fn select(flag) { if flag { 1 } else { \"text\" } }; let number = select(true); number + 2";
SourceDiagnostic arithmetic = Diagnose(operand, "operand.msync");
Require(operand[arithmetic.Span.Start..arithmetic.Span.End] == "number", "Arithmetic error points at the offending operand, not the whole binary expression");
Require(arithmetic.Help?.Contains("value as Number", StringComparison.Ordinal) == true, "Explicit conversion suggestion preserves the numeric target");
Console.WriteLine($"PASS {checks} semantic diagnostic contracts");
