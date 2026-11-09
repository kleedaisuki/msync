using System.Security.Cryptography;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Components;

internal static class SnapshotChecks
{
    private const string CaptureSource = """
        fn identity(value: Bool) { value };
        let z = "definition-z";
        let a = "definition-a";
        let condition = identity(true);
        fn captured() { if condition { z } else { a } };
        let z = "shadow";
        captured()
        """;

    public static void Run()
    {
        Check(CaptureSource, "definition-z");
        Check("fn make(value) { fn() { value } }; let first = make(\"first\"); let second = make(\"second\"); $std::string::concat(first(), second())", "firstsecond");
        Check("let value = \"definition\"; fn recur(done) { if done { value } else { recur(true) } }; let value = \"shadow\"; recur(false)", "definition");
        Check("let external = \"captured\"; let object = scope { read: fn() { external }, item: \"member\", }; { let external = \"inner\"; object::read() }", "captured");
        Check("fn values(value) { [value] }; let value = \"before\"; let delayed = values(value); let value = \"after\"; $std::ranges::front(delayed)", "before");
        foreach (string selected in new[] { "true", "false" })
            Check($"fn choose(present: Bool) {{ if present {{ \"present\" }} else {{ null }} }}; let item? = choose({selected}); if item? {{ \"missing\" }} else {{ item }}", selected == "true" ? "present" : "missing");
        using MemoryStream artifact = new();
        WriteCapture(artifact);
        Console.WriteLine($"Lexical snapshots passed: captures, shadowing, returned closures, recursive slots, scopes, lazy ranges and null refinement through source-free stages; capture artifact {Convert.ToHexStringLower(SHA256.HashData(artifact.ToArray()))}.");
    }

    public static void WriteCapture(Stream output) => Artifact.Write(SourceCompiler.Compile(CaptureSource, "snapshot-capture.msync"), output);

    private static void Check(string source, string expected)
    {
        var module = SourceCompiler.Compile(source, "snapshot.msync");
        using MemoryStream encoded = new();
        Artifact.Write(module, encoded);
        encoded.Position = 0;
        var imported = Artifact.Read(encoded);
        var context = new BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory);
        foreach (var variant in new[] { imported, Binding.Bind(imported, context) })
        {
            using MemoryStream reencoded = new();
            Artifact.Write(variant, reencoded);
            reencoded.Position = 0;
            var value = Interpreter.ExecuteAsync(Artifact.Read(reencoded), new(Output: TextWriter.Null, Error: TextWriter.Null)).GetAwaiter().GetResult();
            if (value is not StringValue text || text.Data != expected) throw new Exception($"Lexical snapshot changed: expected '{expected}', found '{value}'");
        }
    }
}
