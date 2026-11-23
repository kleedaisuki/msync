using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TextChunkView(ulong Length, nint Data, nint Owner);

    [StructLayout(LayoutKind.Sequential)]
    private struct TextChunkBuffer
    {
        internal int Published;
        internal int Capacity;
        internal nint Data;
    }

    private static void CallAppendTextChunk(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        HpirCall call, byte* frame, int destination, ExecutionOptions? options)
    {
        nint source = Read<nint>(frame, call.Arguments[0]);
        nint text = Read<nint>(frame, call.Arguments[1]);
        int viewLayout = import.Binding;
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        using RuntimeTimelineScope? trace = site.Begin("hpir.text.append_chunk");
        try
        {
            ulong size = *(ulong*)source;
            if (size >= int.MaxValue) throw new MsyncException("text.append_chunk exceeded the addressable Range extent", site.Source);
            int length = (int)size;
            nint output = Allocate(program, ref heap, active, pc, viewLayout, 1, diagnostics, counts, clear: false);
            var buffer = HpirHeap.HasLayout(source, viewLayout) ? (TextChunkBuffer*)((TextChunkView*)source)->Owner : null;
            // Earlier views may coexist. Only the published tail owns the unwritten suffix.
            bool reusable = buffer is not null && length == buffer->Published && length < buffer->Capacity;
            if (!reusable)
            {
                string? reason = trace is null ? null : buffer is null ? "initial" : length == buffer->Published ? "grown" : "forked";
                buffer = AllocateTextChunkBuffer(program, heap!, active, diagnostics, counts, pc, viewLayout, source, length);
                trace?.SetAttribute("chunks.storage", reason!);
            }
            else trace?.SetAttribute("chunks.storage", "extended");

            ((nint*)buffer->Data)[length] = text;
            buffer->Published = length + 1;
            *(TextChunkView*)output = new((ulong)buffer->Published, buffer->Data, (nint)buffer);
            Write(frame, destination, output);
            if (trace is not null)
            {
                trace.SetMetric("chunks.length", buffer->Published);
                trace.SetMetric("chunks.capacity", buffer->Capacity);
                trace.SetMetric("chunks.copied", reusable ? 0 : length);
            }
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private static TextChunkBuffer* AllocateTextChunkBuffer(HpirProgram program, HpirHeap heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, int viewLayout, nint source, int length)
    {
        int capacity = length <= int.MaxValue / 2 ? Math.Max(4, length * 2) : int.MaxValue;
        nint data = AllocatePayload(program, heap, active, pc, viewLayout + 2, capacity, diagnostics, counts, clear: true);
        if (length != 0)
        {
            nint previous = *(nint*)(source + HpirTransformImports.VectorData);
            Buffer.MemoryCopy((void*)previous, (void*)data, (long)capacity * sizeof(ulong), (long)length * sizeof(ulong));
        }
        var buffer = (TextChunkBuffer*)AllocatePayload(program, heap, active, pc, viewLayout + 1, 1, diagnostics, counts, clear: false);
        *buffer = new() { Capacity = capacity, Data = data };
        return buffer;
    }
}
