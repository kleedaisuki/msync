using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value PathOperation(string name, IReadOnlyList<Value> args, RuntimeContext context) => name switch
    {
        "identity" => context.AsPath(args[0]),
        "join" => PathSemantics.JoinValue(context.AsPath(args[0]), args[1]),
        "qualify" => args[0] is ConnectionValue connection ? PathSemantics.Qualify(context.AsPath(args[1]), connection) : throw new MsyncException("path.qualify expects Connection"),
        _ => throw new MsyncException($"Unknown path native '{name}'")
    };
}
