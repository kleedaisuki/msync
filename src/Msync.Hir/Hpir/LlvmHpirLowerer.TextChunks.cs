using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int chunkAppendImport = -1;

        internal bool TextChunkAppend(LlvmCallInstruction call) =>
            call.ArgumentCount == 2 && NativeRangeCall(call, "text.append_chunk")
            && StringValue(call.GetArgument(1))
            && (SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0))) is { } chunks
            && (chunks.Empty || IsTextSnapshot(call, 0, chunks));

        internal int TextChunkAppendImport()
        {
            if (chunkAppendImport >= 0) return chunkAppendImport;
            int view = AddLayout(HpirTextChunkImports.ViewLayout());
            AddLayout(HpirTextChunkImports.BufferLayout());
            AddLayout(HpirTextChunkImports.ItemLayout());
            chunkAppendImport = Imports.Count;
            Imports.Add(HpirTextChunkImports.Append(view));
            return chunkAppendImport;
        }
    }

    private sealed partial class Body
    {
        private int textChunkAppends;

        private bool TryAppendTextChunk(LlvmCallInstruction call)
        {
            if (!geometry.TextChunkAppend(call) || FutureRoot(call.GetArgument(1))) return false;
            EmitImportCall(call, geometry.TextChunkAppendImport(), [Location(call.GetArgument(0)), Location(call.GetArgument(1))]);
            allocatedTransforms.Add(call);
            ++textChunkAppends;
            return true;
        }
    }
}
