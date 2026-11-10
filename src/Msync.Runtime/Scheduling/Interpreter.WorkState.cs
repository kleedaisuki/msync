using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private struct WorkOwner
    {
        public List<PendingWork>? Tasks;
        public List<ProcessValue>? Processes;
        public Dictionary<string, int>? AfterRegions;
    }
    private sealed record DeferredValue(Task<Value> Completion, TypeRef ResultType, IReadOnlyList<ProcessValue> Processes, string? PathEndpoint = null) : Value
    {
        public override TypeRef Type => ResultType;
    }
    private sealed record PendingWork(Task<bool> Completion, IReadOnlyList<ProcessValue> Processes, bool RequireSuccess)
    {
        public ResourceIndex.Registration? Registration;
        public IReadOnlyList<ResourceIndex.Registration>? Registrations;
    }
}
