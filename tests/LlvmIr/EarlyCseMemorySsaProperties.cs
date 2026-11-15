using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class EarlyCseMemorySsaProperties
{
    internal static int Run()
    {
        var nonMssa = CallFixture(12);
        LlvmEarlyCse.Run(nonMssa.Function);
        Program.Require(nonMssa.Later.Parent is not null,
            "The original non-MSSA API must retain source generation-only behavior across a write.");
        var inaccessible = CallFixture(12);
        var tree = new LlvmDominatorTree(inaccessible.Function);
        var metrics = new Dictionary<string, ulong>();
        var aa = new LlvmAAResults(inaccessible.Function, tree: tree, metrics: metrics);
        using var graph = new LlvmMemorySsa(inaccessible.Function, aa, tree, metrics: metrics);
        Program.Require(LlvmEarlyCse.Run(inaccessible.Function, true, tree: tree, memorySsa: graph),
            "MSSA EarlyCSE did not change redundant field loads across an inaccessible-memory demand call.");
        Program.Require(inaccessible.Later.Parent is null && inaccessible.Result.Left == inaccessible.Earlier &&
            inaccessible.Result.Right == inaccessible.Earlier && inaccessible.Call.Parent is not null,
            "Inaccessible state effects must not clobber accessible fields or remove the demand call.");
        Program.Require(graph.GetMemoryAccess(inaccessible.Later) is null &&
            graph.GetMemoryAccess(inaccessible.Earlier)?.DefiningAccess is not null,
            "EarlyCSE must update, but not dispose, caller-owned MemorySSA.");
        var clobber = CallFixture(0xfff);
        LlvmEarlyCse.Run(clobber.Function, true);
        Program.Require(clobber.Later.Parent is not null,
            "A call that can modify the accessed field must remain an actual MSSA clobber.");
        var capped = CallFixture(12);
        LlvmEarlyCse.Run(capped.Function, true, memorySsaOptimizationCap: 0);
        Program.Require(capped.Later.Parent is not null,
            "The source optimization-cap fallback must use defining accesses, not an uncapped replacement query.");
        var sibling = FieldStoreFixture();
        LlvmEarlyCse.Run(sibling.Function, true);
        Program.Require(sibling.Later.Parent is null && sibling.Result.Right == sibling.Earlier,
            "MSSA must use actual field-offset AA to bypass a write to a disjoint field.");
        var readonlyCalls = ReadonlyCallFixture();
        LlvmEarlyCse.Run(readonlyCalls.Function, true);
        Program.Require(readonlyCalls.Later.Parent is null && readonlyCalls.Result.Right == readonlyCalls.Earlier,
            "Readonly argument-memory calls must use the same source MSSA-generation query across inaccessible writes.");
        var pureCalls = ReadonlyCallFixture();
        pureCalls.Reader.MemoryEffectsAttribute = new(0);
        LlvmEarlyCse.Run(pureCalls.Function, true);
        Program.Require(pureCalls.Later.Parent is null,
            "Readnone call attributes must reach source SimpleValue CSE rather than remaining an obsolete unsupported path.");
        var coroutine = ReadonlyCallFixture();
        coroutine.Function.IsPresplitCoroutine = true;
        LlvmEarlyCse.Run(coroutine.Function, true);
        Program.Require(coroutine.Later.Parent is not null,
            "The source presplit-coroutine restriction must prevent call commoning.");
        return 10;
    }

    private sealed record Fixture(LlvmFunction Function, LlvmLoadInstruction Earlier, LlvmLoadInstruction Later,
        LlvmBinaryOperator Result, LlvmCallInstruction Call);

    private static Fixture CallFixture(uint memoryEffects)
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-mssa", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [pointer]), LlvmLinkage.InternalLinkage, "fields", module);
        var demand = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [pointer]),
            LlvmLinkage.ExternalLinkage, "generated.demand", module);
        demand.MemoryEffectsAttribute = new(memoryEffects);
        var block = new LlvmBasicBlock(context, "entry", function);
        var field = new LlvmGetElementPtrInstruction(integer, function.Arguments[0], [LlvmConstantInt.Get(integer, 1)]);
        var earlier = new LlvmLoadInstruction(integer, field, new(8));
        var call = new LlvmCallInstruction(demand.FunctionType, demand, [field]);
        var later = new LlvmLoadInstruction(integer, field, new(8));
        var result = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, later);
        block.Append(field); block.Append(earlier); block.Append(call); block.Append(later); block.Append(result);
        block.Append(new LlvmReturnInstruction(context, result));
        return new(function, earlier, later, result, call);
    }

    private sealed record CallsFixture(LlvmFunction Function, LlvmFunction Reader, LlvmCallInstruction Earlier,
        LlvmCallInstruction Later, LlvmBinaryOperator Result);

    private static CallsFixture ReadonlyCallFixture()
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-mssa-calls", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [pointer]), LlvmLinkage.InternalLinkage, "calls", module);
        var reader = LlvmFunction.Create(LlvmFunctionType.Get(integer, [pointer]), LlvmLinkage.ExternalLinkage, "reader", module);
        reader.MemoryEffectsAttribute = new(LlvmMemoryEffects.ArgMemOnly(LlvmModRefInfo.Ref).ToIntValue());
        var demand = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), []),
            LlvmLinkage.ExternalLinkage, "generated.demand", module);
        demand.MemoryEffectsAttribute = new(LlvmMemoryEffects.InaccessibleMemOnly().ToIntValue());
        var block = new LlvmBasicBlock(context, "entry", function);
        var earlier = new LlvmCallInstruction(reader.FunctionType, reader, [function.Arguments[0]]);
        var later = new LlvmCallInstruction(reader.FunctionType, reader, [function.Arguments[0]]);
        var result = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, later);
        block.Append(earlier); block.Append(new LlvmCallInstruction(demand.FunctionType, demand, []));
        block.Append(later); block.Append(result); block.Append(new LlvmReturnInstruction(context, result));
        return new(function, reader, earlier, later, result);
    }

    private static Fixture FieldStoreFixture()
    {
        var fixture = CallFixture(12);
        var block = fixture.Call.Parent!;
        var integer = (LlvmIntegerType)fixture.Earlier.Type;
        var root = fixture.Function.Arguments[0];
        var otherField = new LlvmGetElementPtrInstruction(integer, root, [LlvmConstantInt.Get(integer, 2)]);
        block.InsertBefore(otherField, fixture.Call);
        block.InsertBefore(new LlvmStoreInstruction(LlvmConstantInt.Get(integer, 11), otherField, new(8)), fixture.Call);
        fixture.Call.EraseFromParent();
        return fixture;
    }
}
