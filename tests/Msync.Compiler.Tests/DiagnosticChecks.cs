using Msync.Compiler;
using Msync.Compiler.Syntax;
using Frontend = Msync.Compiler.Compiler;

internal static class DiagnosticChecks
{
    public static void Run()
    {
        NullableFix();
        SyntaxGuidance();
        GenericContext();
        EditorTypes();
        UncalledDefinitions();
        Console.WriteLine("structured diagnostics and inferred editor insight ok");
    }

    private static void NullableFix()
    {
        const string source = "let file = if exists ~/input { ~/input } else { null };";
        SourceDiagnostic diagnostic = Reject(source);
        Check(diagnostic.Code == "E0201" && diagnostic.Label!.Contains("Null", StringComparison.Ordinal), "nullable value lost its diagnostic category");
        Check(diagnostic.Fix is not null, "nullable admission has no source edit");
        var fix = diagnostic.Fix!;
        Check(source[..fix.Span.Start] == "let file", "nullable fix targets the expression rather than the binding");
        _ = Frontend.Compile(source[..fix.Span.Start] + fix.Replacement + source[fix.Span.End..]);
        SourceDiagnostic comparison = Reject("null == null");
        Check(comparison.Code == "E0201" && comparison.Help!.Contains("is null", StringComparison.Ordinal), "Null comparison lacks actionable help");
        SourceDiagnostic condition = Reject("if ~/input { true }");
        Check(condition.Code == "E0203" && condition.Help!.Contains("exists path", StringComparison.Ordinal), "Path condition lacks a real Bool-producing alternative");
        SourceDiagnostic match = Reject("match exists ~/input { true => { true } }");
        Check(match.Code == "E0209" && match.Label!.Contains("false", StringComparison.Ordinal), "match diagnostic does not name the missing Bool case");
    }

    private static void SyntaxGuidance()
    {
        const string missing = "let value = true\nlet next = false;";
        SourceDiagnostic diagnostic = Reject(missing);
        Check(diagnostic.Code == "E0102" && diagnostic.Fix?.Replacement == ";", "missing separator lacks its exact edit");
        var fix = diagnostic.Fix!;
        _ = Frontend.Compile(missing[..fix.Span.Start] + fix.Replacement + missing[fix.Span.End..]);
        SourceDiagnostic open = Reject("fn test() {\n  true");
        Check(open.Code == "E0103" && open.Related.Single().Span.Start == 10, "unclosed block lost its opening delimiter");
        SourceDiagnostic literal = Reject("let text = \"hello");
        Check(literal.Code == "E0104" && literal.Help!.Contains("two consecutive quotes", StringComparison.Ordinal), "unterminated String lacks escaping guidance");
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            string source = "# first" + newline + "let value = null;";
            SourceDiagnostic position = Reject(source);
            Check(position.Position == (2, 13), "diagnostic coordinates disagree for a supported line ending");
        }
    }

    private static void GenericContext()
    {
        const string source = """
            scope MyClass {
                new: fn() { scope { x: ~/.codex } },
                get: fn(some) { some::x }
            };
            let my_instance = MyClass::new()::x;
            MyClass::get(my_instance) -> ./;
            """;
        SourceDiagnostic diagnostic = Reject(source);
        Check(diagnostic.Code == "E0206", "member selection still reports a generic Connection type mismatch");
        Check(source[diagnostic.Span.Start..diagnostic.Span.End] == "some", "primary member diagnostic does not point at the invalid receiver");
        Check(diagnostic.Help!.Contains("Scope itself", StringComparison.Ordinal), "member error does not distinguish a Scope from its Path member");
        Check(diagnostic.Related.Any(item => item.Message.Contains("some: Path", StringComparison.Ordinal)
            && source[item.Span.Start..item.Span.End] == "MyClass::get(my_instance)"), "generic failure lost the actual typed call site");
        SourceDiagnostic annotation = Reject("fn only(value: Bool) { value }; only(~/file);");
        Check(annotation.Code == "E0203" && annotation.Related.Single().Message.Contains("value", StringComparison.Ordinal), "typed argument mismatch lost its parameter declaration");
        Check(annotation.Source[annotation.Span.Start..annotation.Span.End] == "~/file", "argument mismatch underlines the whole call instead of the argument");
    }

    private static void EditorTypes()
    {
        const string source = """
            scope MyClass {
                new: fn() { scope { x: ~/.codex } },
                get: fn(some) { some::x }
            };
            let construct = MyClass::new;
            let instance = construct();
            let ClassAlias = MyClass;
            MyClass::get(instance) -> ./;
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///test.msync");
        Check(analysis.Diagnostic is null, "valid screenshot sample does not compile");
        SemanticOccurrence Find(string text, int from = 0)
        {
            int start = source.IndexOf(text, from, StringComparison.Ordinal);
            return analysis.Occurrences.Single(item => item.Span.Start == start && item.Span.Length == text.Length);
        }
        SemanticOccurrence scope = Find("MyClass");
        Check(scope.Type == "Scope" && scope.Members!.Select(item => item.Name).SequenceEqual(new[] { "new", "get" }), "Scope hover lacks actual declared members");
        Check(Find("construct").Type == "Function" && Find("ClassAlias").Type == "Scope", "aliases lost semantic roles");
        Check(Find("instance").Type == "Scope", "constructor result is not inferred as Scope");
        SemanticOccurrence get = Find("get");
        Check(get.Signature == "fn get(some: Scope) -> Path", "declaration hover is stale after later specialization: " + get.Signature);
        Check(Find("new").Signature.EndsWith("-> Scope", StringComparison.Ordinal), "constructor declaration result was not refreshed");
        int called = source.IndexOf("MyClass::get(instance)", StringComparison.Ordinal);
        Check(Find("get", called).Type == "Function", "selected member occurrence is not an exact Function token");
        const string generic = "fn identity(value) { value }; identity(true); identity(\"text\");";
        EditorAnalysis polymorphic = EditorCompiler.Analyze(generic, "file:///generic.msync");
        var parameter = polymorphic.Occurrences.Single(item => item.Span.Start == generic.IndexOf("value", StringComparison.Ordinal));
        Check(parameter.Type.Contains("Bool", StringComparison.Ordinal) && parameter.Type.Contains("String", StringComparison.Ordinal), "generic parameter hover depends on the last call only");
    }

    private static void UncalledDefinitions()
    {
        const string source = "fn dormant(input) { scope Inner { child: fn(x) { x }, nested: scope {} }; Inner };";
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///uncalled.msync");
        Check(analysis.Diagnostic is null, "uncalled generic body was incorrectly specialized");
        foreach (var (name, type) in new[] { ("dormant", "Function"), ("Inner", "Scope"), ("child", "Function"), ("nested", "Scope") })
        {
            int start = source.IndexOf(name, StringComparison.Ordinal);
            Check(analysis.Occurrences.Any(item => item.Span.Start == start && item.Span.Length == name.Length && item.Type == type), "syntax role missing inside an uncalled generic body: " + name);
        }
        Check(analysis.Occurrences.Single(item => item.Span.Start == 3).Signature.Contains("inferred at call", StringComparison.Ordinal), "uncalled generic result was fabricated");
        EditorAnalysis keywords = EditorCompiler.Analyze("scope scope { fn: fn() { true } };", "file:///names.msync");
        Check(keywords.Occurrences.Any(item => item.Span.Start == 6 && item.Span.Length == 5 && item.Type == "Scope"), "declaration name span accidentally targets its keyword");
    }

    private static SourceDiagnostic Reject(string source)
    {
        try { _ = Frontend.Compile(source, "file:///test.msync"); }
        catch (CompileException error) { return error.Diagnostic; }
        catch (SyntaxException error) { return error.Diagnostic; }
        throw new InvalidOperationException("expected a diagnostic for: " + source);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
