using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private const int SnapshotCompletionOffset = sizeof(ulong);
    private const int SnapshotCompletionStride = 2 * sizeof(ulong);

    private sealed partial class Body
    {
        private Dictionary<LlvmAllocaInstruction, bool>? completionItems;
        private readonly Dictionary<LlvmAllocaInstruction, int> frameCompletions = [];
        private int completionItemCount;

        private bool IsCompletionItem(LlvmValue value)
        {
            if (!geometry.HasCompletionVectors || value is not LlvmAllocaInstruction item
                || !item.AllocatedType.IsDoubleTy() && !item.AllocatedType.IsIntegerTy(1)) return false;
            completionItems ??= [];
            if (completionItems.TryGetValue(item, out bool known)) return known;
            known = item.Users.Any(user => IsCompletionPull(user, item));
            completionItems.Add(item, known);
            if (known) ++completionItemCount;
            return known;
        }

        private bool IsCompletionPull(LlvmUser user, LlvmAllocaInstruction item) =>
            user is LlvmCallInstruction { CalledFunction: { } target, ArgumentCount: 2 } next
            && next.GetArgument(1) == item && geometry.IteratorOperation(target, out var operation)
            && operation == LlvmIteratorOperation.Next
            && next.GetArgument(0) is LlvmCallInstruction { CalledFunction: { } entry, ArgumentCount: 1 } open
            && geometry.IteratorOperation(entry, out operation) && operation == LlvmIteratorOperation.Open
            && (AccessSnapshot(open) is { Completion: true } shape && item.AllocatedType == shape.Element
                || geometry.TransformShape(open.GetArgument(0)) is { } mapped
                    && geometry.FutureResult(mapped.Callback) && item.AllocatedType == mapped.Callback.ReturnType);

        private void WriteCompletionVectorItem(LlvmInstruction site, int vector, int offset, LlvmValue value)
        {
            var mode = FutureInput(value);
            if (mode != HpirFutureInput.Deferred)
                Copy(Width(value.Type), Indirect(vector, offset, site),
                    mode == HpirFutureInput.Either ? Payload(value) : Location(value), site.Source);
            if (mode != HpirFutureInput.Ready)
                Copy(HpirWidth.U64, Indirect(vector, checked(offset + SnapshotCompletionOffset), site), Location(value), site.Source);
        }

        private LlvmSnapshotCarrier? AccessSnapshot(LlvmCallInstruction call) =>
            geometry.SelectedCarrier(call) ?? geometry.SnapshotShape(call.GetArgument(0));
    }
}
