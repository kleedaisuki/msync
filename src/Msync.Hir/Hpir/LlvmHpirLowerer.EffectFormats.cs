using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal int TransferCompletionLayout(bool observed) => observed
            ? FutureLayout(-(int)HpirService.LiveTransfer, HpirFutureImports.Completion(HpirWidth.U8)) : -1;

        internal int LiveBoolTextImport(bool observed) =>
            FutureImport(HpirLiveTransferImports.CreateBoolText(TransferCompletionLayout(observed)));

        internal int LiveNumberTextImport(bool observed) =>
            FutureImport(HpirLiveTransferImports.CreateNumberText(TransferCompletionLayout(observed)));
    }

    private sealed partial class Body
    {
        private int boolFileFormats, numberFileFormats;

        private bool RecordFileBoolFormat(LlvmValue value, LlvmCallInstruction format)
        {
            if (value is not LlvmFileOutInst { Mode: LlvmFileOutMode.Transfer }
                || !value.Type.IsIntegerTy(1) || format.ArgumentCount != 1
                || geometry.ExplicitCastTarget(format) != TypeKind.String
                || !format.HasOneUse || format.Users.First() is not LlvmFileOutInst write || write.Value != format) return false;
            bool supported = write.Mode == LlvmFileOutMode.Transfer
                ? Geometry.IsPathRecord(write.Destination.Type) : geometry.CanWriteFileText(write);
            if (!supported) return false;
            // The sole observer is actual file admission. Keep the concrete
            // receipt live until then, rather than manufacturing a Text Future.
            (deferredFormats ??= []).TryAdd(format, value);
            return true;
        }

        private bool RecordFileNumberFormat(LlvmValue value, LlvmCallInstruction format)
        {
            if (format.ArgumentCount != 1 || !geometry.NumberFormat(format) || !format.HasOneUse
                || format.Users.First() is not LlvmFileOutInst { Mode: LlvmFileOutMode.Transfer } transfer
                || transfer.Value != format || !Geometry.IsPathRecord(transfer.Destination.Type)) return false;
            (deferredFormats ??= []).TryAdd(format, value);
            return true;
        }

        private bool TryLiveScalarTextTransfer(LlvmFileOutInst transfer)
        {
            if (transfer.Value is not LlvmCallInstruction format || deferredFormats is null
                || !deferredFormats.TryGetValue(format, out var source)) return false;
            bool observed = !transfer.UseEmpty;
            int path = PathRecordAddress(transfer.Destination, transfer);
            int receipt = observed ? Location(transfer) : -1;
            if (source.Type.IsDoubleTy())
            {
                bool mixed = FutureInput(source) == HpirFutureInput.Either;
                int bits = mixed ? Payload(source) : Reserve(8, 8);
                if (!mixed)
                    Add(new(HpirOp.ConstU64, bits), transfer.Source);
                EmitCall(transfer, ~geometry.LiveNumberTextImport(observed), receipt, [Location(source), bits, path]);
                ++numberFileFormats;
            }
            else
            {
                EmitCall(transfer, ~geometry.LiveBoolTextImport(observed), receipt, [Location(source), path], safepoint: observed);
                ++boolFileFormats;
            }
            ++livePathTransfers;
            return true;
        }
    }
}
