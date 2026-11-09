using Msync.Compiler;
using Msync.Compiler.Syntax;

internal static class NavigationChecks
{
    public static void Run()
    {
        Target("let value = true; fn f(value: Path) { val§ue }; f(./);", "value: Path");
        Target("let .codex = true; fn f() { .co§dex }; f();", ".codex =");
        Target("let value = true; fn f() { val§ue }; let value = false; f();", "value = true");
        Target("scope Config { root: ./ }; let alias = Config; alias::ro§ot;", "root:");
        Target("scope Config { root: ./ }; let alias = Config; ali§as::root;", "alias =");
        Target("fn f() { let local = scope { leaf: true }; local::le§af }; f();", "leaf:");
        string future = "fn before() { lat§er }; let later = true; before();";
        Check(Definitions(future).Count == 0, "an unbound Path was linked to a future declaration");
        string library = "let fs = $std::fs; fs::ex§ists(./);";
        var standard = Definitions(library);
        Check(standard.Count == 1 && standard[0].SourceName == "msync-stdlib:///0.8.0/fs.msync"
            && standard[0].Source[standard[0].Span.Start..standard[0].Span.End] == "exists", "standard member lost its virtual declaration");
        Outline();
        Console.WriteLine("semantic definitions preserve lexical identity and parser-owned outlines exclude literal text");
    }

    private static IReadOnlyList<EditorLocation> Definitions(string marked)
    {
        int offset = marked.IndexOf('§');
        return EditorCompiler.Analyze(marked.Remove(offset, 1), "file:///navigation.msync").DefinitionsAt(offset);
    }

    private static void Target(string marked, string target)
    {
        string source = marked.Replace("§", "", StringComparison.Ordinal);
        int expected = source.IndexOf(target, StringComparison.Ordinal);
        var definitions = Definitions(marked);
        Check(definitions.Count == 1 && definitions[0].Span.Start == expected,
            $"definition points at another binding: {marked}; expected {expected}; got {string.Join(',', definitions.Select(location => location.Span.Start))}");
    }

    private static void Outline()
    {
        const string source = "let text = \"fn phantom() { true };\"; # scope Ghost { };\n"
            + "scope Actual { new: fn(file: Path) { let .codex = file; scope { leaf: .codex } } };";
        var analysis = EditorCompiler.Analyze(source, "file:///outline.msync");
        Check(analysis.Symbols.Select(symbol => symbol.Name).SequenceEqual(["text", "Actual"]), "outline indexed String/comment text as declarations");
        var actual = analysis.Symbols[1];
        Check(actual.Children.Select(symbol => symbol.Name).SequenceEqual(["new"]), "Scope fields missing from outline");
        var operation = actual.Children[0];
        Check(operation.Children.Select(symbol => symbol.Name).SequenceEqual(["file", ".codex", "leaf"]), "nested parameters, bindings or Scope fields missing from outline");
        Check(actual.Span.Start < actual.SelectionSpan.Start && actual.Span.End >= operation.Span.End
            && operation.Span.End > operation.SelectionSpan.End, "outline range is not the complete declaration");
        var unfinished = EditorCompiler.Analyze(source[..source.LastIndexOf('}')], "file:///outline.msync");
        Check(unfinished.Symbols.Select(symbol => symbol.Name).SequenceEqual(["text", "Actual"]), "unfinished declaration removed its structural outline");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
