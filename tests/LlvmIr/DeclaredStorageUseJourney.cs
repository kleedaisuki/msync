using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class DeclaredStorageUseJourney
{
    internal static async Task<int> RunAsync()
    {
        const string source = "fn read(row: Scope) { row::first }; read(scope { first: scope { value: 7 } })::value";
        var graph = LlvmHirLowerer.Lower(Compiler.Analyze(source, "declared-storage-use.msync"));
        var read = graph.Module.Functions.Single(function => function.Name == "read");
        LlvmPromoteMemoryToRegister.Run(read, new LlvmDominatorTree(read));
        var load = graph.Module.Functions.SelectMany(function => function.BasicBlocks)
            .SelectMany(block => block.Instructions).OfType<LlvmLoadInstruction>().Single(load =>
                load.Type.IsPointerTy() && load.PointerOperand is LlvmGetElementPtrInstruction
                { PointerOperand: LlvmArgument, HasAllZeroIndices: true, TargetStorageType: not null });
        var address = (LlvmGetElementPtrInstruction)load.PointerOperand;
        var root = (LlvmArgument)address.PointerOperand;
        var declared = address.TargetStorageType;

        // Physical field geometry remains available when optimization-created
        // pointer loads have no source value annotation and the base is a Scope.
        root.LogicalType = graph.Types.Count;
        graph = graph with { Types = [.. graph.Types, new NativeCodeType(TypeKind.Scope)] };
        address.ReplaceAllUsesWith(root);
        address.EraseFromParent();
        load.LogicalType = -1;
        load.GetOperandUse(0).LogicalType = -1;
        Program.Require(ReferenceEquals(load.GetOperandUse(0).TargetStorageType, declared),
            "Folding a zero GEP must retain the physical storage declaration on its load operand.");

        List<CompilationObservation> records = [];
        var program = LlvmNativeLowerer.Lower(graph, new(records.Add, includeOptimizerPasses: true));
        Program.Require(records.Sum(record => (long)(record.Metrics?.GetValueOrDefault("addresses.memory_storage.physical_use") ?? 0)) > 0,
            "A folded pointer-valued field must select its recorded physical storage, not infer it from an abstract Scope.");
        string work = Path.GetFullPath(".temp/llvm-ir/declared-storage-use");
        Directory.CreateDirectory(work);
        await Execute(program, work);
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        await Execute(NativeArtifact.Read(bytes), work);
        return 2 + await AggregateLoads();
    }

    private static async Task<int> AggregateLoads()
    {
        const string source = "fn read(row: Scope) { row::run }; read(scope { run: fn { 7 } })()";
        foreach (bool foldFirst in new[] { false, true })
        {
            var graph = LlvmHirLowerer.Lower(Compiler.Analyze(source, "declared-function-storage.msync"));
            var read = graph.Module.Functions.Single(function => function.Name == "read");
            var tree = new LlvmDominatorTree(read);
            LlvmPromoteMemoryToRegister.Run(read, tree);
            var load = read.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmLoadInstruction>()
                .Single(load => load.Type is LlvmStructType { Name: "msync.function" });
            var address = (LlvmGetElementPtrInstruction)load.PointerOperand;
            var root = (LlvmArgument)address.PointerOperand;
            root.LogicalType = graph.Types.Count;
            graph = graph with { Types = [.. graph.Types, new NativeCodeType(TypeKind.Scope)] };
            if (foldFirst)
            {
                address.ReplaceAllUsesWith(root);
                address.EraseFromParent();
                load.GetOperandUse(0).LogicalType = -1;
            }

            List<CompilationObservation> records = [];
            CompilationTrace trace = new(records.Add, includeOptimizerPasses: true);
            using var assumptions = new LlvmAssumptionCache(read);
            LlvmInstCombine.Run(read, new LlvmAAResults(read, tree: tree), assumptions,
                new LlvmTargetLibraryInfo(graph.Module), new LlvmTargetTransformInfo(graph.Module.DataLayout), tree, trace: trace);
            Program.Require(read.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmLoadInstruction>().Count() == 3,
                "The physical storage repair must retain actual three-component Function load scalarization.");
            Program.Require(records.Any(record => record.Metrics?.GetValueOrDefault("instcombine.memory.storage_view.gep") > 0),
                "Aggregate-load scalarization must publish the original memory operand's physical geometry on its byte GEPs.");
            var program = LlvmNativeLowerer.Lower(graph, trace);
            string work = Path.GetFullPath($".temp/llvm-ir/declared-storage-use/function-{foldFirst}");
            Directory.CreateDirectory(work);
            await Execute(program, work);
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            bytes.Position = 0;
            await Execute(NativeArtifact.Read(bytes), work);
        }
        return 4;
    }

    private static async Task Execute(NativeProgram program, string work)
    {
        await using NativeInterpreter interpreter = new(program, new ExecutionOptions(WorkingDirectory: work)
            { Tiers = new NativeTierOptions(NativeJitPolicy.Off) });
        NativeValue result = await interpreter.ExecuteAsync();
        Program.Require(result.AsNumber() == 7,
            "A pointer-valued first field loaded through a folded physical storage use must preserve its nested object.");
    }
}
