using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal readonly List<HpirIteratorSelection> IteratorSelections = [];

        internal bool StaticLocalQualification(LlvmCallInstruction call) => call.ArgumentCount == 2
            && call.CalledFunction is { } target && PathOperation(call, target) == HpirPathOp.Qualify
            && call.GetArgument(0) is LlvmLoadInstruction { IsSimple: true, PointerOperand: LlvmGlobalVariable literal }
            && ConnectionLiteral(literal) is { ConnectionKind: ConnectionKind.Local } endpoint
            && string.IsNullOrEmpty(endpoint.Text);

        internal bool IteratorUse(LlvmUser user) => user is LlvmCallInstruction { CalledFunction: { } target }
            && IteratorOperation(target, out var operation) && operation == LlvmIteratorOperation.Open;
    }

    private sealed partial class Body
    {
        private LlvmCallInstruction? preparedSelector;
        private int preparedSelectorIndex;

        private void PrepareIteratorSelection(CompilationTrace? trace)
        {
            if (!geometry.IsEntry(function) || geometry.IsPersistentSession) return;
            foreach (LlvmInstruction instruction in function.BasicBlocks[0].Instructions)
            {
                if (instruction is LlvmCallInstruction call)
                {
                    if (geometry.IteratorUse(call))
                    {
                        PrepareFirstSelection(call, trace);
                        return;
                    }
                    if (geometry.PathSelection(call, out _) || geometry.StaticLocalQualification(call)) continue;
                    if (call.CalledFunction is { } target
                        && (geometry.PathOperation(call, target) == HpirPathOp.Qualify || geometry.TextOutput(target))) return;
                }
                // Only this guaranteed prefix is entitled to record membership.
                // An earlier effect or CFG edge keeps all following selectors live.
                if (instruction.IsTerminator || instruction is LlvmFileOutInst
                    || !geometry.IndependentOfPreparedTransfers(instruction)) return;
            }
        }

        private void PrepareFirstSelection(LlvmCallInstruction open, CompilationTrace? trace)
        {
            if (open.GetArgument(0) is not LlvmCallInstruction selector
                || !geometry.PathSelection(selector, out string? pattern) || !selector.Users.All(geometry.IteratorUse))
                return;
            LlvmValue directory = selector.GetArgument(0);
            bool local = directory is LlvmCallInstruction qualify && geometry.StaticLocalQualification(qualify);
            if (local) directory = ((LlvmCallInstruction)directory).GetArgument(1);
            if (!TryLocalPath(directory, [], out NativePath path))
            {
                trace?.Metric("range.iterator_selection.residual_directory", 1);
                return;
            }
            preparedSelector = selector;
            preparedSelectorIndex = geometry.IteratorSelections.Count;
            geometry.IteratorSelections.Add(new(Local(path), pattern!, local, open.Source ?? selector.Source));
            trace?.Metric("range.iterator_selection.recipes", 1);
        }
    }
}
