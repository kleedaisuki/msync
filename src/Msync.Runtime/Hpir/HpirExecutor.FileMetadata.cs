using System.Runtime.CompilerServices;
using Msync.Core.Hpir;
using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallFileMetadata(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, ExecutionOptions? options,
        CancellationToken token, HpirPathAnchors anchors)
    {
        var (operation, text, layout) = HpirFileMetadataImports.Decode(import.Binding);
        nint ready = Read<nint>(frame, call.Arguments[0]);
        var path = PathOperand(frame, call.Arguments[1], text, anchors).ToHost(contextual: true);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        work ??= new(token);
        nint cell = 0;
        int id;
        if (operation == HpirFileMetadata.Size)
        {
            var value = work.SubmitFileMetadata<double>(operation, path, options, anchors.Binding(program.Context), site,
                static (_, path, context, token) => NativeBuiltins.ReadFileSize(path, context, token));
            id = value.JobId;
            Unsafe.WriteUnaligned((void*)ready, value.Value);
            if (!value.Ready)
            {
                cell = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
                HpirFuture.PublishNumber(cell, value);
            }
        }
        else
        {
            var value = work.SubmitFileMetadata<byte>(operation, path, options, anchors.Binding(program.Context), site, NativeBuiltins.ReadFilePredicate);
            id = value.JobId;
            Unsafe.WriteUnaligned((void*)ready, value.Value);
            if (!value.Ready)
            {
                cell = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
                HpirFuture.PublishBool(cell, value);
            }
        }
        Write(frame, destination, cell);
        if (id != 0 && active->Parent != null) work.TrackGuestJob(id, (nint)active, cell, heap!);
    }
}
