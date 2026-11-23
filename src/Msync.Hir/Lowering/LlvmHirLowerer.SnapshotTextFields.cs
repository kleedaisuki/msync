using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private static bool SnapshotPolicyConsumer(string? name) =>
            name is "ranges.concat" or "ranges.zip" or "string.join" or "text.append_chunk"
                or "process.spawn" or "ranges.transform" or "ranges.filter" or "ranges.take" or "ranges.drop"
                or "ranges.enumerate_from";

        private sealed partial class SnapshotFacts
        {
            private Fact NormalizeAppendedText(Fact incoming) =>
                !incoming.NeedsMaterialize && (incoming.Carrier.Empty || incoming.Carrier.Element == owner.DataPointer)
                    ? new(Kind.Known, new(owner.DataPointer, false)) : new(Kind.Conflict);

            private bool SnapshotAppendOrConcat(LlvmCallInstruction call, Node result, string? name)
            {
                if (name == "text.append_chunk" && call.ArgumentCount == 2)
                {
                    result.AddsText = true;
                    Link(Value(call.GetArgument(0)), result);
                    return true;
                }
                if (name == "ranges.concat" && call.ArgumentCount == 2)
                {
                    // Carry element geometry, not an assertion that the plan
                    // already has the materialized snapshot header.
                    result.ProducesPlan = true;
                    Link(Value(call.GetArgument(0)), result);
                    Link(Value(call.GetArgument(1)), result);
                    return true;
                }
                return false;
            }
        }
    }
}
