using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class EarlyCseInvariantProperties
{
    internal static int Run()
    {
        int checks = 0;
        foreach (var kind in new[] { LlvmIntrinsicKind.MemSet, LlvmIntrinsicKind.MemSetInline })
        {
            var integer = MemSetFixture(kind, "integer");
            LlvmEarlyCse.Run(integer.Function, true);
            Require(integer.Load.Parent is null && integer.Return.ReturnValue is LlvmConstantInt { IsZero: true } && integer.Fill.Parent is not null,
                "Actual zero memset must forward a sufficiently sized regular load without removing the earlier fill.");
        }
        foreach (string type in new[] { "double", "pointer", "fixed-vector" })
        {
            var fill = MemSetFixture(LlvmIntrinsicKind.MemSet, type);
            LlvmEarlyCse.Run(fill.Function, true);
            Require(fill.Load.Parent is null && fill.Return.ReturnValue is LlvmConstant { IsNullValue: true },
                "Zero-fill forwarding preserves the actual load's scalar/vector/pointer type.");
        }
        foreach (string rejection in new[] { "short", "nonzero", "variable-value", "variable-length", "volatile-fill", "volatile-load", "ordered-load", "scalable-vector" })
        {
            var fill = MemSetFixture(LlvmIntrinsicKind.MemSet, rejection);
            LlvmEarlyCse.Run(fill.Function, true);
            Require(fill.Load.Parent is not null, "Source memset precondition was lost: " + rejection);
        }
        var wideLength = MemSetFixture(LlvmIntrinsicKind.MemSet, "wide-length");
        LlvmEarlyCse.Run(wideLength.Function, false);
        Require(wideLength.Load.Parent is null, "Memset forwarding compares the actual APInt length, not a truncated host integer.");
        var unordered = MemSetFixture(LlvmIntrinsicKind.MemSet, "unordered-load");
        LlvmEarlyCse.Run(unordered.Function, true);
        Require(unordered.Load.Parent is null, "The pinned source permits an unordered atomic load on the zero-memset path.");

        foreach (string mode in new[] { "current-metadata", "earlier-metadata", "start-before", "start-between" })
        {
            var fixture = InvariantFixture(mode);
            LlvmEarlyCse.Run(fixture.Function, false);
            Require(fixture.Later.Parent is null && fixture.Result.Right == fixture.Earlier,
                "Actual invariant-generation facts must bypass an intervening writer: " + mode);
        }
        foreach (string mode in new[] { "none", "late-start", "finite-start", "different-size" })
        {
            var fixture = InvariantFixture(mode);
            LlvmEarlyCse.Run(fixture.Function, false);
            Require(fixture.Later.Parent is not null,
                "Source invariant scope/generation/exact-location precondition was lost: " + mode);
        }
        var scoped = SiblingFixture();
        LlvmEarlyCse.Run(scoped.Function, false);
        Require(scoped.Left.Parent is null && scoped.Right.Parent is not null,
            "Invariant scopes must pop at the dominator sibling boundary, not leak globally.");

        var metadata = InvariantFixture("earlier-metadata");
        var clone = (LlvmLoadInstruction)metadata.Earlier.Clone();
        Require(clone.HasInvariantLoadMetadata, "Load cloning must preserve actual !invariant.load metadata.");
        string text = LlvmIrWriter.Write(metadata.Function.Parent!);
        Require(text.Contains(", !invariant.load !0", StringComparison.Ordinal) && text.Contains("!0 = !{}", StringComparison.Ordinal),
            "Text IR must retain the actual invariant marker attachment and its empty metadata node.");

        LlvmLocal.CombineMetadataForCse(metadata.Earlier, metadata.Later, doesMove: false);
        Require(metadata.Earlier.HasInvariantLoadMetadata, "A stationary kept load retains its original invariant attachment.");
        LlvmLocal.CombineMetadataForCse(metadata.Earlier, metadata.Later, doesMove: true);
        Require(!metadata.Earlier.HasInvariantLoadMetadata, "A moved load cannot assert invariantness on an incoming path that lacks the metadata.");
        metadata.Earlier.HasInvariantLoadMetadata = metadata.Later.HasInvariantLoadMetadata = true;
        LlvmLocal.CombineMetadataForCse(metadata.Earlier, metadata.Later, doesMove: true);
        Require(metadata.Earlier.HasInvariantLoadMetadata, "Moving a common load preserves invariant metadata present on both source paths.");

        var assume = AssumeFixture();
        LlvmEarlyCse.Run(assume.Function, false);
        Require(assume.Later.Parent is null && assume.Return.ReturnValue == assume.Function.Arguments[0] && assume.Assume.Parent is not null,
            "Assume installs its simple condition in the scoped value table without treating it as a memory clobber.");

        foreach (var kind in new[] { LlvmIntrinsicKind.LifetimeStart, LlvmIntrinsicKind.LifetimeEnd })
        {
            var fixture = LifetimeFixture(kind, "only");
            LlvmEarlyCse.Run(fixture.Function, true);
            Require(fixture.Marker.Parent is null, "Source Local DCE removes markers when every pointer Use is a lifetime marker.");
            var poison = LifetimeFixture(kind, "poison");
            LlvmEarlyCse.Run(poison.Function, true);
            Require(poison.Marker.Parent is null, "Source Local DCE removes a lifetime marker on poison.");
            var live = LifetimeFixture(kind, "live");
            LlvmEarlyCse.Run(live.Function, false);
            Require(live.Marker.Parent is not null && live.Later!.Parent is not null,
                "A live lifetime marker is not skipped or treated as a readnone call; its actual source effects kill the generation.");
        }
        var fake = FakeUseFixture();
        LlvmEarlyCse.Run(fake.Function, false);
        Require(fake.Marker.Parent is not null && fake.Later.Parent is not null && fake.Store.Parent is not null,
            "Fake use retains its source effects, consumes LastStore and advances generation in non-MSSA EarlyCSE.");
        var fakeMssa = FakeUseFixture();
        LlvmEarlyCse.Run(fakeMssa.Function, true);
        Require(fakeMssa.Marker.Parent is not null && fakeMssa.Later.Parent is null && fakeMssa.Store.Parent is not null,
            "MSSA can bypass fake-use inaccessible state for a field load, but must not delete fake-use or bypass its LastStore boundary.");
        var launder = LifetimeFixture(LlvmIntrinsicKind.LaunderInvariantGroup, "only");
        LlvmEarlyCse.Run(launder.Function, true);
        Require(launder.Marker.Parent is null, "The source shared DCE predicate removes dead launder.invariant.group.");
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    private sealed record FillFixture(LlvmFunction Function, LlvmCallInstruction Fill, LlvmLoadInstruction Load, LlvmReturnInstruction Return);

    private static FillFixture MemSetFixture(LlvmIntrinsicKind kind, string mode)
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-memset", context);
        var i8 = LlvmType.GetInt8Ty(context);
        var i64 = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        LlvmType type = mode switch
        {
            "double" => LlvmType.GetDoubleTy(context),
            "pointer" => pointer,
            "fixed-vector" => LlvmFixedVectorType.Get(i8, 8),
            "scalable-vector" => LlvmScalableVectorType.Get(i8, 8),
            _ => i64
        };
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [pointer, i8, i64]),
            LlvmLinkage.InternalLinkage, "fill_then_load", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        LlvmValue length = mode == "variable-length" ? function.Arguments[2] : mode == "wide-length"
            ? LlvmConstantInt.Get(context, new LlvmAPInt(128, 1).Shl(100))
            : LlvmConstantInt.Get(i64, mode == "short" ? 7UL : 8UL);
        LlvmValue value = mode == "variable-value" ? function.Arguments[1] : LlvmConstantInt.Get(i8, mode == "nonzero" ? 1UL : 0UL);
        var fill = LlvmIntrinsic.CreateMemoryIntrinsic(kind,
            [function.Arguments[0], value, length, LlvmConstantInt.GetBool(context, mode == "volatile-fill")], module);
        var ordering = mode == "ordered-load" ? LlvmAtomicOrdering.Monotonic
            : mode == "unordered-load" ? LlvmAtomicOrdering.Unordered : LlvmAtomicOrdering.NotAtomic;
        var load = new LlvmLoadInstruction(type, function.Arguments[0], new(8), mode == "volatile-load", ordering);
        var ret = new LlvmReturnInstruction(context, load);
        block.Append(fill); block.Append(load); block.Append(ret);
        return new(function, fill, load, ret);
    }

    private sealed record LoadsFixture(LlvmFunction Function, LlvmLoadInstruction Earlier, LlvmLoadInstruction Later, LlvmBinaryOperator Result);

    private static LoadsFixture InvariantFixture(string mode)
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-invariant", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [pointer]), LlvmLinkage.InternalLinkage, "loads", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var earlier = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)) { HasInvariantLoadMetadata = mode == "earlier-metadata" };
        var later = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)) { HasInvariantLoadMetadata = mode == "current-metadata" };
        var result = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, later);
        LlvmCallInstruction? start = null;
        if (mode is "start-before" or "start-between" or "late-start" or "finite-start" or "different-size")
            start = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.InvariantStart,
                [LlvmConstantInt.Get(integer, mode == "different-size" ? 16UL : 8UL), function.Arguments[0]], module);
        if (mode is "start-before" or "finite-start" or "different-size") block.Append(start!);
        block.Append(earlier);
        if (mode == "start-between") block.Append(start!);
        block.Append(UnknownWriter(function));
        if (mode == "late-start") { block.Append(start!); block.Append(UnknownWriter(function)); }
        block.Append(later); block.Append(result);
        if (mode == "finite-start")
            block.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.InvariantEnd,
                [start!, LlvmConstantInt.Get(integer, 8), function.Arguments[0]], module));
        block.Append(new LlvmReturnInstruction(context, result));
        return new(function, earlier, later, result);
    }

    private static LlvmCallInstruction UnknownWriter(LlvmFunction function)
    {
        var module = function.Parent!;
        var writer = module.GetFunction(System.Text.Encoding.UTF8.GetBytes("writer")) ??
            LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(module.Context), [LlvmPointerType.Get(module.Context)]),
                LlvmLinkage.ExternalLinkage, "writer", module);
        return new(writer.FunctionType, writer, [function.Arguments[0]]);
    }

    private sealed record BranchFixture(LlvmFunction Function, LlvmLoadInstruction Left, LlvmLoadInstruction Right);

    private static BranchFixture SiblingFixture()
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-invariant-scope", context);
        var integer = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [LlvmPointerType.Get(context), LlvmType.GetInt1Ty(context)]),
            LlvmLinkage.InternalLinkage, "siblings", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var left = new LlvmBasicBlock(context, "left", function);
        var right = new LlvmBasicBlock(context, "right", function);
        var earlier = new LlvmLoadInstruction(integer, function.Arguments[0], new(8));
        entry.Append(earlier); entry.Append(new LlvmCondBrInstruction(function.Arguments[1], left, right));
        left.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.InvariantStart,
            [LlvmConstantInt.Get(integer, 8), function.Arguments[0]], module));
        left.Append(UnknownWriter(function));
        var leftLoad = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)); left.Append(leftLoad);
        var leftSum = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, leftLoad); left.Append(leftSum);
        left.Append(new LlvmReturnInstruction(context, leftSum));
        right.Append(UnknownWriter(function));
        var rightLoad = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)); right.Append(rightLoad);
        var rightSum = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, rightLoad); right.Append(rightSum);
        right.Append(new LlvmReturnInstruction(context, rightSum));
        return new(function, leftLoad, rightLoad);
    }

    private sealed record AssumeCase(LlvmFunction Function, LlvmCallInstruction Assume, LlvmICmpInstruction Later, LlvmReturnInstruction Return);

    private static AssumeCase AssumeFixture()
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-assume", context);
        var integer = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer, integer]), LlvmLinkage.InternalLinkage, "assume", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var first = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, function.Arguments[0], function.Arguments[1]);
        var later = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, function.Arguments[0], function.Arguments[1]);
        var assume = LlvmIntrinsic.CreateAssumption(first, module);
        var result = new LlvmSelectInstruction(later, function.Arguments[0], function.Arguments[1]);
        var ret = new LlvmReturnInstruction(context, result);
        block.Append(first); block.Append(assume); block.Append(later); block.Append(result); block.Append(ret);
        return new(function, assume, later, ret);
    }

    private sealed record MarkerFixture(LlvmFunction Function, LlvmCallInstruction Marker, LlvmLoadInstruction? Later);

    private static MarkerFixture LifetimeFixture(LlvmIntrinsicKind kind, string mode)
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-markers", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [pointer]), LlvmLinkage.InternalLinkage, "lifetime", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var marker = LlvmIntrinsic.CreateMemoryIntrinsic(kind,
            [mode == "poison" ? LlvmPoisonValue.Get(pointer) : function.Arguments[0]], module);
        LlvmLoadInstruction? later = null;
        if (mode == "live")
        {
            var earlier = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)); block.Append(earlier);
            block.Append(marker); later = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)); block.Append(later);
            var result = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, later); block.Append(result);
            block.Append(new LlvmReturnInstruction(context, result));
        }
        else { block.Append(marker); block.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(integer, 0))); }
        return new(function, marker, later);
    }

    private sealed record FakeCase(LlvmFunction Function, LlvmCallInstruction Marker, LlvmLoadInstruction Later, LlvmStoreInstruction Store);

    private static FakeCase FakeUseFixture()
    {
        LlvmContext context = new();
        LlvmModule module = new("early-cse-fake-use", context);
        var integer = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [LlvmPointerType.Get(context)]),
            LlvmLinkage.InternalLinkage, "fake_use", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var slot = new LlvmAllocaInstruction(integer, LlvmConstantInt.Get(integer, 1), alignment: new(8)); block.Append(slot);
        var earlier = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)); block.Append(earlier);
        var store = new LlvmStoreInstruction(LlvmConstantInt.Get(integer, 1), slot, new(8)); block.Append(store);
        var marker = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.FakeUse, [slot], module); block.Append(marker);
        block.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(integer, 2), slot, new(8)));
        var later = new LlvmLoadInstruction(integer, function.Arguments[0], new(8)); block.Append(later);
        var result = new LlvmBinaryOperator(LlvmOpcode.Add, earlier, later); block.Append(result);
        block.Append(new LlvmReturnInstruction(context, result));
        return new(function, marker, later, store);
    }
}
