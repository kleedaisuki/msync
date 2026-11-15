using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SimplifyCfgSwitchChecks
{
    internal static int Run()
    {
        RangeComparison();
        DeadCases();
        SelectResults();
        ResultReplacement("linear", [2, 5, 8, 11, 14, 17], 32, false);
        ResultReplacement("bitmap", [3, 1, 7, 2, 11, 4], 8, true);
        ResultReplacement("lookup", [1000, 7, 90000, 12, 123456, 9999], 32, true);
        SparseRangeReduction();
        PowersOfTwo();
        UMinDefault();
        return 9;
    }

    private static void RangeComparison()
    {
        Fixture fixture = Create("range", [7, 8, 9], [1, 1, 1], commonPhi: false, sameDestination: true);
        var tree = new LlvmDominatorTree(fixture.Function);
        using var updater = new LlvmDomTreeUpdater(tree);
        Run(fixture, new() { ConvertSwitchRangeToICmp = true }, updater);
        Require(fixture.Switch.Parent is null,
            "contiguous switch becomes range branch");
        Require(Code(fixture).OfType<LlvmICmpInstruction>().Any(compare => compare.Predicate == LlvmCmpPredicate.ICmpUlt),
            "nonzero-based range uses source unsigned comparison");
        Require(tree.Dominates(fixture.Entry, fixture.Default), "incremental DT remains connected after range replacement");
    }

    private static void DeadCases()
    {
        Fixture fixture = Create("dead-cases", [0, 1, 2, 3, 5], [10, 20, 30, 40, 50], commonPhi: false);
        LlvmConstantInt mask = LlvmConstantInt.Get((LlvmIntegerType)fixture.Argument.Type, 3);
        LlvmBinaryOperator masked = new(LlvmOpcode.And, fixture.Argument, mask);
        fixture.Entry.InsertBefore(masked, fixture.Switch); fixture.Switch.Condition = masked;
        Run(fixture, new());
        Require(fixture.Switch.Parent is not null && fixture.Switch.CaseCount == 4 && fixture.Switch.DefaultDestUnreachable,
            "known bits prune dead cases then remove fully covered default");
    }

    private static void SelectResults()
    {
        Fixture fixture = Create("select", [3, 9], [10, 20]);
        Run(fixture, new());
        Require(fixture.Switch.Parent is null && Code(fixture).OfType<LlvmSelectInstruction>().Count() == 2,
            "two result cases and default become the source nested selects");
    }

    private static void ResultReplacement(string kind, ulong[] results, uint resultWidth, bool allowLookup)
    {
        Fixture fixture = Create(kind, [0, 1, 2, 3, 4, 5], results, resultWidth: resultWidth);
        var tree = new LlvmDominatorTree(fixture.Function);
        using var updater = new LlvmDomTreeUpdater(tree);
        Run(fixture, new() { ConvertSwitchToArithmetic = true, ConvertSwitchToLookupTable = allowLookup }, updater);
        Require(fixture.Switch.Parent is null, kind + " replaces the original switch");
        LlvmInstruction[] instructions = Code(fixture);
        if (kind == "linear") Require(instructions.OfType<LlvmBinaryOperator>().Any(binary => binary.Opcode == LlvmOpcode.Mul), "affine result map uses multiplication");
        if (kind == "bitmap") Require(instructions.OfType<LlvmBinaryOperator>().Any(binary => binary.Opcode == LlvmOpcode.LShr), "packed bitmap uses shifted extraction");
        if (kind == "lookup") Require(fixture.Module.GlobalVariables.Any(global => global.IsConstant && global.Name.StartsWith("switch.table.", StringComparison.Ordinal)) &&
            instructions.OfType<LlvmLoadInstruction>().Any(), "lookup replacement materializes and reads a constant table");
        foreach (LlvmBasicBlock block in fixture.Function.BasicBlocks)
            if (LlvmDominatorTree.Predecessors(block).Any() && tree.GetNode(block) is not null)
                Require(tree.Dominates(fixture.Entry, block), "source incremental DT includes replacement blocks");
    }

    private static void SparseRangeReduction()
    {
        Fixture fixture = Create("sparse", [5, 69, 133, 197], [10, 20, 30, 40], commonPhi: false);
        Run(fixture, new());
        Require(fixture.Switch.Condition is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } &&
            descriptor.Kind == LlvmIntrinsicKind.FShl, "sparse switch reduction uses source funnel-shift rotation");
        Require(Enumerable.Range(0, fixture.Switch.CaseCount).Select(i => fixture.Switch.GetCaseValue(i).Value.GetZExtValue()).Order().SequenceEqual(new ulong[] { 0, 1, 2, 3 }),
            "reduced cases become a dense range");
    }

    private static void PowersOfTwo()
    {
        Fixture fixture = Create("powers", [1, 2, 64, 128], [10, 20, 30, 40], commonPhi: false);
        Run(fixture, new());
        Require(fixture.Switch.Condition is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } && descriptor.Kind == LlvmIntrinsicKind.CTTZ,
            "power-of-two switch indexes by trailing zeros");
        Require(Code(fixture).OfType<LlvmCallBase>().Any(call => call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CTPop),
            "reachable default is protected by population-count guard");
        Require(fixture.Switch.Parent != fixture.Entry, "source split keeps translated switch in the new block");
    }

    private static void UMinDefault()
    {
        Fixture fixture = Create("umin", [0, 1, 2, 3, 4], [10, 20, 30, 40, 50], commonPhi: false);
        fixture.Default.GetTerminator().EraseFromParent(); fixture.Default.Append(new LlvmUnreachableInstruction(fixture.Module.Context));
        LlvmCallInstruction minimum = LlvmIntrinsic.CreateBinary(LlvmIntrinsicKind.UMin, fixture.Argument,
            LlvmConstantInt.Get((LlvmIntegerType)fixture.Argument.Type, 3), fixture.Module);
        fixture.Entry.InsertBefore(minimum, fixture.Switch); fixture.Switch.Condition = minimum;
        LlvmBasicBlock maximumDestination = fixture.Switch.GetCaseDestination(3);
        Run(fixture, new());
        Require(fixture.Switch.Condition == fixture.Argument && fixture.Switch.DefaultDestination == maximumDestination,
            "umin saturation case becomes ordinary input default");
    }

    private sealed record Fixture(LlvmModule Module, LlvmFunction Function, LlvmBasicBlock Entry,
        LlvmBasicBlock Default, LlvmSwitchInstruction Switch, LlvmValue Argument);

    private static Fixture Create(string name, ulong[] cases, ulong[] results, bool commonPhi = true,
        bool sameDestination = false, uint resultWidth = 32)
    {
        LlvmContext context = new();
        LlvmModule module = new(name, context) { DataLayout = new("e-n8:16:32:64") };
        LlvmIntegerType argumentType = LlvmType.GetInt8Ty(context), resultType = LlvmIntegerType.Get(context, resultWidth);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(resultType, [argumentType]), LlvmLinkage.InternalLinkage, name, module);
        LlvmBasicBlock entry = new(context, "entry", function), defaultBlock = new(context, "default", function);
        LlvmBasicBlock? common = commonPhi ? new(context, "join", function) : null;
        LlvmPhiInstruction? phi = commonPhi ? new(resultType) : null;
        LlvmSwitchInstruction selection = new(function.Arguments[0], defaultBlock, cases.Length);
        entry.Append(selection);
        LlvmBasicBlock? reused = null;
        for (int i = 0; i < cases.Length; ++i)
        {
            LlvmBasicBlock destination = sameDestination && reused is not null ? reused : new(context, "case", function);
            if (destination != reused || !sameDestination)
            {
                LlvmConstantInt value = LlvmConstantInt.Get(resultType, results[i]);
                if (common is null) destination.Append(new LlvmReturnInstruction(context, value));
                else { destination.Append(new LlvmUncondBrInstruction(common)); phi!.AddIncoming(value, destination); }
            }
            reused = destination;
            selection.AddCase(LlvmConstantInt.Get(argumentType, cases[i]), destination);
        }
        LlvmConstantInt defaultValue = LlvmConstantInt.Get(resultType, 99);
        if (common is null) defaultBlock.Append(new LlvmReturnInstruction(context, defaultValue));
        else
        {
            defaultBlock.Append(new LlvmUncondBrInstruction(common)); phi!.AddIncoming(defaultValue, defaultBlock);
            common.Append(phi); common.Append(new LlvmReturnInstruction(context, phi));
        }
        return new(module, function, entry, defaultBlock, selection, function.Arguments[0]);
    }

    private static void Run(Fixture fixture, LlvmSimplifyCfgOptions options, LlvmDomTreeUpdater? updater = null) =>
        LlvmSimplifyCfg.SimplifyCFG(fixture.Entry, new(fixture.Module.DataLayout), updater, options,
            tuning: new() { HoistCommon = false, SinkCommon = false });
    private static LlvmInstruction[] Code(Fixture fixture) => fixture.Function.BasicBlocks.SelectMany(block => block.Instructions).ToArray();
    private static void Require(bool condition, string cause)
    {
        if (!condition) throw new InvalidOperationException($"SimplifyCFG switch: {cause}");
    }
}
