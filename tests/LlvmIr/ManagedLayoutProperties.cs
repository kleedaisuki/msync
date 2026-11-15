using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime.Native;

internal static class ManagedLayoutProperties
{
    private const string Source = """
        let order = 0;
        fn record(value: Number) { order = order * 10 + value; value };
        let object = scope { value: 1 };
        let values = [record(1), record(2)];
        let aliases = [object, object];
        let callbacks = [fn { order = 999; 0 }];
        for entry in aliases { entry::value = entry::value + 1; };
        let read = fn { object::value };
        order * 10 + read()
        """;

    internal static int Run()
    {
        int checks = 0;
        var graph = LlvmHirLowerer.Lower(Compiler.Analyze(Source, "managed-layout.msync"));
        var layout = graph.Module.DataLayout;
        Require(layout.GetProgramAddressSpace() == 0 && layout.GetAllocaAddressSpace() == 1
            && layout.GetDefaultGlobalsAddressSpace() == 1, "The target producer's code and managed storage address spaces are distinct.");
        Require(layout.HasExternalState(1) && layout.HasUnstableRepresentation(1)
            && layout.MustNotIntroduceIntToPtr(1) && layout.MustNotIntroducePtrToInt(1)
            && !layout.IsNonIntegralAddressSpace(0), "The target uses actual peu1 flags, not the different legacy ni interpretation.");
        Require(graph.Module.Functions.All(function => function.AddressSpace == 0)
            && graph.Module.GlobalVariables.All(global => global.AddressSpace == 1),
            "Functions follow program AS0 while global data addresses follow module G1.");
        var instructions = graph.Module.Functions.SelectMany(function => function.BasicBlocks)
            .SelectMany(block => block.Instructions).ToArray();
        Require(instructions.OfType<LlvmAllocaInstruction>().All(allocation => allocation.AddressSpace == 1)
            && instructions.OfType<LlvmHeapAllocInst>().All(allocation => allocation.AddressSpace == 1),
            "Frame and heap storage are ordinary AS1 addresses, without casts at each use.");
        var functionType = LlvmStructType.GetTypeByName(graph.Module.Context, "msync.function")!;
        Require(functionType.Elements[0] is LlvmPointerType { AddressSpace: 0 }
            && functionType.Elements[1] is LlvmPointerType { AddressSpace: 1 }
            && functionType.Elements[2] is LlvmPointerType { AddressSpace: 1 },
            "A Function has code AS0, environment AS1 and name AS1 in its actual aggregate type.");
        foreach (var pair in graph.SourceFunctions)
        foreach (var capture in pair.Key.Arguments.Take(pair.Value.CaptureCount))
            Require(capture.Type is LlvmPointerType { AddressSpace: 1 }, "Capture parameters pass managed locations, not by-value snapshots.");
        Require(graph.Constructors.Where(pair => pair.Value == NativeKind.Range)
            .All(pair => pair.Key.ReturnType is LlvmPointerType { AddressSpace: 1 }),
            "Range constructor results remain ordinary managed builtin references.");
        var paths = LlvmHirLowerer.Lower(Compiler.Analyze("let value = ./source; [value]", "managed-path.msync"));
        var pathType = LlvmStructType.GetTypeByName(paths.Module.Context, "msync.path")!;
        Require(pathType.Elements.Skip(2).All(type => type is LlvmPointerType { AddressSpace: 1 }),
            "Path root fields carry the same explicit managed-reference representation.");
        var union = LlvmHirLowerer.Lower(Compiler.Analyze(
            "fn choose(flag: Bool) { if flag { scope { value: 1 } } else { \"text\" } }; choose(true)", "managed-union.msync"));
        var unions = union.Module.Functions.SelectMany(function => function.FunctionType.Parameters.Append(function.ReturnType))
            .OfType<LlvmStructType>().Where(type => type.HasName && type.Name.StartsWith("msync.union.", StringComparison.Ordinal)).ToArray();
        Require(unions.Length > 0 && unions.All(type => type.Elements[2] is LlvmArrayType { ElementType: LlvmPointerType { AddressSpace: 1 } }),
            "Union root arrays hold actual managed AS1 references.");
        StorageViews(false);
        StorageViews(true);
        return checks;

        void StorageViews(bool inheritedView)
        {
            var context = new LlvmContext();
            var module = new LlvmModule("partition-storage-view", context)
            { DataLayout = new("e-p:64:64:64:64-peu1:64:64:64:64-A1-G1") };
            var code = LlvmPointerType.Get(context, 0);
            var data = LlvmPointerType.Get(context, 1);
            var descriptor = LlvmStructType.Create(context, [code, data, data], "msync.function");
            var original = LlvmStructType.Get(context, [LlvmType.GetDoubleTy(context), descriptor]);
            var physical = inheritedView ? descriptor : original;
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "partition", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var allocation = new LlvmAllocaInstruction(physical, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1), 1, new(8))
            {
                AllocatedLogicalType = inheritedView ? 999 : 42,
                StorageView = inheritedView ? new(original, 42, 8) : null
            };
            entry.Append(allocation);
            var builder = new LlvmTransformBuilder(entry);
            builder.DisableGepCanonicalization();
            for (uint field = 0; field < 3; ++field)
            {
                LlvmValue[] indices = inheritedView
                    ? [LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), field)]
                    : [LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1),
                        LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), field)];
                var address = builder.CreateInBoundsGep(physical, allocation, indices);
                builder.CreateAlignedStore(LlvmConstantPointerNull.Get((LlvmPointerType)descriptor.Elements[(int)field]),
                    address, new(8), isVolatile: true);
            }
            builder.CreateRetVoid();
            LlvmSroa.Run(function, new(function));
            var partitions = entry.Instructions.OfType<LlvmAllocaInstruction>().ToArray();
            Require(partitions.Length == 3 && partitions.All(partition => partition.StorageView is { LogicalType: 42 }
                && ReferenceEquals(partition.StorageView.Value.StorageType, original)),
                "New SROA partitions retain original storage layout metadata, including an inherited source view.");
            Require(partitions.Select(partition => partition.StorageView!.Value.ByteOffset).Order().SequenceEqual(new ulong[] { 8, 16, 24 }),
                "Repeated partition offsets compose in original-storage byte coordinates, not per-partition field ordinals.");
            Require(partitions.All(partition => ((LlvmAllocaInstruction)partition.Clone()).StorageView == partition.StorageView),
                "Cloning an alloca preserves its immutable target storage view without adding an object identity.");
        }

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    internal static async Task<int> RunAsync()
    {
        int executions = 0;
        foreach (int optimization in new[] { 0, 2 })
        {
            NativeProgram program = Compiler.Compile(Source, "managed-range-location.msync", optimization);
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes); bytes.Position = 0;
            foreach (var executable in new[] { program, NativeArtifact.Read(bytes) })
            {
                await using NativeInterpreter interpreter = new(executable);
                Program.Require((await interpreter.ExecuteAsync()).AsNumber() == 123,
                    "Managed AS1 preserves eager Range initialization, stored Function values and captured/aliased locations through direct and Native2 execution.");
                ++executions;
            }
        }
        return executions;
    }
}
