using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int fileRenameImport = -1;

        internal bool HasFileRename => fileRenameImport >= 0;

        internal bool IsFileRename(LlvmFunction target) =>
            graph.NativeCalls.TryGetValue(target, out string? name) && name == "fs.rename";

        internal int FileRenameImport() => fileRenameImport >= 0 ? fileRenameImport
            : fileRenameImport = FutureImport(HpirFileRenameImports.Create());
    }

    private sealed partial class Body
    {
        private int fileRenameSites;

        private bool TryFileRename(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !geometry.IsFileRename(target)) return false;
            if (call.ArgumentCount != 2 || Width(call.Type) != HpirWidth.Void
                || !Geometry.IsPathRecord(call.GetArgument(0).Type) || !Geometry.IsPathRecord(call.GetArgument(1).Type))
                throw Error("fs.rename requires two physical Path operands and no result storage", call);

            EmitImportCall(call, geometry.FileRenameImport(),
                [PathRecordAddress(call.GetArgument(0), call), PathRecordAddress(call.GetArgument(1), call)]);
            ++fileRenameSites;
            return true;
        }

        private void ReportFileRenames(CompilationTrace? trace) => trace?.Metric("paths.rename_sites", fileRenameSites);
    }
}
