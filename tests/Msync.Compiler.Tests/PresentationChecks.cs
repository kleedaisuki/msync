using Msync.Core.Native;
using Msync.Compiler;
using Msync.Core;
using Frontend = Msync.Compiler.Compiler;

internal static class PresentationChecks
{
    public static void Run()
    {
        const string source = """
            object SomeClass {
                remote: ssh://forge,
                count: 3,
                label: "settings",
                ready: true,
                new: fn() { object { x: ~/.codex } },
                nested: object { enabled: true }
            };
            let ClassAlias = SomeClass;
            let anonymous = object { remote: SomeClass::remote };
            let instance = SomeClass::new();
            let instanceAlias = instance;
            let operation = SomeClass::new;
            let remote = SomeClass::remote;
            let namedChoice = if exists ~/condition { SomeClass } else { ClassAlias };
            let mixedChoice = if exists ~/condition { SomeClass } else { anonymous };
            """;
        _ = Frontend.Compile(source);
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///presentation.msync");
        Check(analysis.Diagnostic is null, "presentation sample does not typecheck: " + analysis.Diagnostic?.EditorMessage);
        foreach (string name in new[] { "SomeClass", "ClassAlias", "namedChoice" })
            Role(analysis, source, name, "Object", "type");
        foreach (string name in new[] { "anonymous", "instance", "instanceAlias", "mixedChoice" })
            Role(analysis, source, name, "Object", "variable");
        Role(analysis, source, "operation", "Function", "function");
        Role(analysis, source, "remote", "Connection", "variable", last: true);

        var named = Occurrence(analysis, source, "SomeClass");
        var members = named.Members!.ToDictionary(member => member.Name, StringComparer.Ordinal);
        Check(members["remote"].Type == "Connection" && members["remote"].Endpoint == new NativeConnection(ConnectionKind.Ssh, "forge"),
            "Object field lost its inferred Connection value");
        Check(members["remote"].SemanticRole == "variable", "a Connection field was presented as an Object type");
        Check(members["count"].Type == "Number" && members["label"].Type == "String" && members["ready"].Type == "Bool",
            "Object fields were restricted instead of preserving independently inferred types");
        Check(members["new"].Type == "Function" && members["new"].SemanticRole == "function", "function-valued Object field lost its function role");
        Check(members["nested"].Type == "Object" && members["nested"].SemanticRole == "variable", "an anonymous Object field acquired named type presentation");
        Check(Occurrence(analysis, source, "instance").Members!.Single().Type == "Path", "anonymous Object presentation changed its member type");
        Console.WriteLine("named Object, anonymous Object, inferred heterogeneous fields and function presentation roles ok");
    }

    private static void Role(EditorAnalysis analysis, string source, string name, string type, string role, bool last = false)
    {
        SemanticOccurrence occurrence = Occurrence(analysis, source, name, last);
        Check(occurrence.Type == type && occurrence.SemanticRole == role,
            $"{name}: expected {type}/{role}, found {occurrence.Type}/{occurrence.SemanticRole}");
    }

    private static SemanticOccurrence Occurrence(EditorAnalysis analysis, string source, string name, bool last = false)
    {
        string marker = name == "SomeClass" ? "object SomeClass" : "let " + name;
        int start = (last ? source.LastIndexOf(marker, StringComparison.Ordinal) : source.IndexOf(marker, StringComparison.Ordinal))
            + marker.Length - name.Length;
        return analysis.Occurrences.Single(item => item.Span.Start == start && item.Span.Length == name.Length);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
