namespace Msync.Runtime;

internal readonly record struct ContextualReadWait(Task Completion, bool ProcessFlow);

internal sealed class ContextualObservationBlocked(ContextualReadWait wait) : Exception
{
    internal ContextualReadWait Wait { get; } = wait;
}
