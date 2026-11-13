using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static NativeValue BooleanOperation(string name, IReadOnlyList<NativeValue> args) => name switch
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
