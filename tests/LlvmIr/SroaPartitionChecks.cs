using Msync.Core;
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SroaPartitionChecks
{
    internal static int Run()
    {
        int checks = 0;
        foreach (string endian in new[] { "e", "E" })
        {
            OverlappingCopy(endian);
            ++checks;
        }
        MultiSliceInstruction();
        ++checks;
        return checks;
    }

    private static void OverlappingCopy(string endian)
    {
        LlvmContext context = new();
        LlvmModule module = new("sroa-presplit-overlap", context);
        module.DataLayout = new LlvmDataLayout(endian + "-n8:16:32:64");
        LlvmIntegerType i8 = LlvmType.GetInt8Ty(context), i32 = LlvmType.GetInt32Ty(context), i64 = LlvmType.GetInt64Ty(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(i32), LlvmLinkage.InternalLinkage,
            "overlapping-copy", module);
        LlvmBasicBlock entry = new(context, "entry", function);
        LlvmAllocaInstruction allocation = new(LlvmArrayType.Get(i8, 12), LlvmConstantInt.Get(i32, 1), alignment: new(8));
        entry.Append(allocation);
        LlvmTransformBuilder builder = new(entry);
        builder.DisableGepCanonicalization();
        LlvmValue[] cells = new LlvmValue[3];
        for (int index = 0; index < cells.Length; ++index)
        {
            cells[index] = builder.CreateGep(i8, allocation, [LlvmConstantInt.Get(i64, (ulong)(index * 4))]);
            builder.CreateStore(LlvmConstantInt.Get(i32, (ulong)(index + 1)), cells[index], alignment: new(4));
        }
        var copy = builder.CreateLoad(i64, cells[0], alignment: new(4));
        builder.CreateStore(copy, cells[1], alignment: new(4));
        var left = builder.CreateLoad(i32, cells[1], alignment: new(4));
        var right = builder.CreateLoad(i32, cells[2], alignment: new(4));
        builder.CreateRet(builder.CreateAdd(left, right));
        var tree = new LlvmDominatorTree(function);
        List<CompilationObservation> observations = [];
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        var result = LlvmSroa.Run(function, tree, trace: trace);
        Program.Require(result.Changed, "SROA must rewrite the source overlapping-copy partition graph.");
        var metrics = observations.Single(item => item.Name == "optimizer.pass.llvm-sroa").Metrics!;
        Program.Require(metrics.GetValueOrDefault("presplit.slice_groups") > 0 &&
            metrics.GetValueOrDefault("presplit.slice_group_overflows") == 0,
            "Single-pointer load/store groups must retain their first slice without constructing overflow sets.");
        LlvmSccp.Run(function);
        LlvmDeadCodeElimination.Run(function);
        var returned = (LlvmReturnInstruction)entry.GetTerminator();
        Program.Require(returned.ReturnValue is LlvmConstantInt value && value.GetZExtValue() == 3,
            "Presplitting must preserve memmove-like overlap ordering for both byte orders.");
        Program.Require(function.BasicBlocks.SelectMany(block => block.Instructions).All(instruction =>
            instruction is not LlvmAllocaInstruction and not LlvmLoadInstruction and not LlvmStoreInstruction),
            "The actual split-load/store graph must become promoted SSA rather than retain the original memory copy.");
    }

    private static void MultiSliceInstruction()
    {
        LlvmContext context = new();
        LlvmModule module = new("sroa-presplit-multiple-slices", context);
        var i8 = LlvmType.GetInt8Ty(context); var i32 = LlvmType.GetInt32Ty(context); var i64 = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i32), LlvmLinkage.InternalLinkage, "memmove", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var allocation = new LlvmAllocaInstruction(LlvmArrayType.Get(i8, 12), LlvmConstantInt.Get(i32, 1), alignment: new(4));
        entry.Append(allocation);
        LlvmTransformBuilder builder = new(entry);
        builder.DisableGepCanonicalization();
        for (ulong index = 0; index < 3; ++index)
        {
            var cell = builder.CreateGep(i8, allocation, [LlvmConstantInt.Get(i64, index * 4)]);
            builder.CreateStore(LlvmConstantInt.Get(i32, index + 1), cell, alignment: new(4));
        }
        var destination = builder.CreateGep(i8, allocation, [LlvmConstantInt.Get(i64, 4)]);
        var transfer = builder.CreateMemMove(destination, new(4), allocation, new(4), LlvmConstantInt.Get(i64, 8));
        var value = builder.CreateLoad(i32, destination, alignment: new(4));
        builder.CreateRet(value);
        List<CompilationObservation> observations = [];
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        LlvmSroa.Run(function, new(function), trace: trace);
        var metrics = observations.Single(item => item.Name == "optimizer.pass.llvm-sroa").Metrics!;
        Program.Require(metrics.GetValueOrDefault("presplit.slice_group_overflows") > 0,
            "A transfer with two distinct slices in one partition must retain its overflow group.");
        Program.Require(transfer.Parent is not null && LlvmMemIntrinsic.TryGet(transfer, out var memory) && memory.IsMemMove &&
            memory.LengthInBytes!.Value.GetZExtValue() == 8,
            "Grouping multiple slices must not remove or change the unsplittable overlapping memory operation.");
        LlvmMemIntrinsic.TryGet(transfer, out var preserved);
        var sourceOffset = LlvmAPInt.GetZero(64); var destinationOffset = LlvmAPInt.GetZero(64);
        var source = LlvmValueTracking.StripAndAccumulateConstantOffsets(preserved.RawSource, module.DataLayout, ref sourceOffset);
        var target = LlvmValueTracking.StripAndAccumulateConstantOffsets(preserved.RawDest, module.DataLayout, ref destinationOffset);
        Program.Require(ReferenceEquals(source, target) && destinationOffset - sourceOffset == new LlvmAPInt(64, 4),
            "The two slices of an overlapping transfer must keep their common object and ordered byte displacement.");
    }
}
