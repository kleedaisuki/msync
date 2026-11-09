using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private async ValueTask<Value> CloseIteratorAsync(Value value)
    {
        if (value is not IteratorValue iterator) throw new MsyncException("Loop cleanup requires an iterator");
        if (!iterators.Contains(iterator.Iterator)) return Value.Null;
        await iterator.Iterator.DisposeAsync();
        iterators.Remove(iterator.Iterator);
        return Value.Null;
    }
}
