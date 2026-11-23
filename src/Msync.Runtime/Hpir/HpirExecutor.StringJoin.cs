namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static nint JoinTextSnapshot(nint data, int count, nint separator, ref TextAllocation allocation, CancellationToken token)
    {
        var items = new ReadOnlySpan<nint>((void*)data, count);
        if (count == 1)
        {
            allocation.Join(1, HpirTextStorage.Read(items[0]).Length);
            return allocation.Reuse(items[0]);
        }
        var delimiter = HpirTextStorage.Read(separator);
        int length = checked(Math.Max(0, count - 1) * delimiter.Length);
        for (int index = 0; index < items.Length; ++index)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            length = checked(length + HpirTextStorage.Read(items[index]).Length);
        }
        allocation.Join(count, length);
        if (length == 0 && delimiter.IsEmpty) return allocation.Reuse(separator);
        nint result = allocation.Reserve(length, out Span<char> output);
        int offset = 0;
        for (int index = 0; index < items.Length; ++index)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            if (index != 0)
            {
                delimiter.CopyTo(output[offset..]);
                offset += delimiter.Length;
            }
            var item = HpirTextStorage.Read(items[index]);
            item.CopyTo(output[offset..]);
            offset += item.Length;
        }
        return result;
    }
}
