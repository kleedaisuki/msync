using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class MachineExecutionChecks
{
    internal static async Task<int> RunAsync()
    {
        Program.Require((int)NativeOp.Select == 46 && (int)NativeOp.MachineBinary == 48 &&
            (int)NativeOp.MachineCompare == 49 && (int)NativeOp.MachineCast == 50 &&
            (int)NativeOp.MachineIntrinsic == 51 && (int)NativeOp.MachineUnary == 54,
            "Machine instruction selection must append wire identities without renumbering source opcodes.");
        NativeProgram original = BuildProgram();
        using MemoryStream bytes = new(); NativeArtifact.Write(original, bytes); bytes.Position = 0;
        int checks = 0;
        foreach (NativeProgram program in new[] { original, NativeArtifact.Read(bytes) })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Off) });
            NativeValue a64 = Integer(64, 0x8000000000000001), three = Integer(64, 3);
            await IntegerCheck("add64", [a64, three], new(64, 0x8000000000000004));
            await IntegerCheck("sub64", [a64, three], new(64, 0x7ffffffffffffffe));
            await IntegerCheck("mul64", [a64, three], new(64, 0x8000000000000003));
            await IntegerCheck("and64", [a64, three], new(64, 1));
            await IntegerCheck("or64", [a64, three], new(64, 0x8000000000000003));
            await IntegerCheck("xor64", [a64, three], new(64, 0x8000000000000002));
            await IntegerCheck("udiv64", [a64, three], new(64, 0x2aaaaaaaaaaaaaab));
            await IntegerCheck("sdiv64", [a64, three], new(64, 0xd555555555555556));
            await IntegerCheck("urem64", [a64, three], new(64, 0));
            await IntegerCheck("srem64", [a64, three], LlvmAPInt.GetAllOnes(64));
            await IntegerCheck("shl64", [a64, three], new(64, 8));
            await IntegerCheck("lshr64", [a64, three], new(64, 0x1000000000000000));
            await IntegerCheck("ashr64", [a64, three], new(64, 0xf000000000000000));
            await BoolCheck("slt64", [a64, three], true);
            await BoolCheck("ult64", [a64, three], false);
            await IntegerCheck("add257", [NativeValue.FromMachineInteger(new(257, [1UL, 0, 0, 0, 1UL])),
                Integer(257, 3)], new(257, [4UL, 0, 0, 0, 1UL]));
            await IntegerCheck("sext8", [Integer(8, 0x80)], new(64, 0xffffffffffffff80));
            await IntegerCheck("zext8", [Integer(8, 0x80)], new(64, 0x80));
            await IntegerCheck("trunc64", [Integer(64, 0x123480)], new(8, 0x80));
            await IntegerCheck("bitcastNumber", [NativeValue.FromNumber(1)], new(64, 0x3ff0000000000000));
            await IntegerCheck("fptosi64", [NativeValue.FromNumber(-17.75)], new(64, unchecked((ulong)-17L), isSigned: true));
            await NumberCheck("sitofp64", [Integer(64, ulong.MaxValue)], -1);
            await NumberCheck("uitofp64", [Integer(64, ulong.MaxValue)], 18446744073709551616.0);
            await FloatCheck("addHalf", [Floating(LlvmAPFloatSemantics.IEEEhalf, 0x3c00), Floating(LlvmAPFloatSemantics.IEEEhalf, 0x1400)],
                LlvmAPFloat.FromBits(LlvmAPFloatSemantics.IEEEhalf, 0x3c01));
            await FloatCheck("addBFloat", [Floating(LlvmAPFloatSemantics.BFloat, 0x3f80), Floating(LlvmAPFloatSemantics.BFloat, 0x3c00)],
                LlvmAPFloat.FromBits(LlvmAPFloatSemantics.BFloat, 0x3f81));
            await FloatCheck("addFloat", [Floating(LlvmAPFloatSemantics.IEEEsingle, 0x3f800000), Floating(LlvmAPFloatSemantics.IEEEsingle, 0x33800000)],
                LlvmAPFloat.FromBits(LlvmAPFloatSemantics.IEEEsingle, 0x3f800000));
            await FloatCheck("negFloat", [Floating(LlvmAPFloatSemantics.IEEEsingle, 0x7f800001)],
                LlvmAPFloat.FromBits(LlvmAPFloatSemantics.IEEEsingle, 0xff800001));
            await FloatCheck("fpTrunc", [NativeValue.FromNumber(1.0009765625)], LlvmAPFloat.FromBits(LlvmAPFloatSemantics.IEEEhalf, 0x3c01));
            await NumberCheck("fpExt", [Floating(LlvmAPFloatSemantics.IEEEhalf, 0x3c01)], 1.0009765625);
            NativeValue nan = Floating(LlvmAPFloatSemantics.IEEEsingle, 0x7fc00001);
            await BoolCheck("fcmpUno", [nan, Floating(LlvmAPFloatSemantics.IEEEsingle, 0)], true);
            await BoolCheck("fcmpOne", [nan, Floating(LlvmAPFloatSemantics.IEEEsingle, 0)], false);
            await IntegerCheck("pop64", [a64], new(64, 2));
            await IntegerCheck("ctlz64", [Integer(64, 1), NativeValue.False], new(64, 63));
            await IntegerCheck("cttz64", [Integer(64, 8), NativeValue.True], new(64, 3));
            await IntegerCheck("bswap64", [Integer(64, 0x0123456789abcdef)], new(64, 0xefcdab8967452301));
            await IntegerCheck("bitreverse8", [Integer(8, 0x13)], new(8, 0xc8));
            await IntegerCheck("uaddsat8", [Integer(8, 255), Integer(8, 1)], new(8, 255));
            await IntegerCheck("saddsat8", [Integer(8, 127), Integer(8, 1)], new(8, 127));
            await IntegerCheck("usubsat8", [Integer(8, 0), Integer(8, 1)], new(8, 0));
            await IntegerCheck("ssubsat8", [Integer(8, 128), Integer(8, 1)], new(8, 128));
            await IntegerCheck("ushlsat8", [Integer(8, 64), Integer(8, 2)], new(8, 255));
            await IntegerCheck("sshlsat8", [Integer(8, 64), Integer(8, 1)], new(8, 127));
            await IntegerCheck("umulh64", [Integer(64, ulong.MaxValue), Integer(64, 2)], new(64, 1));
            await IntegerCheck("smulh64", [Integer(64, ulong.MaxValue), Integer(64, 2)], new(64, ulong.MaxValue));
            await IntegerCheck("fshl8", [Integer(8, 0x12), Integer(8, 0x34), Integer(8, 4)], new(8, 0x23));
            await IntegerCheck("fshr8", [Integer(8, 0x12), Integer(8, 0x34), Integer(8, 4)], new(8, 0x23));
            await IntegerCheck("scmp64", [a64, three], new(8, 255));
            await IntegerCheck("ucmp64", [a64, three], new(8, 1));
            await IntegerCheck("uaddvalue8", [Integer(8, 255), Integer(8, 1)], new(8, 0));
            await BoolCheck("uaddflag8", [Integer(8, 255), Integer(8, 1)], true);
            await IntegerCheck("smulvalue8", [Integer(8, 64), Integer(8, 2)], new(8, 128));
            await BoolCheck("smulflag8", [Integer(8, 64), Integer(8, 2)], true);

            async Task<NativeValue> Invoke(string name, NativeValue[] arguments) => await interpreter.InvokeAsync(
                NativeValue.FromFunction(new(Enumerable.Range(0, program.Functions.Count).Single(index => program.Functions[index].Name == name))), arguments);
            async Task IntegerCheck(string name, NativeValue[] arguments, LlvmAPInt expected)
            { Program.Require((await Invoke(name, arguments)).AsMachineInteger() == expected, name + " lost machine integer bits."); ++checks; }
            async Task BoolCheck(string name, NativeValue[] arguments, bool expected)
            { Program.Require((await Invoke(name, arguments)).AsBool() == expected, name + " has the wrong predicate/overflow flag."); ++checks; }
            async Task NumberCheck(string name, NativeValue[] arguments, double expected)
            { Program.Require((await Invoke(name, arguments)).AsNumber() == expected, name + " crossed Number ABI incorrectly."); ++checks; }
            async Task FloatCheck(string name, NativeValue[] arguments, LlvmAPFloat expected)
            { Program.Require((await Invoke(name, arguments)).AsMachineFloat().BitwiseIsEqual(expected), name + " lost floating format/sign/payload bits."); ++checks; }
        }
        return checks;
    }

    private static NativeValue Integer(uint width, ulong bits) => NativeValue.FromMachineInteger(new(width, bits));
    private static NativeValue Floating(LlvmAPFloatSemantics format, ulong bits) => NativeValue.FromMachineFloat(LlvmAPFloat.FromBits(format, bits));

    private static NativeProgram BuildProgram()
    {
        List<NativeCodeType> types = [new(TypeKind.Null), new(TypeKind.Bool), new(TypeKind.Number)];
        int i8 = AddType(NativeMachineType.Integer(8)), i64 = AddType(NativeMachineType.Integer(64)), i257 = AddType(NativeMachineType.Integer(257));
        int half = AddType(NativeMachineType.Floating(LlvmAPFloatSemantics.IEEEhalf));
        int bfloat = AddType(NativeMachineType.Floating(LlvmAPFloatSemantics.BFloat));
        int single = AddType(NativeMachineType.Floating(LlvmAPFloatSemantics.IEEEsingle));
        List<NativeFunctionCode> functions = [new("main", [new(0)], [new(NativeOp.Constant, 0, A: 0), new(NativeOp.Return, A: 0)], new([], 0, []))
        { Storage = new(new NativeSlotStorage[1], new NativeInstructionStorage[2]) }];
        foreach ((string name, LlvmOpcode op) in new[] { ("add64", LlvmOpcode.Add), ("sub64", LlvmOpcode.Sub), ("mul64", LlvmOpcode.Mul), ("and64", LlvmOpcode.And), ("or64", LlvmOpcode.Or), ("xor64", LlvmOpcode.Xor), ("udiv64", LlvmOpcode.UDiv), ("sdiv64", LlvmOpcode.SDiv),
            ("urem64", LlvmOpcode.URem), ("srem64", LlvmOpcode.SRem), ("shl64", LlvmOpcode.Shl), ("lshr64", LlvmOpcode.LShr), ("ashr64", LlvmOpcode.AShr) })
            Binary(name, op, i64);
        Binary("add257", LlvmOpcode.Add, i257); Binary("addHalf", LlvmOpcode.FAdd, half);
        Binary("addBFloat", LlvmOpcode.FAdd, bfloat); Binary("addFloat", LlvmOpcode.FAdd, single);
        Compare("slt64", LlvmCmpPredicate.ICmpSlt, i64); Compare("ult64", LlvmCmpPredicate.ICmpUlt, i64);
        Compare("fcmpUno", LlvmCmpPredicate.FCmpUno, single); Compare("fcmpOne", LlvmCmpPredicate.FCmpOne, single);
        Cast("sext8", LlvmOpcode.SExt, i8, i64); Cast("zext8", LlvmOpcode.ZExt, i8, i64); Cast("trunc64", LlvmOpcode.Trunc, i64, i8);
        Cast("bitcastNumber", LlvmOpcode.BitCast, 2, i64); Cast("fptosi64", LlvmOpcode.FPToSI, 2, i64);
        Cast("sitofp64", LlvmOpcode.SIToFP, i64, 2); Cast("uitofp64", LlvmOpcode.UIToFP, i64, 2);
        Cast("fpTrunc", LlvmOpcode.FPTrunc, 2, half); Cast("fpExt", LlvmOpcode.FPExt, half, 2);
        Add("negFloat", [single], single, new(NativeOp.MachineUnary, 1, A: 0, Immediate: (int)LlvmOpcode.FNeg));
        Intrinsic("pop64", NativeMachineIntrinsic.CTPop, [i64], i64);
        Intrinsic("ctlz64", NativeMachineIntrinsic.CTLZ, [i64, 1], i64); Intrinsic("cttz64", NativeMachineIntrinsic.CTTZ, [i64, 1], i64);
        Intrinsic("bswap64", NativeMachineIntrinsic.BSwap, [i64], i64); Intrinsic("bitreverse8", NativeMachineIntrinsic.BitReverse, [i8], i8);
        foreach ((string name, NativeMachineIntrinsic op) in new[] { ("uaddsat8", NativeMachineIntrinsic.UAddSat), ("saddsat8", NativeMachineIntrinsic.SAddSat),
            ("usubsat8", NativeMachineIntrinsic.USubSat), ("ssubsat8", NativeMachineIntrinsic.SSubSat), ("ushlsat8", NativeMachineIntrinsic.UShlSat), ("sshlsat8", NativeMachineIntrinsic.SShlSat),
            ("uaddvalue8", NativeMachineIntrinsic.UAddOverflowValue), ("smulvalue8", NativeMachineIntrinsic.SMulOverflowValue) }) Intrinsic(name, op, [i8, i8], i8);
        Intrinsic("uaddflag8", NativeMachineIntrinsic.UAddOverflowFlag, [i8, i8], 1);
        Intrinsic("smulflag8", NativeMachineIntrinsic.SMulOverflowFlag, [i8, i8], 1);
        Intrinsic("umulh64", NativeMachineIntrinsic.UMulH, [i64, i64], i64); Intrinsic("smulh64", NativeMachineIntrinsic.SMulH, [i64, i64], i64);
        Intrinsic("fshl8", NativeMachineIntrinsic.FShl, [i8, i8, i8], i8); Intrinsic("fshr8", NativeMachineIntrinsic.FShr, [i8, i8, i8], i8);
        Intrinsic("scmp64", NativeMachineIntrinsic.SCmp, [i64, i64], i8); Intrinsic("ucmp64", NativeMachineIntrinsic.UCmp, [i64, i64], i8);
        return new([new(NativeKind.Null)], functions, types, []);

        int AddType(NativeMachineType type) { int index = types.Count; types.Add(NativeCodeType.ForMachine(type)); return index; }
        void Add(string name, int[] arguments, int result, NativeInstruction operation)
        {
            var slots = arguments.Select(type => new NativeSlot(type, NativeSlotRole.Parameter)).Append(new(result)).ToArray();
            functions.Add(new(name, slots, [operation, new(NativeOp.Return, A: arguments.Length)], new(arguments, result, []), ParameterCount: arguments.Length)
            { Storage = new(new NativeSlotStorage[slots.Length], new NativeInstructionStorage[2]) });
        }
        void Binary(string name, LlvmOpcode opcode, int type) => Add(name, [type, type], type, new(NativeOp.MachineBinary, 2, A: 0, B: 1, Immediate: (int)opcode));
        void Compare(string name, LlvmCmpPredicate predicate, int type) => Add(name, [type, type], 1, new(NativeOp.MachineCompare, 2, A: 0, B: 1, Immediate: (int)predicate));
        void Cast(string name, LlvmOpcode opcode, int input, int result) => Add(name, [input], result, new(NativeOp.MachineCast, 1, A: 0, Type: result, Immediate: (int)opcode));
        void Intrinsic(string name, NativeMachineIntrinsic operation, int[] arguments, int result) =>
            Add(name, arguments, result, new(NativeOp.MachineIntrinsic, arguments.Length, Immediate: (int)operation, Inputs: Enumerable.Range(0, arguments.Length).ToArray()));
    }
}
