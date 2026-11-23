using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private HashSet<LlvmCallInstruction>? liftedTextConcats;
        internal bool LiftedTextConcat(LlvmCallInstruction call) => liftedTextConcats?.Contains(call) == true;
        internal int TextConcatImport()
        {
            _ = TextLayout();
            return FutureImport(HpirTextInputImports.Concat(FutureLayout(-(int)HpirService.ConcatTextPending,
                HpirTextInputImports.ConcatLayout())));
        }
    }

    private sealed partial class Body
    {
        private int textConcatCompletions;

        private bool TryLiftedTextConcat(LlvmCallInstruction call)
        {
            if (!geometry.LiftedTextConcat(call)) return false;
            int mask = 0, flags = Reserve(8, 8);
            var arguments = new int[3];
            for (int index = 0; index < 2; ++index)
            {
                var input = call.GetArgument(index);
                if (geometry.StoredText(input)) throw Error("a deferred concat across mixed session Text storage", call);
                bool observed = TextCell(input) && geometry.ObservedTextArgument(call, index);
                if (!observed && TextCell(input) && !geometry.MixedTextResult(input)) mask |= 1 << index;
                arguments[index] = observed ? CompletedText(call, input) : Location(input);
            }
            Add(new(HpirOp.ConstU64, flags, Immediate: mask), call.Source);
            for (int index = 0; index < 2; ++index)
            {
                var input = call.GetArgument(index);
                if (!geometry.MixedTextResult(input) || geometry.ObservedTextArgument(call, index)) continue;
                int tag = FormatBool(call, TextTag(input));
                if (index == 1) Add(new(HpirOp.AddU64, tag, tag, tag), call.Source);
                Add(new(HpirOp.OrU64, flags, flags, tag), call.Source);
            }
            arguments[2] = flags;
            EmitImportCall(call, geometry.TextConcatImport(), arguments);
            ++textConcatCompletions;
            return true;
        }
    }
}
