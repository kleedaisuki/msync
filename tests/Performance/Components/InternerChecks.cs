using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Components;

internal static class InternerChecks
{
    public static void Run()
    {
        CheckFirstInsertionOrder();
        CheckRollbackAndArtifact();
        CheckPersistentValues();
        Console.WriteLine("Constant interning passed: first-insertion IDs, duplicate values, failed-submission rollback, source-free artifact and persistent runtime values.");
    }

    private static void CheckFirstInsertionOrder()
    {
        StringBuilder source = new();
        for (int i = 0; i < 128; i++) source.AppendLine($"let item_{i} = \"value_{i % 16}\";");
        var module = SourceCompiler.Compile(source.ToString(), "interner-order.msync", 0);
        var actual = module.Constants.OfType<StringValue>().Select(value => value.Data).ToArray();
        var expected = Enumerable.Range(0, 16).Select(i => $"value_{i}").ToArray();
        if (!actual.SequenceEqual(expected)) throw new Exception("Constant interning changed first-insertion order or retained duplicate values");
        var distinctKinds = SourceCompiler.Compile("let text = \"same\"; let path = ./same; let again = \"same\";", optimization: 0);
        if (distinctKinds.Constants.OfType<StringValue>().Count(value => value.Data == "same") != 1
            || !distinctKinds.Constants.OfType<PathValue>().Any(value => value.Raw == "same"))
            throw new Exception("String and Path constants must retain distinct value identities");
    }

    private static void CheckRollbackAndArtifact()
    {
        CompilerSession compiler = new("interner-rollback.msync", 0);
        var first = compiler.Compile("\"prefix\";");
        Reject(compiler);
        var recovered = compiler.Compile("let accepted = \"stale\"; accepted");
        if (!recovered.Constants.Take(first.Constants.Count).SequenceEqual(first.Constants)) throw new Exception("Failed submission changed accepted constant IDs");
        using MemoryStream encoded = new();
        Artifact.Write(recovered, encoded);
        encoded.Position = 0;
        var imported = Artifact.Read(encoded);
        var value = Interpreter.ExecuteAsync(imported, new(Output: TextWriter.Null, Error: TextWriter.Null)).GetAwaiter().GetResult();
        if (value is not StringValue { Data: "stale" }) throw new Exception("Rolled-back constant index did not survive source-free execution");
    }

    private static void CheckPersistentValues()
    {
        CompilerSession compiler = new("interner-session.msync", 0);
        ExecutionSession runtime = new(new(Output: TextWriter.Null, Error: TextWriter.Null));
        runtime.ExecuteAsync(compiler.Compile("let keep = \"prefix\";")).GetAwaiter().GetResult();
        Reject(compiler);
        var recovered = compiler.Compile("let accepted = \"stale\"; accepted == \"stale\" and keep == \"prefix\"");
        var value = runtime.ExecuteAsync(recovered).GetAwaiter().GetResult();
        if (value is not BoolValue { Data: true }) throw new Exception("Failed compilation changed persistent values or reused a stale constant ID");
    }

    private static void Reject(CompilerSession compiler)
    {
        try { compiler.Compile("let discarded = \"stale\"; let invalid = null;"); }
        catch (CompileException) { return; }
        throw new Exception("Expected the unmarked nullable binding to fail after creating a temporary constant");
    }
}
