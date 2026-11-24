using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private enum FutureUnionUse : byte { None, Forward, Observe }
        private const TypeKind CompletionMembers = TypeKind.Number | TypeKind.Bool;
        private HashSet<LlvmValue>? unionCells;

        private bool UnionCell(LlvmValue value) => unionCells?.Contains(value) == true;

        private FutureUnionUse UnionFlow(LlvmValue value, LlvmUser user)
        {
            var into = geometry.Union(user.Type);
            bool carrier = UnionCell(value);
            if (user is LlvmPhiInstruction or LlvmSelectInstruction)
                return carrier && (into & CompletionMembers) != 0 ? FutureUnionUse.Forward : FutureUnionUse.None;
            if (user is not LlvmCallInstruction { ArgumentCount: 1 } call || call.GetArgument(0) != value)
                return FutureUnionUse.None;
            var operation = geometry.RepresentationOperation(call);
            if (operation == LlvmRuntimeConversionOperation.IsNull) return FutureUnionUse.Observe;
            if (operation != LlvmRuntimeConversionOperation.Represent) return FutureUnionUse.None;
            if ((into & CompletionMembers) != 0 || carrier && (geometry.Member(call, CompletionMembers) & CompletionMembers) != 0)
                return FutureUnionUse.Forward;
            return FutureUnionUse.None;
        }

        private bool UnionFutureUse(LlvmValue value, LlvmUser user)
        {
            if (UnionFlow(value, user) != FutureUnionUse.None) return true;
            if (user is LlvmStoreInstruction store && store.ValueOperand == value)
                return geometry.FutureStorageDelta(store.GetOperandUse(1)) is not null || geometry.ReadySessionUnionStore(store);
            if (user is LlvmReturnInstruction)
                return geometry.FutureUnionResult(function) || geometry.IsEntry(function);
            if (user is not LlvmCallInstruction { CalledFunction: { } target } call) return false;
            if (geometry.RepresentationOperation(call) == LlvmRuntimeConversionOperation.Represent)
                return geometry.Member(call, CompletionMembers) == TypeKind.Never;
            if (target.IsDeclaration) return false;
            for (int index = 0; index < call.ArgumentCount; ++index)
                if (call.GetArgument(index) == value && !geometry.FutureUnionParameter(target.Arguments[index])) return false;
            return true;
        }

        private int UnionTail(LlvmType type) => checked(((int)Layout.GetTypeAllocSize(type).GetFixedValue() + 7) & -8);

        private int UnionCompletion(LlvmValue value) =>
            UnionCell(value) ? checked(RecordLocation(value) + UnionTail(value.Type)) : absentFuture;

        private int ObserveSessionUnion(LlvmInstruction site, LlvmValue value)
        {
            int payload = Reserve(8, 8);
            Copy(HpirWidth.U64, payload, LeafLocation(value, geometry.FieldOffset(value.Type, 1)), site.Source);
            TypeKind members = geometry.Union(value.Type);
            if ((members & TypeKind.Number) != 0) Observe(TypeKind.Number, HpirWidth.U64);
            if ((members & TypeKind.Bool) != 0) Observe(TypeKind.Bool, HpirWidth.U8);
            return payload;

            void Observe(TypeKind kind, HpirWidth width)
            {
                int match = Reserve(1, 1);
                CompareTag(site, value, kind, match);
                int branch = CursorBranch(match, site.Source);
                int import = geometry.DemandImport(width, HpirFutureInput.Either);
                EmitCall(site, ~import, payload, [UnionCompletion(value), payload]);
                LinkCursorBranch(branch, branch + 1, code.Count);
            }
        }

        private void EmitFutureUnionNull(LlvmCallInstruction test)
        {
            LlvmValue value = test.GetArgument(0);
            int isNull = Reserve(1, 1);
            if ((geometry.Union(value.Type) & TypeKind.Null) != 0)
                CompareTag(test, value, TypeKind.Null, isNull);
            else Add(new(HpirOp.ConstU8, isNull, Immediate: 0), test.Source);

            int output = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, output, Payload(test)), test.Source);
            int pending = UnionCell(value) ? UnionCompletion(value) : Location(value);
            int import = geometry.LiftImport(HpirFutureOperation.ScalarIsNull, HpirFutureInput.Either, HpirFutureInput.Ready);
            EmitImportCall(test, import, [output, pending, isNull]);
        }
    }
}
