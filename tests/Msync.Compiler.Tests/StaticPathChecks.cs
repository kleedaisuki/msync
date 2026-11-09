using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Frontend = Msync.Compiler.Compiler;

internal static class StaticPathChecks
{
    public static void Run()
    {
        LiteralFacts();
        GenericIsolation();
        ReceiverAndArtifactIsolation();
        Console.WriteLine("static Path values, pure derivation, generic isolation and unchanged artifacts ok");
    }

    private static void LiteralFacts()
    {
        const string source = """
            let folder = ~/real;
            let alias = folder;
            scope Config { root: folder, text: "child", remote: ssh://forge };
            let member = Config::root;
            let joined = folder / "child";
            let textJoined = "base" / "child";
            let remote = Config::remote::folder;
            let remoteChild = remote / "child";
            let same = if exists ~/condition { ~/real } else { ~/real };
            let different = if exists ~/condition { ~/real } else { ~/other };
            let nullable? = if exists ~/condition { ~/real } else { null };
            let fromText: Path = "folder with spaces";
            let toText: String = folder;
            fn make() { ~/fixed / "child" };
            let result = make();
            fn annotated() -> Path { "declared" };
            let annotatedResult = annotated();
            fn explicitReturn() -> Path { return "returned" };
            let returnedResult = explicitReturn();
            let contextual = $env::HOME;
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///known-paths.msync");
        Check(analysis.Diagnostic is null, "known Path sample rejected: " + analysis.Diagnostic?.EditorMessage);
        foreach (string name in new[] { "folder", "alias", "member", "same" }) Path(analysis, source, name, new PathValue("~/real"));
        Path(analysis, source, "joined", PathSemantics.JoinValue(new("~/real"), new StringValue("child")));
        Path(analysis, source, "textJoined", PathSemantics.JoinValue(new("base"), new StringValue("child")));
        var qualified = PathSemantics.Qualify(new("~/real"), new(ConnectionKind.Ssh, "forge"));
        Path(analysis, source, "remote", qualified);
        Path(analysis, source, "remoteChild", PathSemantics.JoinValue(qualified, new StringValue("child")));
        Path(analysis, source, "fromText", new("folder with spaces"));
        Check(Binding(analysis, source, "toText").KnownText == "~/real" && Binding(analysis, source, "toText").KnownPath is null,
            "Path-to-String conversion retained a Path value fact");
        Path(analysis, source, "result", PathSemantics.JoinValue(new("~/fixed"), new StringValue("child")));
        Path(analysis, source, "annotatedResult", new("declared"));
        Path(analysis, source, "returnedResult", new("returned"));
        foreach (string name in new[] { "different", "nullable", "contextual" })
            Check(Binding(analysis, source, name).KnownPath is null, "runtime-dependent path was treated as a static location: " + name);
        var config = analysis.Occurrences.Single(item => item.Span.Start == source.IndexOf("Config", StringComparison.Ordinal));
        Check(config.Members!.Single(member => member.Name == "root").KnownPath == new PathValue("~/real"), "Scope field lost its static Path value");
    }

    private static void GenericIsolation()
    {
        const string source = """
            fn identity(value) { value };
            let first = identity(~/first);
            let second = identity(~/second);
            fn textIdentity(value: String) -> Path { value };
            let firstText = textIdentity("first");
            let secondText = textIdentity("second");
            fn front(values) { $std::ranges::front(values) };
            let firstItem = front([~/first]);
            let secondItem = front([~/second]);
            scope Left { root: ~/"first" };
            scope Right { root: ~/"second" };
            fn scoped(value) { value::root };
            let leftRoot = scoped(Left);
            let rightRoot = scoped(Right);
            """;
        EditorAnalysis analysis = EditorCompiler.Analyze(source, "file:///generic-paths.msync");
        Check(analysis.Diagnostic is null, "generic Path sample rejected: " + analysis.Diagnostic?.EditorMessage);
        foreach (string name in new[] { "first", "second", "firstText", "secondText", "firstItem", "secondItem" })
            Check(Binding(analysis, source, name).KnownPath is null, "a type-shared generic result froze an argument path: " + name);
        Path(analysis, source, "leftRoot", new("~/first"));
        Path(analysis, source, "rightRoot", new("~/second"));
        int selected = source.IndexOf("value::root", StringComparison.Ordinal) + "value::".Length;
        Check(analysis.Occurrences.Single(item => item.Span.Start == selected && item.Span.Length == 4).KnownPath is null,
            "a polymorphic generic source occurrence picked one Scope's Path");
    }

    private static void ReceiverAndArtifactIsolation()
    {
        const string incomplete = "fn make() { ~/fixed / \"child\" }; make()/";
        EditorAnalysis analysis = EditorCompiler.Analyze(incomplete, "file:///path-receiver.msync");
        Check(analysis.At(incomplete.Length).Receiver?.KnownPath == PathSemantics.JoinValue(new("~/fixed"), new StringValue("child")),
            "incomplete slash completion lost the known factory result");
        const string source = "let folder = ~/root / \"child\"; folder -> ~/target;";
        byte[] before = Bytes(Frontend.Compile(source, "source.msync", 0));
        _ = EditorCompiler.Analyze(source, "source.msync");
        byte[] after = Bytes(Frontend.Compile(source, "source.msync", 0));
        Check(before.AsSpan().SequenceEqual(after), "editor path facts changed relocatable artifact bytes");
    }

    private static byte[] Bytes(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        return stream.ToArray();
    }

    private static SemanticOccurrence Binding(EditorAnalysis analysis, string source, string name)
    {
        int start = source.IndexOf("let " + name, StringComparison.Ordinal) + 4;
        return analysis.Occurrences.Single(item => item.Span.Start == start && item.Span.Length == name.Length);
    }

    private static void Path(EditorAnalysis analysis, string source, string name, PathValue expected) =>
        Check(Binding(analysis, source, name).KnownPath == expected,
            $"static Path does not match runtime path construction: {name}, expected {expected}, found {Binding(analysis, source, name).KnownPath?.ToString() ?? "unknown"}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
