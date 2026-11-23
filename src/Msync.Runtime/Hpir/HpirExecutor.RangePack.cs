using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void PackRangeWords(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> args, byte* frame, int destination)
    {
        ulong length = 0;
        for (int at = 1; at < args.Count; at += 2) length += Read<ulong>(frame, args[at]);
        int stride = program.Layouts[import.Binding].Size;
        using RuntimeTimelineScope? trace = diagnostics.BeginRangePack(active->Function, pc, args.Count / 2, length, stride);
        try
        {
            if (length > int.MaxValue)
                throw new MsyncException("ranges.materialize exceeded the addressable Range extent", diagnostics.Source(active->Function, pc));
            if (length == 0) { Write(frame, destination, (nint)0); return; }

            nint result = Allocate(program, ref heap, active, pc, import.Binding, (int)length, diagnostics, counts, clear: false);
            byte* target = (byte*)result;
            for (int at = 0; at < args.Count; at += 2)
            {
                ulong bytes = Read<ulong>(frame, args[at + 1]) * (ulong)stride;
                if (bytes == 0) continue;
                Buffer.MemoryCopy((void*)Read<nint>(frame, args[at]), target, bytes, bytes);
                target += bytes;
            }
            counts?.RangePacked(length, stride);
            Write(frame, destination, result);
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }
}
