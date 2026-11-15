using Msync.Core.Llvm;
using Msync.Core.Native;
namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private bool ExtendedIntegerIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (!call.Type.IsIntegerTy(1)) return false;
            if (kind == LlvmIntrinsicKind.FShr)
                Add(new(NativeOp.Copy, Slot(call), ArgumentSlot(call, 1), Source: call.Source));
            else if (kind == LlvmIntrinsicKind.BitReverse || kind == LlvmIntrinsicKind.UShlSat || kind == LlvmIntrinsicKind.SShlSat)
                Add(new(NativeOp.Copy, Slot(call), ArgumentSlot(call, 0), Source: call.Source));
            else if (kind == LlvmIntrinsicKind.Abs)
            {
                if (call.GetArgument(1) is LlvmConstantInt { IsOne: true })
                    Add(new(NativeOp.Constant, Slot(call), owner.Constant(new(NativeKind.Bool, Boolean: false)), Source: call.Source));
                else Add(new(NativeOp.Copy, Slot(call), ArgumentSlot(call, 0), Source: call.Source));
            }
            else if (kind == LlvmIntrinsicKind.CTLZ)
            {
                if (call.GetArgument(1) is LlvmConstantInt { IsOne: true })
                    Add(new(NativeOp.Constant, Slot(call), owner.Constant(new(NativeKind.Bool, Boolean: false)), Source: call.Source));
                else Add(new(NativeOp.Unary, Slot(call), ArgumentSlot(call, 0), Immediate: (int)NativeUnary.Not, Source: call.Source));
            }
            else if (kind == LlvmIntrinsicKind.SMulH || kind == LlvmIntrinsicKind.UMulH)
                Add(new(NativeOp.Constant, Slot(call), owner.Constant(new(NativeKind.Bool, Boolean: false)), Source: call.Source));
            else if (kind == LlvmIntrinsicKind.UAddSat || kind == LlvmIntrinsicKind.SAddSat)
                Add(new(NativeOp.Binary, Slot(call), ArgumentSlot(call, 0), ArgumentSlot(call, 1), Immediate: (int)NativeBinary.BoolOr, Source: call.Source));
            else if (kind == LlvmIntrinsicKind.USubSat || kind == LlvmIntrinsicKind.SSubSat)
            {
                int inverse = Register(owner.Type(call));
                Add(new(NativeOp.Unary, inverse, ArgumentSlot(call, 1), Immediate: (int)NativeUnary.Not, Source: call.Source));
                Add(new(NativeOp.Binary, Slot(call), ArgumentSlot(call, 0), inverse, Immediate: (int)NativeBinary.BoolAnd, Source: call.Source));
            }
            else return false;
            trace?.Metric("intrinsic." + kind.Name, 1);
            return true;
        }
    }
}
