using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private bool hasDirectoryCreation;
        internal bool HasDirectoryCreation => hasDirectoryCreation;

        internal bool IsDirectoryCreation(LlvmFunction target) => graph.NativeCalls.TryGetValue(target, out string? name)
            && name == HpirDirectoryCreationImports.Name;

        internal int DirectoryCreationImport(bool text)
        {
            hasDirectoryCreation = true;
            return FutureImport(HpirDirectoryCreationImports.Create(text));
        }
    }

    private sealed partial class Body
    {
        private int directoryCreationSites, directoryCreationText, directoryCreationUnions;

        private bool TryDirectoryCreation(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !geometry.IsDirectoryCreation(target)) return false;
            if (call.ArgumentCount != 1 || !call.Type.IsIntegerTy(1))
                throw Error("fs.create_directories requires one Path or String operand and a Bool result", call);
            var path = call.GetArgument(0);
            bool union = KnownPathUnion(path);
            bool text = !union && PathTextOperand(path, call);
            int argument = PathCallAddress(call, path, text, union);
            EmitImportCall(call, geometry.DirectoryCreationImport(text), [argument]);
            ++directoryCreationSites;
            if (text) ++directoryCreationText;
            if (union) ++directoryCreationUnions;
            return true;
        }

        private void ReportDirectoryCreation(CompilationTrace? trace)
        {
            if (trace is null || directoryCreationSites == 0) return;
            trace.Metric("paths.create_directories_sites", directoryCreationSites);
            trace.Metric("paths.create_directories_text", directoryCreationText);
            trace.Metric("paths.create_directories_unions", directoryCreationUnions);
        }
    }
}
