using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class MachineLoweringJourney
{
    internal static async Task<int> RunAsync(string? output = null)
    {
        var input = LlvmHirLowerer.Lower(Compiler.Analyze("7", "machine-target.msync"));
        LlvmContext context = input.Module.Context;
        var i64 = LlvmType.GetInt64Ty(context);
        var i129 = LlvmIntegerType.Get(context, 129);
        var i8 = LlvmType.GetInt8Ty(context);
        var f32 = LlvmType.GetFloatTy(context);
        var f64 = LlvmType.GetDoubleTy(context);
        List<(string Name, NativeValue[] Arguments, NativeValue Expected)> cases = [];
        LlvmBasicBlock Function(string name, LlvmType result, params LlvmType[] parameters)
        {
            var function = LlvmFunction.Create(LlvmFunctionType.Get(result, parameters), LlvmLinkage.InternalLinkage, name, input.Module);
            return new(context, "entry", function);
        }
        void Return(LlvmBasicBlock block, LlvmInstruction instruction)
        {
            block.Append(instruction);
            block.Append(new LlvmReturnInstruction(context, instruction));
        }

        var add = Function("exact_i64_add", i64, i64);
        Return(add, new LlvmBinaryOperator(LlvmOpcode.Add, add.Parent!.Arguments[0], LlvmConstantInt.Get(i64, 1)));
        cases.Add(("exact_i64_add", [Integer(64, 1UL << 53)], Integer(64, (1UL << 53) + 1)));
        cases.Add(("exact_i64_add", [Integer(64, ulong.MaxValue)], Integer(64, 0)));

        var multiply = Function("exact_i129_mul", i129, i129, i129);
        Return(multiply, new LlvmBinaryOperator(LlvmOpcode.Mul, multiply.Parent!.Arguments[0], multiply.Parent.Arguments[1]));
        var high = LlvmAPInt.GetOneBitSet(129, 100) + new LlvmAPInt(129, 3);
        var factor = new LlvmAPInt(129, 7);
        cases.Add(("exact_i129_mul", [NativeValue.FromMachineInteger(high), NativeValue.FromMachineInteger(factor)],
            NativeValue.FromMachineInteger(high * factor)));

        var cast = Function("truncate_i64_i8", i8, i64);
        Return(cast, new LlvmCastInstruction(LlvmOpcode.Trunc, cast.Parent!.Arguments[0], i8));
        cases.Add(("truncate_i64_i8", [Integer(64, 0xfedcba98765432ab)], Integer(8, 0xab)));

        var compare = Function("signed_i64_compare", LlvmType.GetInt1Ty(context), i64, i64);
        Return(compare, new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, compare.Parent!.Arguments[0], compare.Parent.Arguments[1]));
        cases.Add(("signed_i64_compare", [Integer(64, ulong.MaxValue), Integer(64, 0)], NativeValue.True));

        var select = Function("select_i64", i64, LlvmType.GetInt1Ty(context), i64, i64);
        Return(select, new LlvmSelectInstruction(select.Parent!.Arguments[0], select.Parent.Arguments[1], select.Parent.Arguments[2]));
        foreach (bool condition in new[] { false, true })
            cases.Add(("select_i64", [NativeValue.FromBool(condition), Integer(64, 0x123456789abcdef0), Integer(64, 0xfedcba9876543210)],
                Integer(64, condition ? 0x123456789abcdef0UL : 0xfedcba9876543210UL)));

        var toDouble = Function("bits_to_double", f64, i64);
        Return(toDouble, new LlvmCastInstruction(LlvmOpcode.BitCast, toDouble.Parent!.Arguments[0], f64));
        cases.Add(("bits_to_double", [Integer(64, 0x3ff0000000000001)], NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(0x3ff0000000000001))));
        var fromDouble = Function("double_to_bits", i64, f64);
        Return(fromDouble, new LlvmCastInstruction(LlvmOpcode.BitCast, fromDouble.Parent!.Arguments[0], i64));
        cases.Add(("double_to_bits", [NativeValue.FromNumber(-0.0)], Integer(64, 0x8000000000000000)));

        var fpAdd = Function("single_add", f32, f32, f32);
        Return(fpAdd, new LlvmBinaryOperator(LlvmOpcode.FAdd, fpAdd.Parent!.Arguments[0], fpAdd.Parent.Arguments[1]));
        var one = LlvmAPFloat.GetOne(LlvmAPFloatSemantics.IEEEsingle);
        cases.Add(("single_add", [NativeValue.FromMachineFloat(one), NativeValue.FromMachineFloat(one)],
            NativeValue.FromMachineFloat(one.Add(one).Value)));

        var negate = Function("single_negate", f32, f32);
        Return(negate, new LlvmUnaryOperator(negate.Parent!.Arguments[0]));
        var negativeZero = LlvmAPFloat.GetZero(LlvmAPFloatSemantics.IEEEsingle, true);
        cases.Add(("single_negate", [NativeValue.FromMachineFloat(negativeZero)], NativeValue.FromMachineFloat(negativeZero.ChangeSign())));

        var memory = Function("machine_memory", i64, i64);
        var cell = new LlvmAllocaInstruction(i64, LlvmConstantInt.Get(i64, 1), alignment: new(8));
        memory.Append(cell);
        memory.Append(new LlvmStoreInstruction(memory.Parent!.Arguments[0], cell, new(8)));
        Return(memory, new LlvmLoadInstruction(i64, cell, new(8)));
        cases.Add(("machine_memory", [Integer(64, 0xdeadbeeffeed1234)], Integer(64, 0xdeadbeeffeed1234)));

        NativeProgram direct = LlvmNativeLowerer.Lower(input);
        using MemoryStream bytes = new();
        NativeArtifact.Write(direct, bytes); bytes.Position = 0;
        if (output is not null)
        {
            string path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes.ToArray());
        }
        int executions = 0;
        foreach (NativeProgram program in new[] { direct, NativeArtifact.Read(bytes) })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Off) });
            foreach (var test in cases)
            {
                int index = Enumerable.Range(0, program.Functions.Count).Single(index => program.Functions[index].Name == test.Name);
                NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(index)), test.Arguments);
                bool equal = actual.Kind switch
                {
                    NativeKind.MachineInteger => actual.AsMachineInteger() == test.Expected.AsMachineInteger(),
                    NativeKind.MachineFloat => actual.AsMachineFloat().BitwiseIsEqual(test.Expected.AsMachineFloat()),
                    NativeKind.Number => BitConverter.DoubleToUInt64Bits(actual.AsNumber()) == BitConverter.DoubleToUInt64Bits(test.Expected.AsNumber()),
                    _ => actual.Equals(test.Expected)
                };
                Program.Require(equal, "LLVM-to-Native machine value changed: " + test.Name);
                ++executions;
            }
        }
        return executions;
    }

    private static NativeValue Integer(uint width, ulong value) => NativeValue.FromMachineInteger(new(width, value));
}
