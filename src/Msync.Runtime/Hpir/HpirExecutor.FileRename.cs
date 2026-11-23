using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallFileRename(HpirProgram program, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, int pc, IReadOnlyList<int> arguments, byte* frame,
        ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors)
    {
        var source = PathOperand(frame, arguments[0], false, anchors).ToHost(contextual: true);
        var destination = PathOperand(frame, arguments[1], false, anchors).ToHost(contextual: true);
        work ??= new(token);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        work.RenameFile(source, destination, options, anchors.Binding(program.Context), site, token);
    }
}
