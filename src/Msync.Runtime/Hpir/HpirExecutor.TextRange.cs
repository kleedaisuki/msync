using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallTextRange(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> arguments, byte* frame, int destination, ExecutionOptions? options, CancellationToken token,
        HpirPathAnchors anchors)
    {
        if (import.Service == HpirService.TextRangeSource)
        {
            var (source, layout) = HpirTextRangeImports.DecodeSource(import.Binding);
            nint plan = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts,
                clear: source == HpirTextRangeSource.Stdin);
            if (source != HpirTextRangeSource.Stdin)
            {
                HpirPathValue path = PathOperand(frame, arguments[0], source == HpirTextRangeSource.FileText, anchors);
                Unsafe.WriteUnaligned((void*)plan, path);
            }
            Write(frame, destination, plan);
            return;
        }

        RuntimeTimeline? timeline = options?.Timeline;
        SourceSpan? sourceSite = import.Service != HpirService.TextRangeNext || timeline is not null
            ? diagnostics.Source(active->Function, pc) : null;
        var site = new HpirWorkSite(timeline, sourceSite, active->Function, pc);
        if (import.Service == HpirService.TextRangeOpen)
        {
            work ??= new(token);
            Write(frame, destination, OpenTextCursor(work, Read<nint>(frame, arguments[0]), import.Binding != 0, program, options, site));
            return;
        }
        if (import.Service == HpirService.TextRangeJoin)
        {
            work ??= new(token);
            JoinLiveText(program, ref heap, work, active, diagnostics, counts, pc, import.Binding != 0,
                Read<nint>(frame, arguments[0]), Read<nint>(frame, arguments[1]), frame, destination, options, token, site);
            return;
        }
        long id = Read<long>(frame, arguments[0]);
        if (import.Service == HpirService.TextRangeClose)
        {
            work!.CloseTextRange(id, site);
            return;
        }

        string? line;
        try { line = work!.PullTextRange(id, token, site); }
        catch (MsyncException failure) when (failure.Span is null)
        {
            throw new MsyncException(failure.Message, diagnostics.Source(active->Function, pc), failure.InnerException ?? failure);
        }
        counts?.TextRangePull(line);
        if (line is not null)
        {
            nint text = Allocate(program, ref heap, active, pc, program.TextLayout, HpirTextStorage.Elements(line.Length),
                diagnostics, counts, clear: false);
            HpirTextStorage.Write(text, line.AsSpan());
            Unsafe.WriteUnaligned((void*)Read<nint>(frame, arguments[1]), text);
        }
        WriteBool(frame, destination, line is not null);
    }

    private static long OpenTextCursor(HpirWork owner, nint plan, bool file, HpirProgram program, ExecutionOptions? options,
        HpirWorkSite site) => !file ? owner.OpenTextRange(options?.Input ?? Console.In, site)
        : owner.OpenTextRange(Unsafe.ReadUnaligned<HpirPathValue>((void*)plan).ToHost(contextual: true),
            new NativeRuntimeContext(options, program.Context), site);
}
