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
        if (output != "-") { Artifact.Write(module, output); return; }
        using Stream stream = Console.OpenStandardOutput();
        Artifact.Write(module, stream);
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
        foreach (BasicBlock block in module.Blocks ?? []) Console.WriteLine($"block fn#{block.Function} [{block.Start},{block.End}) effects={block.HasEffects} successors=[{string.Join(',', block.Successors)}]");
    }
}
