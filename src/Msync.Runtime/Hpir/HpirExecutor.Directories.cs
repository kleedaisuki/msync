using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallDirectory(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, ExecutionOptions? options, HpirPathAnchors anchors,
        CancellationToken token)
    {
        if (import.Service == HpirService.DirectorySource)
        {
            var (layout, text, recursive) = HpirDirectoryImports.DecodeSource(import.Binding);
            nint plan = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
            Unsafe.WriteUnaligned((void*)plan, PathOperand(frame, call.Arguments[0], text, anchors));
            *((byte*)plan + HpirDirectoryImports.RecursiveOffset) = recursive ? (byte)1 : (byte)0;
            Write(frame, destination, plan);
            return;
        }
        var timeline = options?.Timeline;
        SourceSpan? span = import.Service != HpirService.DirectoryNext || timeline is not null ? diagnostics.Source(active->Function, pc) : null;
        var site = new HpirWorkSite(timeline, span, active->Function, pc);
        if (import.Service == HpirService.DirectoryOpen)
        {
            work ??= new(token);
            byte* plan = (byte*)Read<nint>(frame, call.Arguments[0]);
            var path = Unsafe.ReadUnaligned<HpirPathValue>(plan).ToHost(contextual: true);
            bool selection = import.Binding != 0;
            string? pattern = selection ? program.TextLiterals[Unsafe.ReadUnaligned<int>(plan + HpirPathSelectionImports.PatternOffset)] : null;
            int recipe = selection ? Unsafe.ReadUnaligned<int>(plan + HpirPathSelectionImports.RecipeOffset) : -1;
            var members = recipe < 0 ? null : program.IteratorSelections[recipe].Members;
            bool recursive = !selection && plan[HpirDirectoryImports.RecursiveOffset] != 0;
            Write(frame, destination, work.OpenDirectory(path, recursive,
                new NativeRuntimeContext(options, anchors.Binding(program.Context)), site, pattern, members));
            return;
        }
        long cursor = Read<long>(frame, call.Arguments[0]);
        if (import.Service == HpirService.DirectoryClose)
        {
            work!.CloseDirectory(cursor, site);
            return;
        }
        bool available = PullDirectoryEntry(program, ref heap, work!, active, pc, diagnostics, counts, cursor,
            call.Arguments, frame, site, token);
        WriteBool(frame, destination, available);
    }

    private static bool PullDirectoryEntry(HpirProgram program, ref HpirHeap? heap, HpirWork work, HpirStack.Frame* active,
        int pc, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, long cursor,
        IReadOnlyList<int> operands, byte* frame, HpirWorkSite site, CancellationToken token)
    {
        try
        {
            if (work.PullDirectory(cursor, site, token) is not { } entry) return false;
            var path = Unsafe.ReadUnaligned<HpirPathValue>((void*)Read<nint>(frame, operands[1]));
            StorePathProjection(program, ref heap, active, pc, diagnostics, counts, ref path, entry.Raw, entry.LocalSpelling);
            Unsafe.WriteUnaligned((void*)Read<nint>(frame, operands[2]), path);
            return true;
        }
        catch (MsyncException error) when (error.Span is null)
        {
            throw new MsyncException(error.Message, diagnostics.Source(active->Function, pc), error.InnerException ?? error);
        }
    }
}
