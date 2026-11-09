using Msync.Core;

namespace Msync.Compiler.Semantics;

internal static class ProcessCapabilities
{
    internal static readonly IReadOnlyDictionary<string, TypeRef> Members = new[] { "stdout", "stderr", "exit_code", "success" }
        .ToDictionary(name => name, name => NativeSignatures.Get("process." + name).Result, StringComparer.Ordinal);
}
