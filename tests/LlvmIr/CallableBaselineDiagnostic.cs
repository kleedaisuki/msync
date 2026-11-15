using System.Text.Json;
using Msync.Compiler;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Native;

// Explicit old-path diagnostic only; never called by the new target acceptance journey.
internal static class CallableBaselineDiagnostic
{
    internal static async Task RunAsync(string sourcePath, string output)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        string source = File.ReadAllText(sourcePath);
        HirModule hir = Compiler.Analyze(source, sourcePath);
        File.WriteAllText(Path.Combine(output, "current-typed-hir.txt"), HirPrinter.Print(hir));
        List<object> outcomes = [];
        foreach (int level in new[] { 0, 2 })
        {
            NativeProgram baseline = Compiler.CompileNative(source, sourcePath, level);
            await using NativeInterpreter interpreter = new(baseline,
                new ExecutionOptions(WorkingDirectory: output, SourceFile: sourcePath));
            NativeValue actual = await interpreter.ExecuteAsync();
            outcomes.Add(new { optimization = level, result = actual.AsNumber() });
        }
        string receipt = JsonSerializer.Serialize(new { diagnosticOnly = true, sourcePath, outcomes });
        File.WriteAllText(Path.Combine(output, "old-path-receipt.json"), receipt);
        Console.WriteLine(receipt);
    }
}
