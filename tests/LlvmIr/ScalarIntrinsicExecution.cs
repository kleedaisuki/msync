using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class ScalarIntrinsicExecution
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        foreach (bool simplify in new[] { false, true })
        {
            var input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "scalar-intrinsics.msync"));
            var types = input.Types.ToList();
            int boolean = types.FindIndex(type => type.Kind == TypeKind.Bool && type.Pointee < 0);
            if (boolean < 0) { boolean = types.Count; types.Add(new(TypeKind.Bool)); }
            var functions = input.SourceFunctions.ToDictionary(pair => pair.Key, pair => pair.Value);
            var i1 = LlvmType.GetInt1Ty(input.Module.Context);
            foreach (string name in new[] { "FShl", "CTPop", "CTTZ", "CTTZPoisonZero" })
            {
                int arity = name == "FShl" ? 3 : 1;
                var function = LlvmFunction.Create(LlvmFunctionType.Get(i1, Enumerable.Repeat<LlvmType>(i1, arity).ToArray()),
                    LlvmLinkage.InternalLinkage, name, input.Module);
                foreach (var argument in function.Arguments) argument.LogicalType = boolean;
                LlvmIntrinsicKind kind = name switch
                {
                    "FShl" => LlvmIntrinsicKind.FShl, "CTPop" => LlvmIntrinsicKind.CTPop, _ => LlvmIntrinsicKind.CTTZ
                };
                LlvmValue[] arguments = name.StartsWith("CTTZ", StringComparison.Ordinal)
                    ? [function.Arguments[0], LlvmConstantInt.GetBool(input.Module.Context, name == "CTTZPoisonZero")]
                    : function.Arguments.ToArray();
                var operation = LlvmIntrinsic.CreateIntrinsic(kind, arguments, input.Module);
                operation.LogicalType = boolean;
                LlvmBasicBlock body = new(input.Module.Context, "entry", function);
                body.Append(operation); body.Append(new LlvmReturnInstruction(input.Module.Context, operation));
                functions.Add(function, new(0, boolean));
                if (simplify) LlvmInstSimplifyPass.Run(function);
            }
            var original = LlvmNativeLowerer.Lower(input with { Types = types, SourceFunctions = functions });
            using MemoryStream artifact = new();
            NativeArtifact.Write(original, artifact); artifact.Position = 0;
            foreach (NativeProgram program in new[] { original, NativeArtifact.Read(artifact) })
            {
                await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Off) });
                foreach (bool value in new[] { false, true })
                {
                    await Check("CTPop", [value], value);
                    await Check("CTTZ", [value], !value);
                    if (value) await Check("CTTZPoisonZero", [true], false);
                    foreach (bool second in new[] { false, true })
                        foreach (bool shift in new[] { false, true })
                            await Check("FShl", [value, second, shift], value);
                }

                async Task Check(string name, bool[] values, bool expected)
                {
                    int index = Enumerable.Range(0, program.Functions.Count).Single(index => program.Functions[index].Name == name);
                    var callable = NativeValue.FromFunction(new(index));
                    var result = await interpreter.InvokeAsync(callable, values.Select(NativeValue.FromBool).ToArray());
                    if (result.AsBool() != expected) throw new InvalidOperationException("Native scalar intrinsic result: " + name);
                    ++checks;
                }
            }
        }
        return checks;
    }
}
