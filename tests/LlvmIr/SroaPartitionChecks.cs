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
        var result = LlvmSroa.Run(function, tree);
        Program.Require(result.Changed, "SROA must rewrite the source overlapping-copy partition graph.");
        LlvmSccp.Run(function);
        LlvmDeadCodeElimination.Run(function);
        var returned = (LlvmReturnInstruction)entry.GetTerminator();
        Program.Require(returned.ReturnValue is LlvmConstantInt value && value.GetZExtValue() == 3,
            "Presplitting must preserve memmove-like overlap ordering for both byte orders.");
        Program.Require(function.BasicBlocks.SelectMany(block => block.Instructions).All(instruction =>
            instruction is not LlvmAllocaInstruction and not LlvmLoadInstruction and not LlvmStoreInstruction),
            "The actual split-load/store graph must become promoted SSA rather than retain the original memory copy.");
    }
}
