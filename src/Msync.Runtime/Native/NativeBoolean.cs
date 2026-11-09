using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value BooleanOperation(string name, IReadOnlyList<Value> args) => name switch
    {
        "identity" => Bool(Boolean(args[0])),
        "negate" => Bool(!Boolean(args[0])),
        "both" => Bool(Boolean(args[0]) && Boolean(args[1])),
        "either" => Bool(Boolean(args[0]) || Boolean(args[1])),
        "xor" => Bool(Boolean(args[0]) != Boolean(args[1])),
        "implies" => Bool(!Boolean(args[0]) || Boolean(args[1])),
        _ => throw new MsyncException($"Unknown bool native '{name}'")
    };
}
