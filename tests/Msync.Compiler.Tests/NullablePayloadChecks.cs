using Msync.Compiler;

internal static class NullablePayloadChecks
{
    public static void Run()
    {
        const string scope = "let record? = if exists ./input { object { file: ./input, read: fn { file } } } else { null }; if record is not null { record::";
        var selected = EditorCompiler.Analyze(scope, "nullable-scope.msync").At(scope.Length).Receiver;
        Check(selected?.Type == "Object" && selected.Members?.Any(member => member.Name == "file" && member.Type == "Path") == true
            && selected.Members.Any(member => member.Name == "read" && member.SemanticRole == "function"),
            "a Null alternative erased the non-null Object's editor capabilities");
        const string loop = "let first? = null; for item in [\"one\", \"two\"] { first = object { file: item }; }; if first is not null { first::";
        Check(EditorCompiler.Analyze(loop, "nullable-loop.msync").At(loop.Length).Receiver?.Members?.Single().Name == "file",
            "loop-carried nullable Object metadata did not reach the guarded editor receiver");
        Callable("let operation? = if exists ./input { fn { \"value\" } } else { null }; if operation is not null { operation() }; ");
        Callable("let operation? = if exists ./missing { null } else { $std::string::trim }; if operation is not null { operation(\"value\") }; ");
        const string unguarded = "let record? = if exists ./input { object { file: ./input } } else { null }; record::file = ./other;";
        Check(EditorCompiler.Analyze(unguarded, "unguarded-scope.msync").Diagnostic?.Code == "E0203",
            "retaining nullable Object metadata accidentally admitted an unhandled Null field store");
        Console.WriteLine("nullable Object and callable editor payloads survive Null joins and require narrowing");
    }

    private static void Callable(string source)
    {
        var analysis = EditorCompiler.Analyze(source, "nullable-callable.msync");
        int call = source.LastIndexOf("operation(", StringComparison.Ordinal);
        Check(analysis.Diagnostic is null && analysis.Occurrences.Any(occurrence => occurrence.Span.Start == call
            && occurrence.Type == "Function" && occurrence.SemanticRole == "function"),
            "a guarded nullable callable lost its inferred signature or function role");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
