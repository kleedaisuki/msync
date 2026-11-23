using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal bool StreamsRange(HirFunctionId id) => session is { } && hir.Entry == id
            && hir.Types[hir.Functions[id.Value].Result.Value] is { RuntimeType.Kind: TypeKind.Range, Nullable: false };
    }

    private sealed partial class Body
    {
        private LlvmValue EntryResult(LlvmValue value)
        {
            if (!owner.StreamsRange(Hir.Entry) || !ReferenceEquals(function, owner.functions[Hir.Entry.Value]))
                return Represent(value, source.Result);
            var range = Hir.Types[source.Result.Value];
            if (range.Element >= 0)
            {
                var item = new HirTypeId(range.Element);
                var type = Hir.Types[item.Value];
                StreamRange(value, StackCell(item), type.RuntimeType.Kind | (type.Nullable ? TypeKind.Null : 0));
            }
            else
            {
                TypeRef item = range.RuntimeType.Element ?? TypeRef.Any;
                StreamRange(value, StackCell(owner.RepresentationType(item.Kind), owner.metadata.RuntimeType(item)), item.Kind);
            }
            return owner.Null;
        }

        private void StreamRange(LlvmValue producer, LlvmAllocaInstruction item, TypeKind kind)
        {
            var cursor = IteratorCall(LlvmIteratorOperation.Open, owner.DataPointer, [producer]);
            var next = new LlvmBasicBlock(owner.context, "display.next", function);
            var show = new LlvmBasicBlock(owner.context, "display.item", function);
            var close = new LlvmBasicBlock(owner.context, "display.close", function);
            Add(new LlvmUncondBrInstruction(next));
            block = next;
            var found = IteratorCall(LlvmIteratorOperation.Next, LlvmIntegerType.Get(owner.context, 1), [cursor, item]);
            found.LogicalType = owner.metadata.RuntimeType(TypeRef.Bool);
            Add(new LlvmCondBrInstruction(found, show, close));
            block = show;
            OperationCall(new(LlvmRuntimeValueOperation.SessionRangeItem, (int)kind), LlvmType.GetVoidTy(owner.context),
                owner.metadata.RuntimeType(TypeRef.Null), [item]);
            Add(new LlvmUncondBrInstruction(next));
            block = close;
            var end = IteratorCall(LlvmIteratorOperation.Close, owner.Null.Type, [cursor]);
            end.LogicalType = owner.metadata.RuntimeType(TypeRef.Null);
            owner.trace?.Detail?.Metric("session.range_display", 1);
        }
    }
}
