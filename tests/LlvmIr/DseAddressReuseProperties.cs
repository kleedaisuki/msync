using Msync.Core;
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class DseAddressReuseProperties
{
    internal static int Run()
    {
        RepeatedFields(0);
        RepeatedFields(1);
        DeletedAddressDependencies();
        MergedStores();
        ReadObserver();
        IdentifiedObjectsWithUnrelatedReads();
        UnknownObjects();
        return 7;
    }

    private static void RepeatedFields(uint addressSpace)
    {
        var f = Create(addressSpace);
        for (int index = -6; index < 6; ++index)
            Store(f, Address(f, index * 8), LlvmConstantInt.Get(f.Integer, (ulong)(index + 20)));
        f.Block.Append(new LlvmReturnInstruction(f.Context));
        string before = LlvmIrWriter.Write(f.Module);
        var metrics = Optimize(f);
        Program.Require(LlvmIrWriter.Write(f.Module) == before,
            "DSE must retain distinct visible fields, including negative offsets and nondefault pointer index widths.");
        foreach (string kind in new[] { "casts", "underlying", "offsets" })
        {
            ulong requests = metrics.GetValueOrDefault($"dse.address.{kind}.requests");
            ulong hits = metrics.GetValueOrDefault($"dse.address.{kind}.hits");
            ulong misses = metrics.GetValueOrDefault($"dse.address.{kind}.misses");
            Program.Require(misses <= 12 && requests == hits + misses,
                $"Field candidate indexing must decompose each {kind} fact at most once.");
        }
        Program.Require(metrics.GetValueOrDefault("dse.candidates.disjoint_definitions_skipped") == 66
            && metrics.GetValueOrDefault("dse.overwrite.queries") == 0,
            "Distinct field initialization must skip redundant candidate scans, not merely cache their decompositions.");
        Program.Require(metrics.GetValueOrDefault("dse.address.invalidated_entries") == 0,
            "An unchanged function must retain its address facts for the entire DSE invocation.");
    }

    private static void DeletedAddressDependencies()
    {
        var f = Create(0);
        var oldAddress = Address(f, -8);
        var old = Store(f, oldAddress, LlvmConstantInt.Get(f.Integer, 17));
        for (int index = 0; index < 8; ++index)
            Store(f, Address(f, index * 8), LlvmConstantInt.Get(f.Integer, (ulong)(index + 20)));
        var replacement = Store(f, Address(f, -8), LlvmConstantInt.Get(f.Integer, 91));
        f.Block.Append(new LlvmReturnInstruction(f.Context));
        var metrics = Optimize(f);
        Program.Require(old.Parent is null && oldAddress.Parent is null && replacement.Parent is not null,
            "An overwritten store and its dead address dependency must still be removed.");
        Program.Require(metrics.GetValueOrDefault("dse.address.invalidation.delete") >= 2
            && metrics.GetValueOrDefault("dse.address.invalidated_entries") > 0,
            "Recursive instruction deletion must invalidate previously reused address facts before rewriting operands.");
        string after = LlvmIrWriter.Write(f.Module);
        Optimize(f);
        Program.Require(LlvmIrWriter.Write(f.Module) == after,
            "Address facts must not survive into another DSE invocation.");
    }

    private static void MergedStores()
    {
        var f = Create(0);
        var wide = Store(f, Address(f, 0), LlvmConstantInt.Get(f.Integer, 0));
        var byteAddress = Address(f, 2);
        var narrow = Store(f, byteAddress, LlvmConstantInt.Get(LlvmType.GetInt8Ty(f.Context), 0xab));
        f.Block.Append(new LlvmReturnInstruction(f.Context));
        var metrics = Optimize(f);
        Program.Require(narrow.Parent is null && byteAddress.Parent is null
            && wide.ValueOperand is LlvmConstantInt merged && merged.Value.GetZExtValue() == 0xab0000,
            "Partial overwrite merging must keep the exact little-endian bytes and delete the narrow address dependency.");
        Program.Require(metrics.GetValueOrDefault("dse.stores.merged") == 1
            && metrics.GetValueOrDefault("dse.address.invalidation.store_merge") == 1,
            "Actual store merging must invalidate address facts before operand mutation.");
    }

    private static void ReadObserver()
    {
        var f = Create(0);
        var root = new LlvmGlobalVariable(f.Module, LlvmArrayType.Get(f.Integer, 16), false,
            LlvmLinkage.ExternalLinkage, name: "observed");
        var address = Address(f, 0, root);
        var first = Store(f, address, LlvmConstantInt.Get(f.Integer, 17));
        for (int index = 1; index < 8; ++index)
            Store(f, Address(f, index * 8, root), LlvmConstantInt.Get(f.Integer, (ulong)(index + 20)));
        var read = new LlvmLoadInstruction(f.Integer, address, new(1));
        f.Block.Append(read);
        Store(f, Address(f, 64, root), read);
        Store(f, Address(f, 0, root), LlvmConstantInt.Get(f.Integer, 91));
        f.Block.Append(new LlvmReturnInstruction(f.Context));
        var metrics = Optimize(f);
        Program.Require(first.Parent is not null && read.Parent is not null,
            "Candidate runs must not cross a read observing an earlier field, even when the read is attached to a different MemoryDef.");
        Program.Require(metrics.GetValueOrDefault("dse.candidates.memory_use_boundary")
            + metrics.GetValueOrDefault("dse.candidates.effect_boundary") > 0,
            "Candidate indexing must record the actual observing boundary.");
    }

    private static void IdentifiedObjectsWithUnrelatedReads()
    {
        var f = Create(0);
        var source = new LlvmGlobalVariable(f.Module, f.Integer, false, LlvmLinkage.ExternalLinkage, name: "source");
        for (int index = 0; index < 12; ++index)
        {
            var destination = new LlvmGlobalVariable(f.Module, f.Integer, false, LlvmLinkage.ExternalLinkage, name: $"field{index}");
            var read = new LlvmLoadInstruction(f.Integer, source, new(1));
            f.Block.Append(read);
            Store(f, destination, read);
        }
        f.Block.Append(new LlvmReturnInstruction(f.Context));
        string before = LlvmIrWriter.Write(f.Module);
        var metrics = Optimize(f);
        Program.Require(LlvmIrWriter.Write(f.Module) == before,
            "Independent identified objects and their source reads must retain their exact effects.");
        Program.Require(metrics.GetValueOrDefault("dse.candidates.disjoint_definitions_skipped") == 66
            && metrics.GetValueOrDefault("dse.overwrite.queries") == 0
            && metrics.GetValueOrDefault("dse.candidates.disjoint_read_checks") > 0,
            "Partitioned initialization must skip prior distinct objects across reads of an unrelated identified object.");
    }

    private static void UnknownObjects()
    {
        var f = Create(0, 2);
        for (int index = 0; index < 12; ++index)
            Store(f, Address(f, index * 8, f.Function.Arguments[index % 2]), LlvmConstantInt.Get(f.Integer, (ulong)(index + 20)));
        f.Block.Append(new LlvmReturnInstruction(f.Context));
        var metrics = Optimize(f);
        Program.Require(metrics.GetValueOrDefault("dse.candidates.disjoint_definitions_skipped") == 0,
            "Different argument roots may alias and must not share identified-object partitions.");
    }

    private sealed record Fixture(LlvmContext Context, LlvmModule Module, LlvmFunction Function,
        LlvmBasicBlock Block, LlvmIntegerType Integer);

    private static Fixture Create(uint addressSpace, int pointerCount = 1)
    {
        LlvmContext context = new();
        LlvmModule module = new("dse-address-reuse", context) { DataLayout = new("e-p:64:64-p1:64:64:64:32") };
        var integer = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context),
            Enumerable.Repeat<LlvmType>(LlvmPointerType.Get(context, addressSpace), pointerCount).ToArray()),
            LlvmLinkage.InternalLinkage, "fields", module);
        return new(context, module, function, new(context, "entry", function), integer);
    }

    private static LlvmGetElementPtrInstruction Address(Fixture f, long offset, LlvmValue? root = null)
    {
        var address = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(f.Context), root ?? f.Function.Arguments[0],
            [LlvmConstantInt.GetSigned(f.Integer, offset)]);
        f.Block.Append(address);
        return address;
    }

    private static LlvmStoreInstruction Store(Fixture f, LlvmValue pointer, LlvmValue value)
    {
        var store = new LlvmStoreInstruction(value, pointer, new(1));
        f.Block.Append(store);
        return store;
    }

    private static IReadOnlyDictionary<string, ulong> Optimize(Fixture f)
    {
        List<CompilationObservation> observations = [];
        CompilationTrace trace = new(observations.Add, includeOptimizerPasses: true);
        LlvmDominatorTree tree = new(f.Function), post = new(f.Function, isPostDominator: true);
        LlvmAAResults alias = new(f.Function, tree: tree);
        using LlvmMemorySsa memory = new(f.Function, alias, tree);
        LlvmDeadStoreElimination.Run(f.Function, alias, memory, tree, post, new(f.Module), new(f.Function), trace: trace);
        return observations.Single(record => record.Name == "optimizer.pass.llvm-dse").Metrics!;
    }
}
