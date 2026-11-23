using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private void PrepareContextChecks(CompilationTrace? trace)
        {
            if (!geometry.IsEntry(function) || geometry.IsPersistentSession || !geometry.HasFileMetadata) return;
            var scratch = new HashSet<LlvmInstruction>();
            foreach (var instruction in function.BasicBlocks[0].Instructions)
            {
                if (TryPrepareContextCheck(instruction, scratch)) continue;
                if (instruction is LlvmFileInInst or LlvmFileOutInst || !geometry.IndependentOfPreparedTransfers(instruction))
                    break;
            }
            if (contextCheckImports is null) return;
            int shared = RetainOrdinaryUsers(contextCheckOperands!,
                user => user is LlvmCallInstruction call && HasContextCheck(call));
            trace?.Metric("effects.context_checks", contextCheckImports.Count);
            trace?.Metric("effects.context_check_sliced_operands", contextCheckOperands!.Count);
            trace?.Metric("effects.context_check_shared_operands", shared);
        }

        private bool TryPrepareContextCheck(LlvmInstruction instruction, HashSet<LlvmInstruction> scratch)
        {
            if (instruction is not LlvmCallInstruction { CalledFunction: { } target, ArgumentCount: 1 } call) return false;
            var operation = geometry.FileMetadata(target);
            if (operation is < HpirFileMetadata.Exists or > HpirFileMetadata.IsReadonly
                || call.UseEmpty || !call.Users.All(ContextControl)) return false;
            scratch.Clear();
            if (!TryLocalPath(call.GetArgument(0), scratch, out var path)) return false;
            int import = geometry.ContextCheckImport(new(operation, Local(path), call.Source));
            (contextCheckImports ??= []).Add(call, import);
            (contextCheckOperands ??= []).UnionWith(scratch);
            return true;
        }

        private bool ContextControl(LlvmUser user) => user switch
        {
            LlvmCondBrInstruction or LlvmSelectInstruction => true,
            LlvmCallInstruction { CalledFunction: { } target } => geometry.ScalarDemand(target),
            _ => false
        };

        private bool HasContextCheck(LlvmCallInstruction call) => contextCheckImports?.ContainsKey(call) == true;

        private bool TryContextCheck(LlvmCallInstruction call)
        {
            if (contextCheckImports?.TryGetValue(call, out int import) != true) return false;
            EmitCall(call, ~import, Location(call), [], safepoint: false);
            return true;
        }
    }
}
