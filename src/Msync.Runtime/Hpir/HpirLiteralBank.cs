using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal readonly unsafe struct HpirLiteralBank(nint* addresses, HpirPathAnchors anchors) : IDisposable
{
    private const nuint Alignment = HpirConnectionAbi.Alignment;

    internal nint* Addresses => addresses;
    internal HpirPathAnchors Anchors => anchors;

    internal static HpirLiteralBank Link(HpirProgram program, ExecutionOptions? options)
    {
        BindingContext? context = !program.PathAnchors ? null : program.Context
            ?? new(Path.GetFullPath(options?.WorkingDirectory ?? Environment.CurrentDirectory), HostContext.HomeDirectory, options?.SourceFile);
        var literals = program.TextLiterals;
        int anchors = context is null ? 0 : 2;
        var typeCells = program.TypeCells;
        int types = typeCells.Count;
        int count = checked(literals.Count + types + anchors);
        if (count == 0) return default;
        using RuntimeTimelineScope? trace = options?.Timeline?.Begin("hpir.link.text_literals");
        nuint tableBytes = checked((nuint)count * (nuint)sizeof(nint));
        nuint bytes = tableBytes;
        long characters = 0;
        nint* addresses = null;
        try
        {
            foreach (string text in literals)
            {
                characters += text.Length;
                bytes = checked(bytes + Extent(text.Length));
            }
            if (context is not null)
            {
                characters += (long)context.WorkingDirectory.Length + context.HomeDirectory.Length;
                bytes = checked(bytes + Extent(context.WorkingDirectory.Length) + Extent(context.HomeDirectory.Length));
            }
            bytes = checked(bytes + (nuint)types * sizeof(ulong));
            addresses = (nint*)NativeMemory.Alloc(bytes);
            byte* payload = (byte*)addresses + tableBytes;
            for (int index = 0; index < literals.Count; index++)
                addresses[index] = Place(ref payload, literals[index]);
            WriteTypeCells(typeCells, addresses + literals.Count, payload);
            payload += checked((nint)types * sizeof(ulong));

            HpirPathAnchors path = context is null ? default : new(
                Place(ref payload, context.WorkingDirectory), Place(ref payload, context.HomeDirectory));
            if (context is not null)
            {
                addresses[literals.Count + types] = path.Directory;
                addresses[literals.Count + types + 1] = path.Home;
            }
            trace?.SetMetric("literals.entries", literals.Count);
            trace?.SetMetric("literals.types", types);
            trace?.SetMetric("literals.path_anchors", anchors);
            trace?.SetMetric("literals.utf16_code_units", characters);
            trace?.SetMetric("literals.reserved_bytes", checked((long)bytes));
            return new(addresses, path);
        }
        catch (Exception error)
        {
            NativeMemory.Free(addresses);
            trace?.Fail(error);
            throw;
        }
    }

    private static nint Place(ref byte* payload, string value)
    {
        nint address = (nint)payload;
        HpirTextStorage.Write(address, value.AsSpan());
        payload += Extent(value.Length);
        return address;
    }

    internal static void WriteTypeCells(IReadOnlyList<int> cells, nint* addresses, byte* payload)
    {
        for (int i = 0; i < cells.Count; ++i, payload += sizeof(ulong))
        {
            addresses[i] = (nint)payload;
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(payload, 1UL + (ulong)cells[i]);
        }
    }

    private static nuint Extent(int chars) => checked((nuint)chars * (nuint)HpirScalarImports.TextElementBytes
        + HpirScalarImports.TextHeaderBytes + Alignment - 1) & ~(Alignment - 1);

    public void Dispose() => NativeMemory.Free(addresses);
}
