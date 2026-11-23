using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private int readyFormerLifts;

        private bool TryReadyFormerLift(LlvmCallInstruction call)
        {
            HpirFutureOperation operation = geometry.LiftOperation(call);
            if (!HpirFutureImports.TryShape(operation, out int arity, out var input, out var result)
                || call.ArgumentCount != arity || !ScalarCarrier(call.Type, result)
                || call.Arguments.Any(argument => !ScalarCarrier(argument.Type, input) || FutureRoot(argument))) return false;

            HpirOp opcode = ReadyOpcode(operation);
            if (opcode == HpirOp.Invalid) return false;
            int left = Location(call.GetArgument(0)), output = Location(call);
            int right = arity == 2 ? Location(call.GetArgument(1)) : -1;
            if (operation == HpirFutureOperation.Not)
                right = ReadyBooleanOne(call.Source);
            Add(new(opcode, output, left, right), call.Source);
            if (operation == HpirFutureOperation.BoolEqual)
                Add(new(HpirOp.XorU8, output, output, ReadyBooleanOne(call.Source)), call.Source);
            ++readyFormerLifts;
            return true;
        }

        private int ReadyBooleanOne(SourceSpan? source)
        {
            int one = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, one, Immediate: 1), source);
            return one;
        }

        private static bool ScalarCarrier(LlvmType type, HpirWidth width) =>
            width == HpirWidth.U64 ? type.IsDoubleTy() : type.IsIntegerTy(1);

        private static HpirOp ReadyOpcode(HpirFutureOperation op) => op switch
        {
            HpirFutureOperation.Add => HpirOp.AddF64,
            HpirFutureOperation.Subtract => HpirOp.SubF64,
            HpirFutureOperation.Multiply => HpirOp.MulF64,
            HpirFutureOperation.Divide => HpirOp.DivF64,
            HpirFutureOperation.Remainder => HpirOp.RemF64,
            HpirFutureOperation.Negate => HpirOp.NegF64,
            HpirFutureOperation.Equal => HpirOp.EqF64,
            HpirFutureOperation.NotEqual => HpirOp.NeF64,
            HpirFutureOperation.Less => HpirOp.LtF64,
            HpirFutureOperation.LessEqual => HpirOp.LeF64,
            HpirFutureOperation.Greater => HpirOp.GtF64,
            HpirFutureOperation.GreaterEqual => HpirOp.GeF64,
            HpirFutureOperation.Not or HpirFutureOperation.BoolEqual or HpirFutureOperation.BoolNotEqual => HpirOp.XorU8,
            _ => HpirOp.Invalid
        };
    }
}
