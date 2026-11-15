using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;

internal static class FunctionSsaJourney
{
    internal static async Task<int> RunAsync()
    {
        int executions = 0;
        foreach (string source in new[]
        {
            "fn choose(flag:Bool) { if flag { fn(value:Number) {value+1} } else {fn(value:Number) {value+2}} }; let first=choose(true); let second=choose(false); first(4)*10+second(4)",
            "let left=7; let right=9; fn choose(flag:Bool) {if flag {left} else {right}}; choose(true)*10+choose(false)",
            "let value=1; let selected=fn {value}; value=7; selected()"
        })
        foreach (int optimization in new[] { 0, 2 })
        {
            double expected = source.StartsWith("fn choose", StringComparison.Ordinal) ? 56 : source.StartsWith("let left", StringComparison.Ordinal) ? 79 : 7;
            var program = Compiler.Compile(source, "function-leaf-ssa.msync", optimization);
            using MemoryStream artifact = new(); NativeArtifact.Write(program, artifact); artifact.Position = 0;
            foreach (var executable in new[] { program, NativeArtifact.Read(artifact) })
            {
                await using var interpreter = new NativeInterpreter(executable);
                Program.Require((await interpreter.ExecuteAsync()).AsNumber() == expected,
                    "Function leaf SSA preserves callable selection and mutable capture locations.");
                ++executions;
            }
        }
        executions += await ExplicitLeavesAsync();
        return executions;
    }

    private static async Task<int> ExplicitLeavesAsync()
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "function-leaf-components.msync"));
        var context = input.Module.Context;
        var descriptor = LlvmStructType.GetTypeByName(context, "msync.function") ??
            LlvmStructType.Create(context, [LlvmPointerType.Get(context, input.Module.DataLayout.GetProgramAddressSpace()),
                LlvmPointerType.Get(context, input.Module.DataLayout.GetDefaultGlobalsAddressSpace()),
                LlvmPointerType.Get(context, input.Module.DataLayout.GetDefaultGlobalsAddressSpace())], "msync.function");
        var boolean = LlvmType.GetInt1Ty(context);
        var number = LlvmType.GetDoubleTy(context);
        var types = input.Types.ToList();
        int fnType = types.FindIndex(type => type.Kind == TypeKind.Function && type.Pointee < 0);
        if (fnType < 0) { fnType = types.Count; types.Add(new(TypeKind.Function)); }
        int boolType = types.FindIndex(type => type.Kind == TypeKind.Bool && type.Pointee < 0);
        if (boolType < 0) { boolType = types.Count; types.Add(new(TypeKind.Bool)); }
        var sourceFunctions = input.SourceFunctions.ToDictionary(pair => pair.Key, pair => pair.Value);
        LlvmFunction first = Leaf("leaf-seven", 7), second = Leaf("leaf-nine", 9);
        var factory = LlvmFunction.Create(LlvmFunctionType.Get(descriptor, [boolean]), LlvmLinkage.InternalLinkage,
            "leaf-phi-factory", input.Module);
        factory.Arguments[0].LogicalType = boolType;
        sourceFunctions.Add(factory, new(0, fnType));
        var entry = new LlvmBasicBlock(context, "entry", factory);
        var left = new LlvmBasicBlock(context, "left", factory);
        var right = new LlvmBasicBlock(context, "right", factory);
        var join = new LlvmBasicBlock(context, "join", factory);
        entry.Append(new LlvmCondBrInstruction(factory.Arguments[0], left, right));
        LlvmValue leftValue = Descriptor(left, first), rightValue = Descriptor(right, second);
        left.Append(new LlvmUncondBrInstruction(join)); right.Append(new LlvmUncondBrInstruction(join));
        var phi = new LlvmPhiInstruction(descriptor, 2) { LogicalType = fnType };
        phi.AddIncoming(leftValue, left); phi.AddIncoming(rightValue, right); join.Append(phi);
        var code = new LlvmExtractValueInstruction(phi, [0]);
        var environment = new LlvmExtractValueInstruction(phi, [1]);
        var name = new LlvmExtractValueInstruction(phi, [2]);
        join.Append(code); join.Append(environment); join.Append(name);
        LlvmValue packed = LlvmPoisonValue.Get(descriptor);
        foreach (var (value, field) in new[] { ((LlvmValue)code, 0U), (environment, 1U), (name, 2U) })
        {
            var inserted = new LlvmInsertValueInstruction(packed, value, [field]) { LogicalType = fnType };
            join.Append(inserted); packed = inserted;
        }
        join.Append(new LlvmReturnInstruction(context, packed));
        var lowered = LlvmNativeLowerer.Lower(input with { Types = types, SourceFunctions = sourceFunctions });
        using MemoryStream bytes = new(); NativeArtifact.Write(lowered, bytes); bytes.Position = 0;
        int executions = 0;
        foreach (var executable in new[] { lowered, NativeArtifact.Read(bytes) })
        {
            await using var interpreter = new NativeInterpreter(executable);
            int index = Enumerable.Range(0, executable.Functions.Count).Single(index => executable.Functions[index].Name == "leaf-phi-factory");
            foreach (bool choice in new[] { false, true })
            {
                NativeValue callable = await interpreter.InvokeAsync(NativeValue.FromFunction(new(index)), [NativeValue.FromBool(choice)]);
                Program.Require((await interpreter.InvokeAsync(callable, [])).AsNumber() == (choice ? 7 : 9),
                    "Code/environment/name extracts must survive PHI flattening and dynamic descriptor reassembly.");
                ++executions;
            }
        }
        return executions;

        LlvmFunction Leaf(string name, double result)
        {
            var function = LlvmFunction.Create(LlvmFunctionType.Get(number, []), LlvmLinkage.InternalLinkage, name, input.Module);
            var block = new LlvmBasicBlock(context, "entry", function);
            block.Append(new LlvmReturnInstruction(context, LlvmConstantFP.Get(context, new LlvmAPFloat(result))));
            return function;
        }

        LlvmValue Descriptor(LlvmBasicBlock block, LlvmFunction target)
        {
            LlvmValue value = LlvmPoisonValue.Get(descriptor);
            for (uint field = 0; field < 3; ++field)
            {
                LlvmValue component = field == 0 ? target : LlvmConstantPointerNull.Get((LlvmPointerType)descriptor.Elements[(int)field]);
                var insertion = new LlvmInsertValueInstruction(value, component, [field]) { LogicalType = fnType };
                block.Append(insertion); value = insertion;
            }
            return value;
        }
    }
}
