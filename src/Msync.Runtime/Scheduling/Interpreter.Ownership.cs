using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private void PromoteReturnedProcesses(Value? result, IReadOnlyList<int>? roots, Value[] locals,
        IReadOnlyList<Value>? captures, IReadOnlyList<Value>? arguments, ref WorkOwner caller, bool hasCaller)
    {
        var owned = currentOwner.Processes;
        if (owned is not { Count: > 0 } && !HasProcessFlows(currentOwner.Tasks)) return;
        HashSet<Value> visited = new(ReferenceEqualityComparer.Instance);
        HashSet<ProcessValue> escaped = new(ReferenceEqualityComparer.Instance);
        if (result is not null) CollectReturnedProcesses(result, visited, escaped);
        if (roots is not null)
            foreach (int slot in roots) CollectReturnedProcesses(locals[slot], visited, escaped);
        if (owned is { Count: > 0 })
        {
            HashSet<ProcessValue> stored = new(ReferenceEqualityComparer.Instance);
            if (captures is not null)
                foreach (var capture in captures) CollectReturnedProcesses(capture, visited, stored);
            if (arguments is not null)
                foreach (var argument in arguments) CollectReturnedProcesses(argument, visited, stored);
            stored.IntersectWith(owned);
            escaped.UnionWith(stored);
        }
        if (escaped.Count == 0) return;

        for (int index = (owned?.Count ?? 0) - 1; index >= 0; index--)
        {
            var process = owned![index];
            if (!escaped.Contains(process)) continue;
            if (hasCaller) (caller.Processes ??= []).Add(process);
            owned.RemoveAt(index);
        }
        if (currentOwner.Tasks is not { Count: > 0 } tasks) return;
        for (int index = tasks.Count - 1; index >= 0; index--)
        {
            var work = tasks[index];
            if (!work.Processes.Any(escaped.Contains)) continue;
            if (hasCaller) (caller.Tasks ??= []).Add(work);
            tasks.RemoveAt(index);
        }
    }

    private void RetireSessionWork()
    {
        sessionProcesses.RemoveWhere(static process => process.Completion?.IsCompletedSuccessfully ?? true);
        for (int index = pending.Count - 1; index >= 0; index--)
        {
            var work = pending[index];
            if (!work.Completion.IsCompletedSuccessfully || !work.Completion.Result) continue;
            Retire(work);
            pending.RemoveAt(index);
        }
    }

    internal async ValueTask CloseSessionAsync()
    {
        try
        {
            foreach (var process in sessionProcesses)
            {
                if (process.Control is { IsRunning: true } control) control.Kill();
                else if (process.Completion?.IsCompleted == false) process.AbortOwnedTransport?.Invoke();
            }
            await DrainAsync();
            foreach (var iterator in iterators) await iterator.DisposeAsync();
            iterators.Clear();
        }
        finally { await DisposePlanResourcesAsync(); }
    }

    private static bool HasProcessFlows(List<PendingWork>? tasks)
    {
        if (tasks is null) return false;
        foreach (var work in tasks)
            if (work.Processes.Count != 0) return true;
        return false;
    }

    private static void CollectReturnedProcesses(Value value, HashSet<Value> visited,
        HashSet<ProcessValue> processes)
    {
        if (!visited.Add(value)) return;
        switch (value)
        {
            case ProcessValue process:
                processes.Add(process);
                break;
            case DeferredValue deferred:
                foreach (var dependency in deferred.Processes) processes.Add(dependency);
                break;
            case CellValue cell:
                CollectReturnedProcesses(cell.Data, visited, processes);
                break;
            case ScopeValue scope:
                foreach (var field in scope) CollectReturnedProcesses(field.Value, visited, processes);
                break;
            case FunctionValue { Captures: { } captures }:
                foreach (var capture in captures) CollectReturnedProcesses(capture, visited, processes);
                break;
            case RangeValue { RetainedValues: { } values }:
                foreach (var item in values) CollectReturnedProcesses(item, visited, processes);
                break;
        }
    }
}
