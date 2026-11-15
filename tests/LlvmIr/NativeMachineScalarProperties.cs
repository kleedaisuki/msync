using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class NativeMachineScalarProperties
{
    internal static int Run()
    {
        int checks = 0;
        foreach (uint width in new uint[] { 1, 7, 8, 9, 16, 31, 32, 63, 64, 65, 127, 128, 257 })
        {
            int alignment = width <= 8 ? 1 : width <= 16 ? 2 : width <= 32 ? 4 : 8;
            NativeMachineType descriptor = NativeMachineType.Integer(width, alignment);
            NativeType type = descriptor.PhysicalType;
            NativeLayout layout = new(type);
            NativeAllocation allocation = new(layout);
            LlvmAPInt bits = LlvmAPInt.GetAllOnes(width) ^ new LlvmAPInt(width, 1);
            NativeValue original = NativeValue.FromMachineInteger(bits);
            allocation.Field(0).Store(original);
            NativeValue read = allocation.Field(0).Load();
            Require(read.AsMachineInteger() == bits && read.MachineType == descriptor,
                "Machine storage must retain every bit and the compiled physical descriptor.");
            Require(layout.ReferenceCount == 0 && layout.RawSize >= (int)((width + 7) / 8),
                "Stored machine numbers own raw bytes, never managed value roots.");
            Require(original == read && original.GetHashCode() == read.GetHashCode(),
                "A value's integer width, not its slot alignment, defines its bits.");
            NativeAllocation copy = new(layout);
            NativeStorage.Copy(allocation.Field(0), copy.Field(0));
            Require(copy.Field(0).Load().AsMachineInteger() == bits, "Typed copy must retain wide integer payload bytes.");
            NativeConstant constant = NativeConstant.FromMachineInteger(bits, descriptor);
            Require(constant.ToMachineValue() == read && constant.MachineType == descriptor,
                "Portable integer constants must not round through source Number.");
            Require(original.OptionalSourceType is null && !NativeType.FromTypeRef(TypeRef.Any).Accepts(original),
                "Machine integers must not join the source Any universe.");
            NativeStorage.Clear(type, allocation.Field(0));
            Require(!allocation.Field(0).IsInitialized, "Clearing machine storage removes initialization state.");
        }
        foreach (var format in new[] { LlvmAPFloatSemantics.IEEEhalf, LlvmAPFloatSemantics.BFloat,
            LlvmAPFloatSemantics.IEEEsingle, LlvmAPFloatSemantics.IEEEdouble })
        {
            var descriptor = NativeMachineType.Floating(format, 1);
            foreach (var bits in new[] { LlvmAPFloat.GetZero(format, true).BitcastToAPInt(),
                LlvmAPInt.GetAllOnes(descriptor.BitWidth) })
            {
                var value = NativeValue.FromMachineFloat(format, bits);
                var allocation = new NativeAllocation(new NativeLayout(descriptor.PhysicalType));
                allocation.Field(0).Store(value);
                Require(allocation.Field(0).Load().AsMachineFloat().BitcastToAPInt() == bits,
                    "Raw floating storage must preserve signed zero and NaN payloads.");
                Require(NativeConstant.FromMachineFloat(value.AsMachineFloat()).ToMachineValue() == value,
                    "Portable floating constants preserve their exact format and payload.");
            }
        }
        var wide = NativeMachineType.Integer(64, 8);
        var numeric = new NativeAllocation(new NativeLayout(wide.PhysicalType));
        var pattern = NativeValue.FromMachineInteger(new(64, 0x0123456789ABCDEF));
        NativeStorage.WriteMachineScalar(wide, numeric.Address, pattern);
        var byteType = NativeMachineType.Integer(8);
        Require(NativeStorage.ReadMachineScalar(byteType, numeric.Address).AsMachineInteger().GetZExtValue() == 0xEF,
            "Little-endian byte projection must use physical byte offsets.");
        NativeStorage.WriteMachineScalar(wide, numeric.Address, pattern, littleEndian: false);
        Require(NativeStorage.ReadMachineScalar(byteType, numeric.Address).AsMachineInteger().GetZExtValue() == 1 &&
            NativeStorage.ReadMachineScalar(wide, numeric.Address, littleEndian: false) == pattern,
            "Big-endian raw projections must preserve the requested lane byte order.");
        var sourceNumber = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Number)));
        NativeStorage.WriteNumber(sourceNumber.Field(0), -0.0);
        Require(NativeStorage.ReadMachineScalar(wide, sourceNumber.Address).AsMachineInteger().GetZExtValue() == 0x8000000000000000,
            "A source Number store initializes its whole raw double representation for bitcast loads.");
        var partial = new NativeAllocation(new NativeLayout(wide.PhysicalType));
        NativeStorage.WriteMachineScalar(byteType, partial.Address, NativeValue.FromMachineInteger(new(8, 42)));
        Require(NativeStorage.ReadMachineScalar(wide, partial.Address).AsMachineInteger().GetZExtValue() == 42,
            "Raw widened views use stored bytes without imposing a new initialization scan on padding.");
        Require(NativeValue.FromBool(true).AsMachineInteger() == new LlvmAPInt(1, 1) &&
            NativeValue.FromNumber(-0.0).AsMachineFloat().BitcastToAPInt().GetZExtValue() == 0x8000000000000000,
            "Existing Bool and Number carriers retain exact i1/binary64 projections.");
        Require(!NativeMachineType.Integer(32).HasSameValueType(NativeMachineType.Integer(64)) &&
            !NativeMachineType.Floating(LlvmAPFloatSemantics.IEEEhalf).HasSameValueType(NativeMachineType.Floating(LlvmAPFloatSemantics.BFloat)),
            "Width and floating semantics are mandatory machine value identities.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    internal static async Task<int> RunArtifactAsync(string? sourceOnlyControl = null)
    {
        var i128 = NativeMachineType.Integer(128, 16);
        var half = NativeMachineType.Floating(LlvmAPFloatSemantics.IEEEhalf, 2);
        var bits = new LlvmAPInt(128, new ulong[] { 0xFEDCBA9876543210, 0x123456789ABCDEF0 });
        var fpBits = LlvmAPFloat.FromBits(LlvmAPFloatSemantics.IEEEhalf, 0xFE21);
        var functions = new[]
        {
            Function("entry", [new(0)], [new(NativeOp.Constant, 0, A: 2), new(NativeOp.Return, A: 0)], 0),
            Function("identity", [new(1, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], 1, [1]),
            Function("select", [new(3, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(1)],
                [new(NativeOp.Select, 3, A: 0, B: 1, C: 2), new(NativeOp.Return, A: 3)], 1, [3, 1, 1]),
            Function("wide-constant", [new(1)], [new(NativeOp.Constant, 0, A: 0, Type: 1), new(NativeOp.Return, A: 0)], 1),
            Function("half-constant", [new(2)], [new(NativeOp.Constant, 0, A: 1, Type: 2), new(NativeOp.Return, A: 0)], 2)
        };
        NativeProgram original = new([NativeConstant.FromMachineInteger(bits, i128), NativeConstant.FromMachineFloat(fpBits, half), new(NativeKind.Null)],
            functions, [new(TypeKind.Null), NativeCodeType.ForMachine(i128), NativeCodeType.ForMachine(half), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(original);
        using MemoryStream bytes = new();
        NativeArtifact.Write(original, bytes);
        Program.Require(bytes.ToArray()[14] == 4 && NativeArtifact.FormatVersion == 2,
            "Machine scalars must not change Native2 format or compact revision.");
        bytes.Position = 0;
        NativeProgram decoded = NativeArtifact.Read(bytes);
        Program.Require(decoded.Types[1].Machine == i128 && decoded.Types[2].Machine == half,
            "Existing type record fields must reconstruct exact machine descriptors.");
        int checks = 2;
        foreach (var program in new[] { original, decoded })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Off) });
            var input = NativeValue.FromMachineInteger(bits);
            var identity = await interpreter.InvokeAsync(Callable(1), [input]);
            Program.Require(identity.AsMachineInteger() == bits && identity.MachineType == i128,
                "Host arguments and returns must retain the machine physical descriptor."); ++checks;
            foreach (bool condition in new[] { false, true })
            {
                var result = await interpreter.InvokeAsync(Callable(2),
                    [NativeValue.FromBool(condition), input, NativeValue.FromMachineInteger(~bits)]);
                Program.Require(result.AsMachineInteger() == (condition ? bits : ~bits), "Existing Select must preserve wide machine values."); ++checks;
            }
            var constant = await interpreter.InvokeAsync(Callable(3), []);
            Program.Require(constant.AsMachineInteger() == bits, "Native2 wide constants must not use Number precision."); ++checks;
            var floating = await interpreter.InvokeAsync(Callable(4), []);
            Program.Require(floating.AsMachineFloat().BitwiseIsEqual(fpBits), "Native2 floating constants must preserve half NaN payloads."); ++checks;
        }
        if (sourceOnlyControl is not null)
        {
            byte[] oldBytes = await File.ReadAllBytesAsync(sourceOnlyControl);
            using MemoryStream control = new(oldBytes);
            NativeProgram oldProgram = NativeArtifact.Read(control);
            using MemoryStream roundtrip = new(); NativeArtifact.Write(oldProgram, roundtrip);
            Program.Require(oldBytes.AsSpan().SequenceEqual(roundtrip.ToArray()),
                "Existing source-only Native2 records must remain byte-for-byte unchanged."); ++checks;
        }
        return checks;

        static NativeValue Callable(int function) => NativeValue.FromFunction(new(function));
        static NativeFunctionCode Function(string name, NativeSlot[] slots, NativeInstruction[] instructions,
            int result, int[]? parameters = null) => new(name, slots, instructions,
                new(parameters ?? [], result, []), ParameterCount: parameters?.Length ?? 0)
            { Storage = new(new NativeSlotStorage[slots.Length], new NativeInstructionStorage[instructions.Length]) };
    }
}
