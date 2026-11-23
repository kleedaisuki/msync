using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<HpirStringOp, int> stringImports = [];

        internal bool TryStringOperation(LlvmCallInstruction call, out HpirStringOp operation)
        {
            operation = default;
            if (call.CalledFunction is not { } target) return false;
            if (graph.NativeCalls.TryGetValue(target, out string? name)) return HpirStringImports.TryNative(name, out operation);
            if (!graph.RuntimeCalls.TryGetValue(target, out NativeBinary binary)
                || call.ArgumentCount != 2 || !StringValue(call.GetArgument(0)) || !StringValue(call.GetArgument(1)))
                return false;
            switch (binary)
            {
                case NativeBinary.Equal: operation = HpirStringOp.Equal; return true;
                case NativeBinary.NotEqual: operation = HpirStringOp.NotEqual; return true;
                case NativeBinary.Less: operation = HpirStringOp.Less; return true;
                case NativeBinary.LessEqual: operation = HpirStringOp.LessEqual; return true;
                case NativeBinary.Greater: operation = HpirStringOp.Greater; return true;
                case NativeBinary.GreaterEqual: operation = HpirStringOp.GreaterEqual; return true;
                default: return false;
            }
        }

        internal (int Index, HpirImport Import) StringImport(HpirStringOp operation)
        {
            if (stringImports.TryGetValue(operation, out int index)) return (index, Imports[index]);
            if (HpirStringImports.ProducesText(operation)) _ = TextLayout();
            HpirImport import = HpirStringImports.Create(operation);
            index = Imports.Count;
            Imports.Add(import);
            stringImports.Add(operation, index);
            return (index, import);
        }
    }

    private sealed partial class Body
    {
        private bool TryReadyString(LlvmCallInstruction call)
        {
            if (!geometry.TryStringOperation(call, out HpirStringOp operation)) return false;
            if (operation == HpirStringOp.Concat && TryLiftedTextConcat(call)) return true;
            var (index, import) = geometry.StringImport(operation);
            bool returnsText = HpirStringImports.ProducesText(operation);
            bool validResult = returnsText ? geometry.IsGuestPointer(call.Type)
                : import.Result == HpirWidth.U8 ? call.Type.IsIntegerTy(1) : call.Type.IsDoubleTy();
            if (!validResult || call.ArgumentCount != import.Arguments.Length)
                throw Error("a String call outside the selected physical signature", call);
            var args = new int[call.ArgumentCount];
            for (int i = 0; i < call.ArgumentCount; i++)
            {
                var argument = call.GetArgument(i);
                bool admitted = HpirStringImports.NumericArgument(operation, i) ? argument.Type.IsDoubleTy()
                    : geometry.IsGuestPointer(argument.Type);
                bool observed = TextCell(argument) && geometry.ObservedTextArgument(call, i);
                bool reduce = TextCell(argument) && ReducesText(operation);
                if (!admitted || FutureRoot(argument) && !observed && !reduce)
                    throw Error("a String call without fixed ready operands", call);
                args[i] = observed ? CompletedText(call, argument) : reduce ? ObserveText(call, argument) : Location(argument);
            }
            EmitImportCall(call, index, args, safepoint: returnsText);
            return true;
        }

        private bool TextReduction(LlvmCallInstruction call, LlvmValue input) =>
            call.ArgumentCount == 1 && call.GetArgument(0) == input
            && geometry.TryStringOperation(call, out var operation) && ReducesText(operation);

        private static bool ReducesText(HpirStringOp operation) => operation is HpirStringOp.Length
            or HpirStringOp.Utf8Size or HpirStringOp.LineFeedCount or HpirStringOp.WordCount;

        private bool ObservesTextArgument(LlvmCallInstruction call, LlvmValue value)
        {
            bool found = false;
            for (int index = 0; index < call.ArgumentCount; ++index)
            {
                if (call.GetArgument(index) != value) continue;
                if (!geometry.ObservedTextArgument(call, index)) return false;
                found = true;
            }
            return found;
        }

        private int CompletedText(LlvmInstruction site, LlvmValue value)
        {
            int ready = Reserve(8, 8);
            Add(new(HpirOp.CopyU64, ready, ~Location(value)), site.Source);
            return ready;
        }
    }
}
