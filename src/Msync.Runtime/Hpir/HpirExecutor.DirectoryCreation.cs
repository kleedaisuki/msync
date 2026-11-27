using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallCreateDirectories(HpirProgram program, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, int pc, HpirImport import, HpirCall call, byte* frame, int destination,
        ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors)
    {
        var path = PathOperand(frame, call.Arguments[0], import.Binding != 0, anchors).ToHost(contextual: true);
        work ??= new(token);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        WriteBool(frame, destination, work.CreateDirectories(path, options, anchors.Binding(program.Context), site, token));
    }
}
