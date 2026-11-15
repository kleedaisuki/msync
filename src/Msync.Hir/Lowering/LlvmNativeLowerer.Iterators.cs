using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private void IteratorCall(LlvmCallInstruction call, LlvmIteratorOperation operation)
        {
            switch (operation)
            {
                case LlvmIteratorOperation.Open:
                    int iterator = iterators.Count;
                    iterators.Add(call, iterator);
                    Add(new(NativeOp.OpenIterator, A: ArgumentSlot(call, 0), B: iterator, Source: call.Source));
                    break;
                case LlvmIteratorOperation.Next: IteratorNext(call); break;
                case LlvmIteratorOperation.Close:
                    Add(new(NativeOp.CloseIterator, A: iterators[call.GetArgument(0)], Source: call.Source));
                    values.Add(call, Null());
                    break;
            }
        }
        private void IteratorNext(LlvmCallInstruction call)
        {
            var address = call.GetArgument(1);
            int itemType = owner.Types[owner.Type(address)].Pointee;
            int item = Register(itemType);
            int result = Slot(call);
            int next = code.Count;
            Add(new(NativeOp.NextIterator, item, iterators[call.GetArgument(0)], Source: call.Source));
            Add(new(NativeOp.Store, A: Slot(address), B: item, C: 0, Type: itemType, Source: call.Source));
            Add(new(NativeOp.Constant, result, owner.Constant(new(NativeKind.Bool, Boolean: true)), Source: call.Source));
            int jump = code.Count;
            Add(new(NativeOp.Jump, Source: call.Source));
            localBranches.Add((next, code.Count, true));
            Add(new(NativeOp.Constant, result, owner.Constant(new(NativeKind.Bool, Boolean: false)), Source: call.Source));
            localBranches.Add((jump, code.Count, false));
        }
    }
}
