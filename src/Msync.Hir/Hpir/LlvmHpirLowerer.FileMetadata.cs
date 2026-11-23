using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly bool hasFileMetadata = graph.NativeCalls.Values.Any(static name =>
            HpirFileMetadataImports.Operation(name) != HpirFileMetadata.Invalid);
        internal bool HasFileMetadata => hasFileMetadata;

        internal HpirFileMetadata FileMetadata(LlvmFunction target) => graph.NativeCalls.TryGetValue(target, out string? name)
            ? HpirFileMetadataImports.Operation(name) : HpirFileMetadata.Invalid;

        internal int FileMetadataImport(HpirFileMetadata operation, bool text)
        {
            var payload = HpirFileMetadataImports.Payload(operation);
            int layout = FutureLayout(-((int)HpirService.FileMetadata << 4) - (int)payload, HpirFutureImports.Completion(payload));
            return FutureImport(HpirFileMetadataImports.Create(operation, text, layout));
        }
    }

    private sealed partial class Body
    {
        private int fileMetadataCalls;

        private bool TryFileMetadata(LlvmCallInstruction call, LlvmFunction target)
        {
            var operation = geometry.FileMetadata(target);
            if (operation == HpirFileMetadata.Invalid) return false;
            if (call.ArgumentCount != 1 || (HpirFileMetadataImports.Payload(operation) == HpirWidth.U64
                ? !call.Type.IsDoubleTy() : !call.Type.IsIntegerTy(1))) throw Error("invalid typed file metadata signature", call);
            var path = call.GetArgument(0);
            bool text = PathTextOperand(path, call);
            int address = text ? Location(path) : PathRecordAddress(path, call);
            int ready = Reserve(8, 8);
            Add(new(HpirOp.AddressOf, ready, Payload(call)), call.Source);
            EmitImportCall(call, geometry.FileMetadataImport(operation, text), [ready, address]);
            ++fileMetadataCalls;
            return true;
        }
    }
}
