using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static ulong OrderTextSnapshot(HpirStringOp operation, nint input, nint output, int count, CancellationToken token)
    {
        var source = new ReadOnlySpan<nint>((void*)input, count);
        var destination = new Span<nint>((void*)output, count);
        token.ThrowIfCancellationRequested();
        if (operation == HpirStringOp.SortSnapshot)
        {
            source.CopyTo(destination);
            destination.Sort(SnapshotTextComparer.Instance);
            token.ThrowIfCancellationRequested();
            return (ulong)count;
        }

        int length = 0;
        nint previous = 0;
        foreach (nint item in source)
        {
            token.ThrowIfCancellationRequested();
            if (previous == 0 || !HpirTextStorage.Read(previous).SequenceEqual(HpirTextStorage.Read(item)))
                destination[length++] = item;
            previous = item;
        }
        return (ulong)length;
    }

    private sealed class SnapshotTextComparer : IComparer<nint>
    {
        internal static readonly SnapshotTextComparer Instance = new();

        public int Compare(nint left, nint right) =>
            TextSemantics.CompareOrdinal(HpirTextStorage.Read(left), HpirTextStorage.Read(right));
    }
}
