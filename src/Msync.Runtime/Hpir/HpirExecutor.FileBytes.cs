using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallFileBytes(HpirProgram program, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, int pc, HpirImport import, HpirCall call, byte* frame, int destination,
        ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors)
    {
        var args = call.Arguments;
        if (import.Service == HpirService.FileBytesOpen)
        {
            bool append = (import.Binding & 1) != 0, textPath = (import.Binding & 2) != 0;
            var path = PathOperand(frame, args[0], textPath, anchors).ToHost(contextual: true);
            var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
            var (id, cap) = (work ??= new(token)).OpenFileBytes(path, append, options, anchors.Binding(program.Context), site, token);
            *(ulong*)Read<nint>(frame, args[1]) = (ulong)cap;
            Write(frame, destination, id);
            return;
        }
        long owner = Read<long>(frame, args[0]);
        var bytes = new ReadOnlySpan<byte>((void*)Read<nint>(frame, args[1]), checked((int)Read<ulong>(frame, args[2])));
        bool final = import.Binding != 0;
        int capacity = work!.FileByteChunk(owner, bytes, final, token);
        if (!final) Write(frame, destination, (ulong)capacity);
    }
}
