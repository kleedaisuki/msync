using System.Reflection;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;

internal static class OptimizedSourceDump
{
    internal static void Run(string sourcePath, string output)
    {
        Directory.CreateDirectory(output);
        var graph = LlvmHirLowerer.Lower(Compiler.Analyze(File.ReadAllText(sourcePath), sourcePath));
        File.WriteAllText(Path.Combine(output, "before.ll"), LlvmIrWriter.Write(graph.Module));
        List<CompilationObservation> records = [];
        CompilationTrace trace = new(records.Add, includeOptimizerPasses: true);
        typeof(Compiler).GetMethod("OptimizeLlvm", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [graph, 2, trace]);
        File.WriteAllLines(Path.Combine(output, "optimizer.trace.jsonl"), records.Select(record =>
            JsonSerializer.Serialize(new { record.Name, record.Attributes, record.Metrics,
                record.Outcome, error = record.Error?.Message })));
        File.WriteAllText(Path.Combine(output, "after.ll"), LlvmIrWriter.Write(graph.Module));
        var facts = new
        {
            types = graph.Types.Select((type, index) => new { index, type.Kind, type.Pointee, type.Element, type.Layouts, machine = type.Machine?.ToString() }),
            structures = graph.StructLayouts.Select(pair => new { structure = pair.Key.Name, layout = pair.Value,
                fields = graph.Layouts[pair.Value].Fields }),
            functions = graph.Module.Functions.Where(function => !function.IsDeclaration).Select(function => new
            {
                function.Name,
                arguments = function.Arguments.Select(argument => new { argument.Name, argument.LogicalType }),
                instructions = function.BasicBlocks.SelectMany(block => block.Instructions).Select((instruction, index) => new
                {
                    index, instruction.Name, instruction.Opcode, instruction.LogicalType, instruction.Source,
                    structure = instruction is LlvmGetElementPtrInstruction { SourceElementType: LlvmStructType structure } ? structure.Name : null,
                    targetStorage = (instruction as LlvmGetElementPtrInstruction)?.TargetStorageType?.Name,
                    operands = instruction.OperandUses.Select(use => new { use.OperandNo, use.LogicalType,
                        name = use.Value?.Name, definition = use.Value?.GetType().Name, logical = use.Value?.LogicalType,
                        targetStorage = use.TargetStorageType?.Name })
                })
            })
        };
        File.WriteAllText(Path.Combine(output, "abi-facts.json"), JsonSerializer.Serialize(facts,
            new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            var program = LlvmNativeLowerer.Lower(graph);
            using var artifact = File.Create(Path.Combine(output, "program.native"));
            NativeArtifact.Write(program, artifact);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "target-error.txt"), error.ToString());
            throw;
        }
    }
}
