using Msync.Core.Llvm;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void ValidateMachine(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        NativeCodeType Result() => program.Types[function.Slots[instruction.Result].Type];
        NativeCodeType Operand(int slot) { Register(function, instruction, slot); return program.Types[function.Slots[slot].Type]; }
        bool Same(int left, int right) => NativeTypeRelations.Same(program.Types, function.Slots[left].Type, function.Slots[right].Type);
        bool Integer(NativeCodeType type) => type.Pointee < 0 && (type.Machine?.IsInteger == true || !type.IsMachine && type.Kind == TypeKind.Bool);
        bool Floating(NativeCodeType type) => type.Pointee < 0 && (type.Machine?.IsFloating == true || !type.IsMachine && type.Kind == TypeKind.Number);
        uint Width(NativeCodeType type) => type.Machine?.BitWidth ?? (type.Kind == TypeKind.Bool ? 1U : 64U);
        void Require(bool valid, string cause) { if (!valid) throw Error(instruction, cause); }

        if (instruction.Op == NativeOp.MachineIntrinsic)
        {
            Require((ulong)instruction.Immediate <= byte.MaxValue && Enum.IsDefined((NativeMachineIntrinsic)instruction.Immediate),
                "Unknown machine intrinsic operation");
            var intrinsic = (NativeMachineIntrinsic)instruction.Immediate;
            IReadOnlyList<int> inputs = instruction.Inputs ?? [];
            Require(inputs.Count == NativeMachineOperations.ArgumentCount(intrinsic), "Machine intrinsic argument count disagrees with its operation");
            foreach (int input in inputs) Require(Integer(Operand(input)), "Machine integer intrinsic requires integer operands");
            if (NativeMachineOperations.IsOverflowFlag(intrinsic))
                RequireResult(program, function, instruction, TypeKind.Bool);
            else Require(Integer(Result()), "Machine integer intrinsic requires an integer result");
            bool flagArgument = intrinsic is NativeMachineIntrinsic.Abs or NativeMachineIntrinsic.CTLZ or NativeMachineIntrinsic.CTTZ;
            if (flagArgument) Require(Width(Operand(inputs[1])) == 1, "Machine intrinsic flag is not i1");
            else if (inputs.Count > 1) Require(Same(inputs[0], inputs[1]), "Machine intrinsic operand widths disagree");
            if (inputs.Count > 2) Require(Same(inputs[0], inputs[2]), "Machine funnel shift operand widths disagree");
            if (intrinsic is not (NativeMachineIntrinsic.SCmp or NativeMachineIntrinsic.UCmp) && !NativeMachineOperations.IsOverflowFlag(intrinsic))
                Require(Same(instruction.Result, inputs[0]), "Machine intrinsic result width disagrees with its operands");
            return;
        }
        NativeCodeType first = Operand(instruction.A), result = Result();
        if (instruction.Op == NativeOp.MachineUnary)
        {
            Require(instruction.Immediate == (int)LlvmOpcode.FNeg && Floating(first) && Same(instruction.A, instruction.Result),
                "Machine unary requires a matching floating FNeg result");
            return;
        }
        if (instruction.Op == NativeOp.MachineCast)
        {
            TypeIndex(program, instruction.Type, instruction.Source);
            Require(SameType(program, instruction.Type, function.Slots[instruction.Result].Type), "Machine cast result type disagrees with its register");
            Require((ulong)instruction.Immediate <= byte.MaxValue && NativeMachineOperations.IsCast((LlvmOpcode)instruction.Immediate), "Unknown scalar machine cast");
            var cast = (LlvmOpcode)instruction.Immediate;
            Require(cast switch
            {
                LlvmOpcode.Trunc => Integer(first) && Integer(result) && Width(first) > Width(result),
                LlvmOpcode.ZExt or LlvmOpcode.SExt => Integer(first) && Integer(result) && Width(first) < Width(result),
                LlvmOpcode.FPToUI or LlvmOpcode.FPToSI => Floating(first) && Integer(result),
                LlvmOpcode.UIToFP or LlvmOpcode.SIToFP => Integer(first) && Floating(result),
                LlvmOpcode.FPTrunc => Floating(first) && Floating(result) && Width(first) > Width(result),
                LlvmOpcode.FPExt => Floating(first) && Floating(result) && Width(first) < Width(result),
                LlvmOpcode.BitCast => (Integer(first) || Floating(first)) && (Integer(result) || Floating(result)) && Width(first) == Width(result),
                _ => false
            }, "Machine cast source and destination representations disagree");
            return;
        }
        NativeCodeType second = Operand(instruction.B);
        Require(Same(instruction.A, instruction.B), "Machine operation operand representations disagree");
        if (instruction.Op == NativeOp.MachineCompare)
        {
            Require((ulong)instruction.Immediate <= byte.MaxValue && Enum.IsDefined((LlvmCmpPredicate)instruction.Immediate), "Unknown machine comparison predicate");
            var predicate = (LlvmCmpPredicate)instruction.Immediate;
            Require(predicate >= LlvmCmpPredicate.ICmpEq ? Integer(first) : Floating(first), "Machine comparison operand class disagrees with its predicate");
            RequireResult(program, function, instruction, TypeKind.Bool);
            return;
        }
        Require((ulong)instruction.Immediate <= byte.MaxValue, "Unknown machine binary operation");
        var binary = (LlvmOpcode)instruction.Immediate;
        Require(NativeMachineOperations.IsIntegerBinary(binary) ? Integer(first) && Integer(second)
            : NativeMachineOperations.IsFloatingBinary(binary) && Floating(first) && Floating(second),
            "Machine binary operand class disagrees with its opcode");
        Require(Same(instruction.Result, instruction.A), "Machine binary result representation disagrees with its operands");
    }
}
