using System.Text;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class PreparationImageJourney
{
    internal static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        List<object> receipts = [];
        foreach (int optimization in new[] { 0, 2 })
        {
            string work = Path.Combine(output, "O" + optimization);
            Directory.CreateDirectory(Path.Combine(work, "inputs"));
            string destination = Path.Combine(work, "selected");
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            File.WriteAllText(Path.Combine(work, "inputs", "first.txt"), "first", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(work, "inputs", "ignored.bin"), "ignored", new UTF8Encoding(false));
            string later = Path.Combine(work, "inputs", "later.txt");
            if (File.Exists(later)) File.Delete(later);
            const string source = "fn untouched() { 7 }; ./inputs / <.*[.]txt> -> ./selected/;";
            string sourceFile = Path.Combine(work, "source.msync");
            File.WriteAllText(sourceFile, source);
            var options = new ExecutionOptions(WorkingDirectory: work);
            NativeProgram original = Compiler.Compile(source, sourceFile, optimization);
            Require(original.Backend is null, "The relocatable source must not preselect an executable image.");
            NativeProgram prepared = NativePreparation.Prepare(original, options);
            Require(prepared.ExecutionPlans is { Count: > 0 } &&
                prepared.Functions[prepared.EntryPoint].Instructions.Any(code => code.Op == NativeOp.ExecutePlan),
                "The source fixture must actually rewrite contextual selection/transfer into an execution plan.");
            var after = prepared.Backend ?? throw new InvalidOperationException("Preparation did not emit executable code.");
            Require(after.Functions.Count == original.Functions.Count,
                "Preparing the entry transfer changed the function identities.");
            for (int i = 0; i < original.Functions.Count; ++i)
                if (i != original.EntryPoint) Require(ReferenceEquals(original.Functions[i], prepared.Functions[i]),
                    "Preparation rewrote the logical instructions of an untouched function.");
            var body = after.Functions[prepared.EntryPoint];
            Require(body.Instructions.Count == prepared.Functions[prepared.EntryPoint].Instructions.Count &&
                body.RawWritable.Count == prepared.Functions[prepared.EntryPoint].Slots.Count,
                "Prepared image dimensions disagree with its actual emitted target records.");
            Require(prepared.ExecutionPlans!.All(plan => body.Instructions[plan.Start].Op == NativeOp.ExecutePlan),
                "The selected executable image did not retain the contextual plan entry.");
            Require(!body.PureSynchronous && !body.ContextFree, "ExecutePlan must retain its effectful/contextual classification.");
            File.WriteAllText(later, "late", new UTF8Encoding(false));
            using MemoryStream bytes = new();
            NativeArtifact.Write(prepared, bytes);
            File.WriteAllBytes(Path.Combine(work, "bound.msyncx"), bytes.ToArray());
            bytes.Position = 0;
            NativeProgram detached = NativeArtifact.Read(bytes);
            Require(detached.Backend!.Functions[detached.EntryPoint].Instructions.SequenceEqual(body.Instructions)
                && ReferenceEquals(detached, NativePreparation.Prepare(detached, options)),
                "Loading the detached executable lost the selected image or repeated contextual preparation.");
            await using (NativeInterpreter runtime = new(detached, options)) await runtime.ExecuteAsync();
            Require(File.ReadAllText(Path.Combine(destination, "first.txt")) == "first", "Prepared transfer output was lost.");
            Require(!File.Exists(Path.Combine(destination, "later.txt")) && !File.Exists(Path.Combine(destination, "ignored.bin")),
                "Bound fs selection did not preserve its frozen membership.");
            receipts.Add(new { optimization, rewrittenInstructions = body.Instructions.Count,
                preservedFunctions = original.Functions.Count - 1, executionPlans = prepared.ExecutionPlans!.Count, checkedFiles = 3 });
        }
        string receipt = JsonSerializer.Serialize(new { status = "passed", path = "source-prepare-image", receipts });
        File.WriteAllText(Path.Combine(output, "receipt.json"), receipt);
        Console.WriteLine(receipt);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
