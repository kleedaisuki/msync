using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

internal readonly record struct LlvmSessionRangeRead(LlvmSnapshotCarrier Carrier, LlvmFunction? Callback);

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal Dictionary<LlvmFunction, LlvmSessionRangeRead>? sessionSnapshotReads;
    }

    private sealed partial class Body
    {
        private LlvmValue SnapshotRead(HirNode node, HpirSessionSnapshot snapshot, LlvmValue range)
        {
            int mode = ((int)snapshot.Item << 1) | (snapshot.Empty ? 1 : 0);
            var marker = OperationCall(new(LlvmRuntimeValueOperation.SessionSnapshot, mode,
                    snapshot.Callback >= 0 ? snapshot.Callback.ToString(System.Globalization.CultureInfo.InvariantCulture) : null), range.Type,
                owner.metadata.Type(node.Type), [range]);
            (owner.sessionSnapshotReads ??= []).TryAdd(marker.CalledFunction!,
                new(new(owner.RepresentationType(snapshot.Item), snapshot.Empty),
                    snapshot.Callback < 0 ? null : owner.Reserve(new(snapshot.Callback))));
            return marker;
        }
    }
}
