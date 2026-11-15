using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class MemoryMarkerExecution
{
    internal static async Task<int> RunAsync()
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "memory-markers.msync"));
        var types = input.Types.ToList();
        int number = types.FindIndex(type => type.Kind == TypeKind.Number && type.Pointee < 0);
        int pointer = types.FindIndex(type => type.Pointee == number);
        if (pointer < 0) { pointer = types.Count; types.Add(new(TypeKind.Number, Pointee: number)); }
        var context = input.Module.Context;
        var f64 = LlvmType.GetDoubleTy(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(f64), LlvmLinkage.InternalLinkage, "memory-markers", input.Module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var address = new LlvmAllocaInstruction(f64, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1), alignment: new(8))
        { LogicalType = pointer, AllocatedLogicalType = number };
        entry.Append(address);
        entry.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.LifetimeStart, [address], input.Module));
        entry.Append(new LlvmStoreInstruction(LlvmConstantFP.Get(f64, 42), address, new(8)));
        var launder = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.LaunderInvariantGroup, [address], input.Module);
        entry.Append(launder);
        var invariant = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.InvariantStart,
            [LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 8), launder], input.Module); entry.Append(invariant);
        var load = new LlvmLoadInstruction(f64, launder, new(8)) { LogicalType = number }; entry.Append(load);
        entry.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.FakeUse, [load, address], input.Module));
        entry.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.InvariantEnd,
            [invariant, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 8), address], input.Module));
        entry.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.LifetimeEnd, [address], input.Module));
        entry.Append(new LlvmReturnInstruction(context, load));
        var functions = input.SourceFunctions.ToDictionary(pair => pair.Key, pair => pair.Value);
        functions.Add(function, new(0, number));
        var program = LlvmNativeLowerer.Lower(input with { Types = types, SourceFunctions = functions });
        using MemoryStream artifact = new(); NativeArtifact.Write(program, artifact); artifact.Position = 0;
        foreach (var executable in new[] { program, NativeArtifact.Read(artifact) })
        {
            await using NativeInterpreter interpreter = new(executable, new() { Tiers = new(NativeJitPolicy.Off) });
            int index = Enumerable.Range(0, executable.Functions.Count).Single(index => executable.Functions[index].Name == function.Name);
            var result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(index)), []);
            Program.Require(result.AsNumber() == 42, "Native2 marker/launder graph preserves the actual loaded value.");
        }
        return 2;
    }
}
