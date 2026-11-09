using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value MetaOperation(string operation, IReadOnlyList<Value> arguments) =>
        MetaTypes.Evaluate(operation, arguments.Select(argument => ((TypeValue)argument).Data).ToArray());
}
