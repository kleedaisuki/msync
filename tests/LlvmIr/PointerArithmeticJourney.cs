using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class PointerArithmeticJourney
{
    internal static async Task<int> RunAsync()
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("numbers", "let item=object { left:1, right:2 }; item::right=7; item::left+item::right", 8),
            ("mixed-roots", "let item=object { number:1, run:fn {4}, nested:object { value:6 } }; item::run=fn {7}; item::nested::value=9; item::number+item::run()+item::nested::value", 17),
            ("zero-sized-prefix", "let item=object { empty?:null, number:7, run:fn {4} }; item::number+item::run()", 11),
            ("scope-alias", "let original=object { value:1 }; let row=object { count:2, item:original }; row::item::value=7; original::value+row::count", 9)
        ];
        int executions = 0;
        foreach (bool combine in new[] { false, true })
            foreach (var test in cases)
            {
                var input = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, "pointer-" + test.Name + ".msync"));
                Canonicalize(input.Module, combine);
                var program = LlvmNativeLowerer.Lower(input);
                using MemoryStream artifact = new(); NativeArtifact.Write(program, artifact); artifact.Position = 0;
                foreach (var executable in new[] { program, NativeArtifact.Read(artifact) })
                {
                    await using NativeInterpreter interpreter = new(executable);
                    var result = await interpreter.ExecuteAsync();
                    Program.Require(result.AsNumber() == test.Expected,
                        $"Canonical GEP {test.Name}, combined={combine}: expected {test.Expected}, got {result.AsNumber()}.");
                    ++executions;
                }
            }
        await ScaledArrayAsync();
        await RepeatedLoweringAsync();
        return executions + 3;
    }

    private static void Canonicalize(LlvmModule module, bool combine)
    {
        var addresses = module.Functions.SelectMany(function => function.BasicBlocks).SelectMany(block => block.Instructions)
            .OfType<LlvmGetElementPtrInstruction>().ToArray();
        Program.Require(addresses.Length != 0, "The source journey must contain actual producer field GEPs.");
        foreach (var address in addresses)
        {
            LlvmTransformBuilder builder = new(address);
            Program.Require(builder.CanonicalizeGeps, "The actual builder's default GEP canonicalization must stay enabled.");
            LlvmValue replacement = builder.CreateGep(address.SourceElementType, address.PointerOperand, address.Indices.ToArray(), noWrapFlags: address.NoWrapFlags);
            if (combine)
            {
                uint width = module.DataLayout.GetIndexTypeSizeInBits(address.Type);
                replacement = builder.CreatePtrAdd(replacement, LlvmConstantInt.Get(address.Context, new LlvmAPInt(width, 3)));
                replacement = builder.CreatePtrAdd(replacement, LlvmConstantInt.Get(address.Context, new LlvmAPInt(width, unchecked((ulong)-3L), isSigned: true)));
            }
            address.ReplaceAllUsesWith(replacement); address.EraseFromParent();
        }
        Program.Require(module.Functions.SelectMany(function => function.BasicBlocks).SelectMany(block => block.Instructions)
            .OfType<LlvmGetElementPtrInstruction>().Any(address => address.SourceElementType.IsIntegerTy(8)),
            "The transformed real graph must contain canonical byte GEPs, not a disabled-canonicalization substitute.");
    }

    private static async Task ScaledArrayAsync()
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("let item=object { left:1, right:7 }; item::right", "scaled-array-gep.msync"));
        var address = input.Module.Functions.SelectMany(function => function.BasicBlocks).SelectMany(block => block.Instructions)
            .OfType<LlvmGetElementPtrInstruction>().Last(gep => gep.IndexCount == 2 && gep.GetIndex(1) is LlvmConstantInt { IsOne: true });
        LlvmTransformBuilder builder = new(address);
        uint width = input.Module.DataLayout.GetIndexTypeSizeInBits(address.Type);
        var replacement = builder.CreateScaledPtrAdd(address.PointerOperand, LlvmTypeSize.Fixed(4),
            LlvmConstantInt.Get(address.Context, new LlvmAPInt(width, 2)));
        Program.Require(replacement is LlvmGetElementPtrInstruction { SourceElementType: LlvmArrayType }, "Use the source builder's actual array-scaled pointer form.");
        address.ReplaceAllUsesWith(replacement); address.EraseFromParent();
        var program = LlvmNativeLowerer.Lower(input);
        using MemoryStream artifact = new(); NativeArtifact.Write(program, artifact); artifact.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(artifact));
        Program.Require((await interpreter.ExecuteAsync()).AsNumber() == 7, "An ordinary array-stride constant GEP must address the declared Number field.");
    }

    private static async Task RepeatedLoweringAsync()
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("0", "repeat-pointer-lowering.msync"));
        var types = input.Types.ToList();
        int boolean = types.Count; types.Add(new(Msync.Core.TypeKind.Bool));
        var layouts = input.Layouts.ToList();
        int layout = layouts.Count; layouts.Add(new([new("value", boolean)]));
        int scope = types.Count; types.Add(new(Msync.Core.TypeKind.Scope, Layouts: [layout]));
        var sourceType = LlvmStructType.Create(input.Module.Context, [LlvmType.GetInt1Ty(input.Module.Context)], "fresh.bool.cell");
        var structures = input.StructLayouts.ToDictionary(pair => pair.Key, pair => pair.Value); structures.Add(sourceType, layout);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetInt1Ty(input.Module.Context), []),
            LlvmLinkage.InternalLinkage, "repeat-bool-cell", input.Module);
        LlvmBasicBlock block = new(input.Module.Context, "entry", function);
        var allocation = new LlvmHeapAllocInst(LlvmConstantInt.Get(LlvmType.GetInt64Ty(input.Module.Context), 1), new LlvmAlign(1))
            { LogicalType = scope, LogicalLayout = layout };
        block.Append(allocation);
        LlvmTransformBuilder builder = new(block);
        var address = builder.CreateGep(sourceType, allocation,
            [LlvmConstantInt.Get(LlvmType.GetInt64Ty(input.Module.Context), 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(input.Module.Context), 0)]);
        builder.CreateStore(LlvmConstantInt.GetTrue(input.Module.Context), address);
        var load = builder.CreateLoad(LlvmType.GetInt1Ty(input.Module.Context), address); builder.CreateRet(load);
        var functions = input.SourceFunctions.ToDictionary(pair => pair.Key, pair => pair.Value); functions.Add(function, new(0, boolean));
        input = input with { Types = types, Layouts = layouts, StructLayouts = structures, SourceFunctions = functions };
        var instructions = input.Module.Functions.SelectMany(fn => fn.BasicBlocks).SelectMany(bb => bb.Instructions).ToArray();
        static int[] Annotations(IEnumerable<LlvmInstruction> code) => code.SelectMany(instruction =>
            new[] { instruction.LogicalType }.Concat(instruction.OperandUses.Select(use => use.LogicalType))).ToArray();
        int[] before = Annotations(instructions);
        for (int repetition = 0; repetition < 2; ++repetition)
        {
            var program = LlvmNativeLowerer.Lower(input);
            Program.Require(before.SequenceEqual(Annotations(instructions)), "Target-private pointer types must not leak into source graph annotations.");
            int index = Enumerable.Range(0, program.Functions.Count).Single(i => program.Functions[i].Name == "repeat-bool-cell");
            await using NativeInterpreter interpreter = new(program);
            Program.Require((await interpreter.InvokeAsync(NativeValue.FromFunction(new(index)), [])).AsBool(),
                "Repeated lowering must select its own fresh pointer carrier and preserve the stored Bool.");
        }
    }
}
