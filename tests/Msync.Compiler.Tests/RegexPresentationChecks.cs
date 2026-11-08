using Msync.Compiler;
using Msync.Compiler.Syntax;

internal static class RegexPresentationChecks
{
    public static void Run()
    {
        const string source = """
            let result = match file {
                <fn[?]scope> => { true },
                <(?<name>if)> => { false },
                _ => { false }
            };
            let comparison = "a" < "b";
            let text = "<fn?>";
            // <scope?>
            """;
        Check(source, ["<fn[?]scope>", "<(?<name>if)>"]);
        const string incomplete = "let result = match file { <ready> => { true }, <fn[?]scope";
        Check(incomplete, ["<ready>", "<fn[?]scope"]);
        Check("let text = \"<fn?", []);
        Console.WriteLine("parser-owned complete and incomplete regex presentation spans ok");
    }

    private static void Check(string source, string[] expected)
    {
        EditorParseResult parsed = Parser.ParseForEditor(source);
        string[] parsedText = parsed.RegexSpans.Select(span => source.Substring(span.Start, span.Length)).ToArray();
        if (!parsedText.SequenceEqual(expected)) throw new Exception("parser regex spans differ: " + string.Join(", ", parsedText));
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///regex-presentation.msync");
        if (!analysis.RegexSpans.SequenceEqual(parsed.RegexSpans)) throw new Exception("editor analysis lost parser-owned regex spans");
    }
}
