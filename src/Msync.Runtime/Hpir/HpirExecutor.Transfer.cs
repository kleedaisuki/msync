using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static partial class HpirExecutor
{
    private static void Transfer(HpirProgram program, ref HpirWork? work, int binding, ExecutionOptions? options,
        CancellationToken token, HpirDiagnostics diagnostics, int function, int pc, bool shared)
    {
        HpirPreparedTransfer transfer = program.PreparedTransfers[binding];
        var site = new HpirWorkSite(options?.Timeline, transfer.Source ?? diagnostics.Source(function, pc), function, pc);
        work ??= new(token);
        work.SubmitPreparedTransfer(binding, program.PreparedTransfers, options, program.Context, site, program.Functions[function].Name, shared);
    }
}
