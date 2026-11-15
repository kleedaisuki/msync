using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private void Binary(LlvmBinaryOperator binary)
        {
            NativeBinary? operation = binary.Opcode switch
            {
                LlvmOpcode.FAdd when binary.Type.IsDoubleTy() => NativeBinary.Add,
                LlvmOpcode.FSub when binary.Type.IsDoubleTy() => NativeBinary.Subtract,
                LlvmOpcode.FMul when binary.Type.IsDoubleTy() => NativeBinary.Multiply,
                LlvmOpcode.FDiv when binary.Type.IsDoubleTy() => NativeBinary.Divide,
                LlvmOpcode.FRem when binary.Type.IsDoubleTy() => NativeBinary.Remainder,
                LlvmOpcode.Xor when binary.Type.IsIntegerTy(1) => NativeBinary.NotEqual,
                LlvmOpcode.And when binary.Type.IsIntegerTy(1) => NativeBinary.BoolAnd,
                LlvmOpcode.Or when binary.Type.IsIntegerTy(1) => NativeBinary.BoolOr,
                _ => null
            };
            if (operation is null) { MachineBinary(binary); return; }
            Add(new(NativeOp.Binary, Slot(binary), OperandSlot(binary, 0), OperandSlot(binary, 1),
                Immediate: (int)operation, Source: binary.Source));
            if (trace is not null) trace.Metric("binary." + operation, 1);
        }
        private void Compare(LlvmCmpInstruction comparison)
        {
            if (comparison.Left.Type.IsIntegerTy() && !comparison.Left.Type.IsIntegerTy(1) ||
                comparison.Left.Type.IsFloatingPointTy() && !comparison.Left.Type.IsDoubleTy())
            { MachineCompare(comparison); return; }
            NativeBinary? operation = comparison.Predicate switch
            {
                LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.ICmpEq => NativeBinary.Equal,
                LlvmCmpPredicate.FCmpUne or LlvmCmpPredicate.ICmpNe => NativeBinary.NotEqual,
                LlvmCmpPredicate.FCmpOgt => NativeBinary.Greater,
                LlvmCmpPredicate.FCmpOge => NativeBinary.GreaterEqual,
                LlvmCmpPredicate.FCmpOlt => NativeBinary.Less,
                LlvmCmpPredicate.FCmpOle => NativeBinary.LessEqual,
                LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSgt => NativeBinary.Less,
                LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge => NativeBinary.LessEqual,
                LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSlt => NativeBinary.Greater,
                LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle => NativeBinary.GreaterEqual,
                _ => null
            };
            if (operation is null) { MachineCompare(comparison); return; }
            Add(new(NativeOp.Binary, Slot(comparison), OperandSlot(comparison, 0), OperandSlot(comparison, 1),
                Immediate: (int)operation, Source: comparison.Source));
        }

        private void Select(LlvmSelectInstruction select)
        {
            if (TryAggregateSelect(select)) return;
            if (!select.Condition.Type.IsIntegerTy(1)) throw Unsupported(select, "non-scalar select condition");
            int type = SelectedValueType(select);
            Add(new(NativeOp.Select, Slot(select), OperandSlot(select, 0),
                OperandSlot(select, 1, type), OperandSlot(select, 2, type), Source: select.Source));
        }

        private bool Intrinsic(LlvmCallInstruction call)
        {
            if (call.CalledFunction?.IntrinsicDescriptor is not { } descriptor) return false;
            if (MemoryMarkerIntrinsic(call, descriptor)) return true;
            if (descriptor.IsAssume)
            {
                trace?.Metric("intrinsic.assume_elided", 1);
                return true;
            }
            if (!call.Type.IsIntegerTy(1) && MachineIntegerIntrinsic(call, descriptor.Kind)) return true;
            if (ExtendedIntegerIntrinsic(call, descriptor.Kind)) return true;
            if (!call.Type.IsIntegerTy(1)) throw Unsupported(call, "integer intrinsic carrier " + call.Type);
            var kind = descriptor.Kind;
            if (kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.CTPop)
            {
                Add(new(NativeOp.Copy, Slot(call), ArgumentSlot(call, 0), Source: call.Source));
                trace?.Metric("intrinsic." + kind.Name, 1);
                return true;
            }
            if (kind == LlvmIntrinsicKind.CTTZ)
            {
                if (call.GetArgument(1) is LlvmConstantInt { IsOne: true })
                    Add(new(NativeOp.Constant, Slot(call), owner.Constant(new(NativeKind.Bool, Boolean: false)), Source: call.Source));
                else
                    Add(new(NativeOp.Unary, Slot(call), ArgumentSlot(call, 0), Immediate: (int)NativeUnary.Not, Source: call.Source));
                trace?.Metric("intrinsic." + kind.Name, 1);
                return true;
            }
            NativeBinary operation = kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.UMax ? NativeBinary.BoolOr
                : kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMin ? NativeBinary.BoolAnd
                : throw Unsupported(call, "intrinsic " + kind.Name);
            Add(new(NativeOp.Binary, Slot(call), ArgumentSlot(call, 0), ArgumentSlot(call, 1),
                Immediate: (int)operation, Source: call.Source));
            trace?.Metric("intrinsic." + kind.Name, 1);
            return true;
        }
    }
}
