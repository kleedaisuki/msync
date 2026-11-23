using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    internal static HpirRunOutcome RunSession(HpirProgram program, HpirSessionState state,
        ExecutionOptions? options, CancellationToken token) =>
        RunCore(program, null, ConfiguredMode(), options, token, state);
}
