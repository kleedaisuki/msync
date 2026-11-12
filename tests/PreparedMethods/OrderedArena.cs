using System.Buffers.Binary;

internal sealed class OrderedArena(double[] keys, int seed)
{
    private readonly Row[] rows = new Row[keys.Length + 1];
    private readonly int[] positions = Shuffle(keys.Length, seed);

    internal int Root { get; private set; }

    internal byte[] Build(int stride, int lower, int higher, int value)
    {
        Root = Make(0, keys.Length);
        var bytes = new byte[rows.Length * stride];
        for (int i = 1; i < rows.Length; i++)
        {
            var row = rows[i];
            var record = bytes.AsSpan(i * stride, stride);
            BinaryPrimitives.WriteInt32LittleEndian(record[lower..], row.Lower);
            BinaryPrimitives.WriteInt32LittleEndian(record[higher..], row.Higher);
            BinaryPrimitives.WriteInt64LittleEndian(record[value..], BitConverter.DoubleToInt64Bits(row.Key));
        }
        return bytes;
    }

    internal int Expected(double key)
    {
        for (int i = 1; i < rows.Length; i++)
            if (rows[i].Key == key || double.IsNaN(rows[i].Key) && double.IsNaN(key)) return i;
        return 0;
    }

    private int Make(int start, int end)
    {
        if (start == end) return 0;
        int middle = start + (end - start) / 2;
        int slot = positions[middle];
        rows[slot] = new(keys[middle], Make(start, middle), Make(middle + 1, end));
        return slot;
    }

    private static int[] Shuffle(int length, int seed)
    {
        var values = Enumerable.Range(1, length).ToArray();
        new Random(seed).Shuffle(values);
        return values;
    }

    private readonly record struct Row(double Key, int Lower, int Higher);
}
