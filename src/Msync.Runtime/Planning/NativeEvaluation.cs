using Msync.Core;

namespace Msync.Runtime;

internal static class NativeEvaluation
{
    internal static bool DefersFileRead(string? name) => name is "fs.directory_iterator"
        or "fs.recursive_directory_iterator" or "io.read_lines" or "io.read_bytes";

    internal static bool RequiresPathSession(string? name) => name is not null && !DefersFileRead(name)
        && NativeSignatures.TryGet(name, out var signature) && signature.Effect != NativeEffect.Pure
        && (signature.Behavior.GetValueOrDefault() & NativeBehavior.LocalMemory) == 0;
}
