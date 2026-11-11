using Msync.Core;

namespace Msync.Cli;

internal static class ArtifactCommands
{
    internal static bool IsArtifact(string file) => file.EndsWith(".mso", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mexe", StringComparison.OrdinalIgnoreCase);
    internal static async Task<byte[]> ReadArtifactInput()
    {
        using MemoryStream bytes = new();
        await Console.OpenStandardInput().CopyToAsync(bytes);
        return bytes.ToArray();
    }
    internal static void Write(Module module, string output)
    {
        if (output != "-") { Artifact.WriteProduced(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        Artifact.WriteProduced(module, stream);
    }

    internal static void PrintPlan(Module module, string policy)
    {
        bool color = TerminalPresentation.Colors(policy);
        Console.WriteLine(TerminalPresentation.Paint($"{module.Stage} bytecode, optimization O{module.OptimizationLevel}, {module.Functions.Count} functions, {module.Constants.Count} constants", TerminalPresentation.HeadingColor, color));
        for (int f = 0; f < module.Functions.Count; f++)
        {
            FunctionCode function = module.Functions[f];
            Console.WriteLine(TerminalPresentation.Syntax($"fn #{f} {function.Name}({function.ParameterCount} arguments) -> {function.ReturnType}", color));
            for (int i = 0; i < function.Instructions.Count; i++)
            {
                Instruction instruction = function.Instructions[i];
                string operation = TerminalPresentation.Paint(instruction.Op.ToString().PadRight(16), "36", color);
                Console.WriteLine($"  {i,4}: {operation} {instruction.A} {instruction.B} {instruction.Text}");
            }
        }
        foreach (BasicBlock block in module.Blocks ?? BlockBuilder.Form(module).Blocks!)
            Console.WriteLine($"block fn#{block.Function} [{block.Start},{block.End}) effects={block.HasEffects} successors=[{string.Join(',', block.Successors)}]");
        for (int index = 0; index < (module.ExecutionPlans?.Count ?? 0); index++)
        {
            ExecutionPlan plan = module.ExecutionPlans![index];
            Console.WriteLine(TerminalPresentation.Paint($"execution_plan @p{index} fn#{plan.Function} region [{plan.Start},{plan.End}) eliminated={plan.EliminatedTransfers}", TerminalPresentation.HeadingColor, color));
            if (plan.ResultTransfers is { } results)
                Console.WriteLine($"  result all [{string.Join(',', results.Select(result => $"@p{index}n{result}"))}]");
            foreach (PlannedTransfer transfer in plan.Transfers)
            {
                string operation = TerminalPresentation.Paint(transfer.Kind.ToString(), "36", color);
                Console.WriteLine($"  @p{index}n{transfer.Id}: {operation} mode={transfer.Mode} {transfer.Source} -> {transfer.Destination} predecessors=[{string.Join(',', transfer.Predecessors)}]");
                foreach (PlanResource read in transfer.Reads)
                    Console.WriteLine($"    read {read.Endpoint}:{read.Path} subtree={read.Subtree}");
                foreach (PlanResource write in transfer.Writes)
                    Console.WriteLine($"    write {write.Endpoint}:{write.Path} subtree={write.Subtree}");
            }
            foreach (ExecutionBatch batch in plan.Batches)
                Console.WriteLine($"  batch {batch.Endpoint} transfers=[{string.Join(',', batch.Transfers)}]");
        }
    }
}
