using System.Collections.Immutable;

namespace Msync.Compiler;

internal sealed class LoopContext(string label, bool isFor, int acquireDepth, int iteratorDepth)
{
    public string Label { get; } = label;
    public bool IsFor { get; } = isFor;
    public int AcquireDepth { get; } = acquireDepth;
    public int IteratorDepth { get; } = iteratorDepth;
    public List<Info> Values { get; } = [];
    public List<ImmutableDictionary<object, Info>> ExitFacts { get; } = [];
}
