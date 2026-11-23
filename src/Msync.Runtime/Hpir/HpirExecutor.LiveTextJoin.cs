using System.Text;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void JoinLiveText(HpirProgram program, ref HpirHeap? heap, HpirWork owner,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts,
        int pc, bool file, nint plan, nint delimiter, byte* frame, int destination,
        ExecutionOptions? options, CancellationToken token, HpirWorkSite site)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.text_range.join");
        trace?.SetAttribute("input.kind", file ? "file" : "stdin");
        try
        {
            long cursor = OpenTextCursor(owner, plan, file, program, options, site);
            ReadOnlySpan<char> separator = HpirTextStorage.Read(delimiter);
            string? first = Pull(owner, cursor, token, site, counts);
            long lines = first is null ? 0 : 1;
            StringBuilder? text = null;
            if (first is not null)
                for (string? line = Pull(owner, cursor, token, site, counts); line is not null;
                    line = Pull(owner, cursor, token, site, counts))
                {
                    (text ??= new StringBuilder().Append(first)).Append(separator).Append(line);
                    ++lines;
                }
            owner.CloseTextRange(cursor, site);

            int length = text?.Length ?? first?.Length ?? 0;
            var allocation = new TextAllocation(program, ref heap, active, diagnostics, counts, pc);
            nint result = allocation.Reserve(length, out Span<char> output);
            if (text is null) first.AsSpan().CopyTo(output);
            else text.CopyTo(0, output, length);
            counts?.StringJoin(lines, length);
            trace?.SetMetric("range.lines", lines);
            trace?.SetMetric("text.output_code_units", length);
            Write(frame, destination, result);
        }
        catch (Exception error)
        {
            // Keep a failed cursor owned by execution unwind so close errors
            // cannot replace the originating read or cancellation failure.
            trace?.Fail(error);
            throw;
        }
    }

    private static string? Pull(HpirWork owner, long id, CancellationToken token, HpirWorkSite site,
        RuntimeProfile.HpirFunctionCensus? counts)
    {
        string? line = owner.PullTextRange(id, token, site);
        counts?.TextRangePull(line);
        return line;
    }
}
