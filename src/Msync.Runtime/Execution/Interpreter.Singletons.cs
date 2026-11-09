using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Dictionary<int, ScopeValue>? singletons;

    private Value GetSingleton(int declaration) =>
        singletons is not null && singletons.TryGetValue(declaration, out var value) ? value : Value.Null;

    private ScopeValue SetSingleton(int declaration, Value value)
    {
        if (value is not ScopeValue candidate) throw new MsyncException($"Cannot initialize a named Scope with {value.Type}");
        ref ScopeValue? initialized = ref CollectionsMarshal.GetValueRefOrAddDefault(singletons ??= [], declaration, out bool exists);
        if (!exists) initialized = candidate;
        return initialized!;
    }
}
