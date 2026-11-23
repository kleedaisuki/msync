using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallFileTextWrite(HpirProgram program, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, int pc, HpirImport import, IReadOnlyList<int> arguments, byte* frame,
        ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors)
    {
        var (append, textPath, source) = HpirFileTextWriteImports.Decode(import.Binding);
        var path = PathOperand(frame, arguments[0], textPath, anchors).ToHost(contextual: true);
        nint content = Read<nint>(frame, arguments[1]);
        work ??= new(token);
        HpirTransferTextInput input = source switch
        {
            HpirFileTextSource.Completion => BorrowTransferText(work, content),
            HpirFileTextSource.Receipt => HpirFuture.BorrowBoolText(content, work),
            _ => new(CopyText(content))
        };
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        work.WriteFileText(path, input, append, options, anchors.Binding(program.Context), site, token);
    }
}
