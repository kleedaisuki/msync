using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private void FunctionAggregate(LlvmInsertValueInstruction aggregate)
        {
            if (!TryAggregateInsert(aggregate)) throw Unsupported(aggregate, "source builtin aggregate assembly");
        }

        private NativeInstruction ConstantFunction(LlvmConstant value, int slot)
        {
            ++functionCompositions;
            var leaves = AggregateLeaves(value);
            RecordFunctionContext(leaves, inPrelude: true);
            return new(NativeOp.ComposeFunction, slot, Inputs: leaves.Slots, Source: instructionSource);
        }

        private void ExtractFunction(LlvmExtractValueInstruction extract)
        {
            if (!TryAggregateExtract(extract)) throw Unsupported(extract, "source builtin aggregate extraction");
        }
    }
}
